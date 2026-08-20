using System.Diagnostics;

using Captr.Core.Sessions;

using Shouldly;

namespace Captr.Integration.Tests.Cli;

/// <summary>
/// SPEC §8's settings lock, end to end through the real CLI and host: while a
/// recording runs, capture and quality settings accept only DEGRADING changes, and
/// an accepted one rolls a new segment and is journaled.
/// </summary>
[Trait("Category", "Gpu")]
public class SettingsLockTests
{
    private static string Cli()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Captr.slnx")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("could not locate the repo root");
        string cli = Path.Combine(directory.FullName, "publish", "captr.exe");
        File.Exists(cli).ShouldBeTrue($"No published payload at {cli}. Run: pwsh build/build.ps1 -Publish -SkipTests");
        return cli;
    }

    private static (int ExitCode, string Output) Run(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Cli(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(120_000).ShouldBeTrue($"'captr {string.Join(' ', arguments)}' did not return");
        return (process.ExitCode, output);
    }

    [Fact]
    public async Task While_recording_a_frame_rate_increase_is_refused_and_a_decrease_is_applied_and_journaled()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string workingRoot = Directory.CreateTempSubdirectory("captr-lock-").FullName;
        KillStrayHosts();
        string? saved = BackUpSettings();

        try
        {
            Run("settings", "set", "workingFolder", workingRoot).ExitCode.ShouldBe(0);
            Run("settings", "set", "frameRate", "15").ExitCode.ShouldBe(0);
            Run("start", "--label", "lock-test").ExitCode.ShouldBe(0);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            // Raising the frame rate mid-recording: refused, with a reason.
            (int raiseExit, string raiseOutput) = Run("settings", "set", "frameRate", "30");
            raiseExit.ShouldBe(Captr.Core.Cli.ExitCodes.Error);
            raiseOutput.ShouldContain("Stop the recording first");

            // Lowering it: accepted, applied live, journaled.
            (int lowerExit, string lowerOutput) = Run("settings", "set", "frameRate", "10");
            lowerExit.ShouldBe(0);
            lowerOutput.ShouldContain("rolled a new segment");

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            Run("stop").ExitCode.ShouldBe(0);
            await WaitForIdleAsync(cancellationToken);

            string sessionFolder = Directory.GetDirectories(workingRoot).ShouldHaveSingleItem();
            IReadOnlyList<JournalEvent> events = SessionJournal.ReadAll(
                Path.Combine(sessionFolder, SessionJournal.FileName));

            FrameRateReduced reduction = events.OfType<FrameRateReduced>().ShouldHaveSingleItem();
            reduction.FromFps.ShouldBe(15);
            reduction.ToFps.ShouldBe(10);
            reduction.Reason.ShouldContain("user");

            // The change opened a new arrangement group, because segments either
            // side of a frame-rate change are not join-compatible.
            events.OfType<SegmentOpened>().Select(s => s.ArrangementGroup).Distinct().Count()
                .ShouldBeGreaterThan(1);
        }
        finally
        {
            KillStrayHosts();
            RestoreSettings(saved);
            try
            {
                Directory.Delete(workingRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromMinutes(2))
        {
            if (Run("status").ExitCode == 10)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new TimeoutException("The recorder never returned to idle after stop.");
    }

    private static string SettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Captr", "settings.json");

    private static string? BackUpSettings() =>
        File.Exists(SettingsPath()) ? File.ReadAllText(SettingsPath()) : null;

    private static void RestoreSettings(string? saved)
    {
        if (saved is not null)
        {
            File.WriteAllText(SettingsPath(), saved);
        }
        else if (File.Exists(SettingsPath()))
        {
            File.Delete(SettingsPath());
        }
    }

    private static void KillStrayHosts()
    {
        foreach (Process process in Process.GetProcessesByName("Captr.App"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
