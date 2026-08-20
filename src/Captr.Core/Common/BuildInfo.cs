using System.Reflection;
using System.Text.Json;

namespace Captr.Core.Common;

/// <summary>
/// Answers "exactly which build is this?" from a RUNNING installation (SPEC §11).
/// Owns the identity a support conversation starts from: application version and
/// source commit, plus the exact bundled FFmpeg build — the two things that decide
/// whether a reported bug is even reproducible.
/// </summary>
public sealed record BuildInfo
{
    /// <summary>Semantic version, e.g. <c>0.1.0</c>.</summary>
    public required string Version { get; init; }

    /// <summary>Informational version including the source commit, e.g.
    /// <c>0.1.0+a1b2c3d4e5f6</c> (SPEC §11).</summary>
    public required string InformationalVersion { get; init; }

    /// <summary>Source commit alone, or "unknown" when built outside a git tree.</summary>
    public required string Commit { get; init; }

    /// <summary>The exact upstream FFmpeg build identifier that ships beside us.</summary>
    public required string FfmpegBuildId { get; init; }

    /// <summary>.NET runtime the process is running on.</summary>
    public required string RuntimeVersion { get; init; }

    /// <summary>Reads the identity of the currently running installation.</summary>
    public static BuildInfo Current()
    {
        Assembly assembly = typeof(BuildInfo).Assembly;
        string informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        // The SDK writes "1.2.3+commit"; split rather than parse.
        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        string version = plus > 0 ? informational[..plus] : informational;
        string commit = plus > 0 ? informational[(plus + 1)..] : "unknown";

        return new BuildInfo
        {
            Version = version,
            InformationalVersion = informational,
            Commit = commit,
            FfmpegBuildId = ReadFfmpegBuildId(),
            RuntimeVersion = Environment.Version.ToString(),
        };
    }

    /// <summary>A compact human-readable identity, one line per fact.</summary>
    public string ToDisplayText() =>
        $"""
         Captr {Version}
         Commit:  {Commit}
         FFmpeg:  {FfmpegBuildId}
         .NET:    {RuntimeVersion}
         """;

    /// <summary>Reads the build id the fetch script recorded beside the binary. A
    /// missing file is reported honestly rather than guessed at.</summary>
    private static string ReadFfmpegBuildId()
    {
        try
        {
            string capabilities = Path.Combine(
                Path.GetDirectoryName(Supervision.FfmpegLocator.FindFfmpeg())!, "capabilities.json");
            if (!File.Exists(capabilities))
            {
                capabilities = Path.Combine(
                    Path.GetDirectoryName(Supervision.FfmpegLocator.FindFfmpeg())!, "..", "capabilities.json");
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(capabilities));
            return document.RootElement.GetProperty("buildId").GetString() ?? "unknown";
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException)
        {
            return "unknown";
        }
    }
}
