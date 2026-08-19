using System.Text.RegularExpressions;

using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Captr.Core.Secrets;

/// <summary>
/// Enforced log redaction (SPEC §7: "enforce redaction rather than relying on
/// discipline"). A Serilog policy that scrubs every property whose NAME suggests a
/// secret, registered once at logger construction (SPEC §12) — never at call
/// sites. If someone logs a credential-bearing object, the secret fields come out
/// as [REDACTED] instead of leaking.
/// </summary>
public static partial class SecretRedaction
{
    /// <summary>Replaces suspect property values in every structured log event.</summary>
    public const string Mask = "[REDACTED]";

    [GeneratedRegex("secret|password|token|key|credential|authorization|clientsecret|pwd",
        RegexOptions.IgnoreCase)]
    private static partial Regex SuspectName();

    /// <summary>True when a property with this name must never be logged verbatim.</summary>
    public static bool IsSuspectName(string propertyName) => SuspectName().IsMatch(propertyName);

    /// <summary>Registers the policy on a logger configuration. Call this wherever a
    /// logger is BUILT — the redaction lives in the pipeline, not in call sites.</summary>
    public static LoggerConfiguration WithSecretRedaction(this LoggerConfiguration configuration) =>
        configuration
            .Destructure.With<RedactingDestructuringPolicy>()
            .Enrich.With<RedactingEnricher>();

    /// <summary>Redacts suspect properties when whole objects are destructured
    /// (<c>{@Config}</c>-style logging).</summary>
    private sealed class RedactingDestructuringPolicy : IDestructuringPolicy
    {
        public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, out LogEventPropertyValue result)
        {
            // Only intervene for plain data objects; scalars and collections pass through.
            Type type = value.GetType();
            if (type.IsPrimitive || value is string || type.IsEnum || value is System.Collections.IEnumerable)
            {
                result = null!;
                return false;
            }

            var properties = new List<LogEventProperty>();
            foreach (System.Reflection.PropertyInfo property in type.GetProperties())
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                object? propertyValue;
                try
                {
                    propertyValue = property.GetValue(value);
                }
                catch (Exception exception) when (exception is System.Reflection.TargetInvocationException or NotSupportedException)
                {
                    continue;
                }

                properties.Add(new LogEventProperty(
                    property.Name,
                    IsSuspectName(property.Name)
                        ? new ScalarValue(Mask)
                        : propertyValueFactory.CreatePropertyValue(propertyValue, destructureObjects: true)));
            }

            result = new StructureValue(properties, type.Name);
            return true;
        }
    }

    /// <summary>Redacts suspect TOP-LEVEL properties (<c>{Password}</c>-style logging).</summary>
    private sealed class RedactingEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties.ToList())
            {
                if (IsSuspectName(property.Key) && property.Value is not ScalarValue { Value: Mask })
                {
                    logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, new ScalarValue(Mask)));
                }
            }
        }
    }
}
