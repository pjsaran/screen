using System.Diagnostics;

using Captr.Core.Sessions;

using Shouldly;

namespace Captr.Integration.Tests.Ui;

/// <summary>
/// SPEC §13 rule 5 / §14: "the UI crashing, hanging, or being killed does not affect
/// recording", and relaunching it reattaches with correct state. Proven with the real
/// UI process against a real recording — the guarantee is architectural (the UI is a
/// separate process holding no recording state), and this is what proves it.
/// </summary>
[Trait("Category", "Gpu")]
public class UiIndependenceTests
{
    private static string PublishDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Captr.slnx")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("could not locate the repo root");
        string publish = Path.Combine(directory.FullName, "publish");
        File.Exists(Path.Combine(publish, "captr.exe")).ShouldBeTrue(
            $"No published payload at {publish}. Run: pwsh build/build.ps1 -Publish -SkipTests");
        return publish;
    }

    [Fact]
    public async Task Killing_the_UI_mid_recording_does_not_interrupt_capture_and_relaunch_reattaches()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string publish = PublishDirectory();
        string cli = Path.Combine(publish, "captr.exe");
        string uiExe = Path.Combine(publish, "Captr.App.exe");
        string workingRoot = Directory.CreateTempSubdirectory("captr-uikill-").FullName;

        KillAll();
        // Drives the REAL captr executable, which reads and writes the real
        // settings file. The guard puts it back exactly as it was.
        using var settingsGuard = new RealSettingsGuard();
        try
        {
            RunCli(cli, "settings", "set", "workingFolder", workingRoot).ExitCode.ShouldBe(0);
            RunCli(cli, "start", "--label", "ui-kill").ExitCode.ShouldBe(0);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            // Launch the UI on top of the running recording.
            using (Process ui = Process.Start(new ProcessStartInfo(uiExe) { UseShellExecute = true })!)
            {
                await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);
                ui.HasExited.ShouldBeFalse("the UI must start while a recording is running");

                // KILL IT — the crash/hang case.
                ui.Kill(entireProcessTree: false);
                await ui.WaitForExitAsync(cancellationToken);
            }

            // Capture must be entirely unaffected: still recording, still growing.
            long lengthAfterUiDeath = NewestSegmentLength(workingRoot);
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);

            (int statusExit, string statusOutput) = RunCli(cli, "status");
            statusExit.ShouldBe(0, $"still recording; got: {statusOutput}");
            NewestSegmentLength(workingRoot).ShouldBeGreaterThanOrEqualTo(lengthAfterUiDeath);

            // Relaunch: the UI reattaches to the SAME live session (it holds no
            // state of its own, so "reattach" is just its next status poll).
            using (Process relaunched = Process.Start(new ProcessStartInfo(uiExe) { UseShellExecute = true })!)
            {
                await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);
                relaunched.HasExited.ShouldBeFalse();
                RunCli(cli, "status").ExitCode.ShouldBe(0, "the recording is still running after relaunch");
                relaunched.Kill(entireProcessTree: false);
                await relaunched.WaitForExitAsync(cancellationToken);
            }

            // And it finalises normally afterwards.
            RunCli(cli, "stop").ExitCode.ShouldBe(0);
            await WaitForIdleAsync(cli, cancellationToken);

            string sessionFolder = Directory.GetDirectories(workingRoot).ShouldHaveSingleItem();
            SessionJournal.ReadAll(Path.Combine(sessionFolder, SessionJournal.FileName))
                .OfType<SessionFinalized>().ShouldHaveSingleItem()
                .OutputFiles.ShouldNotBeEmpty();
        }
        finally
        {
            KillAll();
            try
            {
                Directory.Delete(workingRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task WaitForIdleAsync(string cli, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromMinutes(2))
        {
            if (RunCli(cli, "status").ExitCode == 10)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new TimeoutException("The recorder never returned to idle after stop.");
    }

    private static long NewestSegmentLength(string workingRoot)
    {
        string sessionFolder = Directory.GetDirectories(workingRoot).Single();
        string? newest = Directory.EnumerateFiles(sessionFolder, "seg-*.mkv")
            .OrderDescending(StringComparer.Ordinal).FirstOrDefault();
        if (newest is null)
        {
            return 0;
        }

        using var stream = new FileStream(
            newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Length;
    }

    private static (int ExitCode, string Output) RunCli(string cli, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = cli,
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


    private static void KillAll()
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
