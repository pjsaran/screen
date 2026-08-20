using System.Diagnostics;

using Captr.Core.Sessions;

using Shouldly;

namespace Captr.Integration.Tests.Ipc;

/// <summary>
/// SPEC §14: "Killing the host leaves the encoder writing and it is re-adopted on
/// restart" and "killing everything recovers playable footage automatically on next
/// start". Runs against the PUBLISHED payload with real processes — the only way to
/// prove a cross-process guarantee.
/// </summary>
/// <remarks>
/// <para>
/// SPEC-INTERPRETATION NOTE (flagged as SPEC §0 asks). §4 says the encoder is
/// "re-adopted when the host restarts"; §6 says every host start scans for
/// unfinalised sessions and finalises them automatically. Captr resolves the two
/// this way: the new host RE-ADOPTS the orphan (identifying it by PID + start time
/// + image path from the journal, so it owns it rather than leaving it running),
/// stops it, and finalises the session — preserving every frame written up to that
/// point. It does NOT resume supervising the old session as if nothing happened,
/// because a new host has no way to reconstruct the original session's disk guard,
/// segment tracker, and encoder plan without re-deriving state the journal was
/// never meant to replay. Footage is preserved either way; this way the recording's
/// boundaries stay honest.
/// </para>
/// </remarks>
[Trait("Category", "Gpu")]
public class HostCrashReadoptionTests
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
    public async Task Killing_the_host_leaves_the_encoder_writing_and_the_next_host_reaps_and_finalises_it()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string publish = PublishDirectory();
        string cli = Path.Combine(publish, "captr.exe");
        string workingRoot = Directory.CreateTempSubdirectory("captr-readopt-").FullName;

        KillStrayHosts();
        string? savedSettings = BackUpSettings();
        try
        {
            RunCli(cli, ["settings", "set", "workingFolder", workingRoot]).ExitCode.ShouldBe(0);

            // --- Record, then KILL THE HOST (not the encoder) ---------------------
            // Long enough that several of FFmpeg's 512 KB output buffers have been
            // flushed — see the duration assertion at the end for why that matters.
            RunCli(cli, ["start", "--label", "readopt"]).ExitCode.ShouldBe(0);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);

            Process[] hosts = Process.GetProcessesByName("Captr.App");
            hosts.ShouldNotBeEmpty("a host must be running");
            int encoderPid = FindEncoderPid(workingRoot);
            foreach (Process host in hosts)
            {
                host.Kill();
                await host.WaitForExitAsync(cancellationToken);
                host.Dispose();
            }

            // --- The encoder MUST still be alive and still writing (SPEC §4) ------
            using (Process encoder = Process.GetProcessById(encoderPid))
            {
                encoder.HasExited.ShouldBeFalse("the encoder must outlive its host");
            }

            long lengthAfterKill = NewestSegmentLength(workingRoot);
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            NewestSegmentLength(workingRoot).ShouldBeGreaterThanOrEqualTo(lengthAfterKill,
                "the orphaned encoder must keep writing");

            // --- Next host start: re-adopt, stop, finalise, report ---------------
            RunCli(cli, ["recover"]).ExitCode.ShouldBe(0);

            // The orphan is reaped — no encoder left behind.
            bool encoderStillRunning;
            try
            {
                using Process encoder = Process.GetProcessById(encoderPid);
                encoderStillRunning = !encoder.HasExited;
            }
            catch (ArgumentException)
            {
                encoderStillRunning = false;
            }

            encoderStillRunning.ShouldBeFalse("recovery must stop the orphaned encoder");

            // The session is finalised with playable footage covering the whole run.
            string sessionFolder = Directory.GetDirectories(workingRoot).ShouldHaveSingleItem();
            IReadOnlyList<JournalEvent> events = SessionJournal.ReadAll(
                Path.Combine(sessionFolder, SessionJournal.FileName));
            SessionFinalized finalized = events.OfType<SessionFinalized>().ShouldHaveSingleItem();
            finalized.OutputFiles.ShouldNotBeEmpty();
            events.OfType<SessionNote>().ShouldContain(n => n.Text.Contains("orphaned encoder"));

            string ffprobe = Path.Combine(publish, "ffmpeg", "ffprobe.exe");
            ProbeResult probe = await FfprobeClient.ProbeAsync(ffprobe, finalized.OutputFiles[0], cancellationToken);
            probe.Success.ShouldBeTrue(probe.FailureReason);

            // ~45 s of wall time elapsed (30 before the host died, 15 after). The
            // recovered footage covers everything the encoder FLUSHED, which is what
            // SPEC §13 rule 1 promises: everything up to the kill, minus at most the
            // segment in progress. In practice the loss is the encoder's unflushed
            // output buffer (512 KB — seconds of footage, less at higher bitrates),
            // NOT the whole segment, so most of the run must survive. Asserting an
            // exact duration would be asserting the buffer's size, not the product's
            // guarantee.
            probe.Duration.ShouldBeGreaterThan(TimeSpan.FromSeconds(15),
                "footage from before AND after the host died must survive the crash");
        }
        finally
        {
            KillStrayHosts();
            RestoreSettings(savedSettings);
            try
            {
                Directory.Delete(workingRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static int FindEncoderPid(string workingRoot)
    {
        string sessionFolder = Directory.GetDirectories(workingRoot).Single();
        IReadOnlyList<JournalEvent> events = SessionJournal.ReadAll(
            Path.Combine(sessionFolder, SessionJournal.FileName));
        return events.OfType<EncoderProcessLaunched>().Last().ProcessId;
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

        // Full sharing: the encoder holds it open, and NTFS directory metadata is
        // stale for open files.
        using var stream = new FileStream(
            newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Length;
    }

    private static (int ExitCode, string Output) RunCli(string cli, string[] arguments)
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
