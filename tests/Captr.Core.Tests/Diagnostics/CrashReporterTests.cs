using Captr.Core.Diagnostics;

using Shouldly;

namespace Captr.Core.Tests.Diagnostics;

/// <summary>
/// An error nothing else handled leaves a useful local report behind, with no
/// secret in it, and a crash loop cannot fill the disk with them.
/// </summary>
public sealed class CrashReporterTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("captr-crash-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static InvalidOperationException Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    [Fact]
    public void A_report_names_the_role_version_and_exception_with_its_stack()
    {
        string? path = CrashReporter.Write(_folder, "ui", Thrown("the button broke"), "The process continued.", DateTimeOffset.UtcNow);

        path.ShouldNotBeNull();
        Path.GetFileName(path).ShouldStartWith("crash-ui-");
        string report = File.ReadAllText(path);
        report.ShouldContain("Captr crash report — ui");
        report.ShouldContain("Version:");
        report.ShouldContain("the button broke");
        report.ShouldContain(nameof(Thrown), customMessage: "the stack trace is what makes the report useful");
    }

    [Fact]
    public void A_secret_in_the_exception_is_masked()
    {
        string path = CrashReporter.Write(_folder, "host",
            Thrown("request failed: client_secret=Sup3rS3cret!"), "The process ended.", DateTimeOffset.UtcNow)!;

        File.ReadAllText(path).ShouldNotContain("Sup3rS3cret!");
    }

    [Fact]
    public void Only_the_newest_reports_are_kept()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        for (int i = 0; i < CrashReporter.KeepNewest + 5; i++)
        {
            CrashReporter.Write(_folder, "host", Thrown($"crash {i}"), "The process ended.", start.AddSeconds(i));
        }

        Directory.GetFiles(_folder, "crash-*.txt").Length.ShouldBe(CrashReporter.KeepNewest);
    }
}
