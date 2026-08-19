using Captr.Core.Secrets;

using Serilog;
using Serilog.Events;

using Shouldly;

namespace Captr.Core.Tests.Secrets;

/// <summary>
/// Log redaction (SPEC §14): serialise a fully-populated credential-bearing object
/// through the logging pipeline and assert no secret material appears. The policy
/// is registered at logger construction, exactly as production does it.
/// </summary>
public class SecretRedactionTests
{
    private const string PlantedSecret = "SuperSecret-Hunter2-0xDEADBEEF";

    private sealed record SharePointCredentialConfig(
        string DestinationName,
        string TenantId,
        string ClientId,
        string ClientSecret,
        string Password,
        string RefreshToken,
        string ApiKey);

    private static (ILogger Logger, List<LogEvent> Events) MakeCapturingLogger()
    {
        var events = new List<LogEvent>();
        ILogger logger = new LoggerConfiguration()
            .WithSecretRedaction()
            .WriteTo.Sink(new ListSink(events))
            .CreateLogger();
        return (logger, events);
    }

    private static SharePointCredentialConfig FullyPopulated() => new(
        "sp-archive", "tenant-123", "client-456",
        PlantedSecret, PlantedSecret, PlantedSecret, PlantedSecret);

    [Fact]
    public void A_destructured_credential_object_logs_with_every_secret_field_masked()
    {
        (ILogger logger, List<LogEvent> events) = MakeCapturingLogger();

        logger.Information("Configured destination {@Config}", FullyPopulated());

        string rendered = Render(events.Single());
        rendered.ShouldNotContain(PlantedSecret);
        rendered.ShouldContain(SecretRedaction.Mask);
        // Non-secret fields survive — redaction is surgical, not censorship.
        rendered.ShouldContain("sp-archive");
        rendered.ShouldContain("tenant-123");
    }

    [Fact]
    public void A_top_level_property_with_a_suspect_name_is_masked()
    {
        (ILogger logger, List<LogEvent> events) = MakeCapturingLogger();

        logger.Information("Auth with {Password} for {User}", PlantedSecret, "psaran");

        string rendered = Render(events.Single());
        rendered.ShouldNotContain(PlantedSecret);
        rendered.ShouldContain("psaran");
    }

    [Theory]
    [InlineData("ClientSecret")]
    [InlineData("password")]
    [InlineData("AccessToken")]
    [InlineData("apiKey")]
    [InlineData("Authorization")]
    [InlineData("credentialBlob")]
    public void Suspect_names_are_recognised(string name)
    {
        SecretRedaction.IsSuspectName(name).ShouldBeTrue();
    }

    [Theory]
    [InlineData("FrameRate")]
    [InlineData("WorkingFolder")]
    [InlineData("DestinationName")]
    public void Ordinary_names_are_not_redacted(string name)
    {
        SecretRedaction.IsSuspectName(name).ShouldBeFalse();
    }

    private static string Render(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        logEvent.RenderMessage(writer, System.Globalization.CultureInfo.InvariantCulture);
        foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties)
        {
            writer.Write(' ');
            writer.Write(property.Key);
            writer.Write('=');
            property.Value.Render(writer, null, System.Globalization.CultureInfo.InvariantCulture);
        }

        return writer.ToString();
    }

    private sealed class ListSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }
}
