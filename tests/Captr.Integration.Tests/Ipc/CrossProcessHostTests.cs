using System.Diagnostics;

using Captr.Core.Ipc;

using Shouldly;

namespace Captr.Integration.Tests.Ipc;

/// <summary>
/// The real host process across a real process boundary (SPEC §14: "a recording
/// started by one invocation is stopped by another … in a separate process";
/// "status round-trips with no UI running"). Trait Gpu: starting a recording needs
/// the real desktop and encoder.
/// </summary>
[Trait("Category", "Gpu")]
public class CrossProcessHostTests
{
    private static string HostExePath()
    {
        // The test runs from tests/…/bin/…; the host exe lives in src/Captr.App's.
        string configuration = AppContext.BaseDirectory.Contains("Release") ? "Release" : "Debug";
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Captr.slnx")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("could not locate the repo root from the test directory");
        string exe = Path.Combine(dir.FullName, "src", "Captr.App", "bin", configuration,
            "net10.0-windows10.0.19041.0", "Captr.App.exe");
        File.Exists(exe).ShouldBeTrue($"host exe not built at {exe}");
        return exe;
    }

    [Fact]
    public async Task A_recording_started_by_one_connection_is_stopped_by_another()
    {
        KillStrayHosts();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // "One invocation": summon the host and start recording.
        await using (IpcClient? starter = await IpcClient.ConnectAsync(
            "test-a", startHostIfNeeded: true, HostExePath(), cancellationToken))
        {
            starter.ShouldNotBeNull();
            StartResponse started = await starter.RequestAsync<StartResponse>(
                IpcKinds.Start, new StartRequest(10, null, "xproc"), cancellationToken);
            started.AlreadyRecording.ShouldBeFalse(started.Message);
        }

        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

        // "Another invocation, later, separate connection": starting again is the
        // idempotent success SPEC §10 requires, and stopping works.
        await using (IpcClient? stopper = await IpcClient.ConnectAsync(
            "test-b", startHostIfNeeded: false, null, cancellationToken))
        {
            stopper.ShouldNotBeNull("the host must still be running");

            StartResponse startAgain = await stopper.RequestAsync<StartResponse>(
                IpcKinds.Start, new StartRequest(null, null, null), cancellationToken);
            startAgain.AlreadyRecording.ShouldBeTrue();

            StatusResponse status = await stopper.RequestAsync<StatusResponse>(IpcKinds.Status, null, cancellationToken);
            status.State.ShouldBe("recording");
            status.Encoder.ShouldNotBeNull();

            StopResponse stopped = await stopper.RequestAsync<StopResponse>(IpcKinds.Stop, null, cancellationToken);
            stopped.WasRecording.ShouldBeTrue();
        }

        // Finalisation runs in the background; poll until the host reports idle.
        await WaitForIdleAsync(cancellationToken);

        // The finalised recording is listed.
        await using IpcClient? lister = await IpcClient.ConnectAsync("test-c", false, null, cancellationToken);
        lister.ShouldNotBeNull();
        ListRecordingsResponse list = await lister.RequestAsync<ListRecordingsResponse>(
            IpcKinds.ListRecordings, null, cancellationToken);
        list.Recordings.ShouldContain(r => r.Finalized && r.RecordedSpan > TimeSpan.FromSeconds(2));

        KillStrayHosts();
    }

    private static async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(60))
        {
            await using IpcClient? client = await IpcClient.ConnectAsync("test-poll", false, null, cancellationToken);
            if (client is null)
            {
                return; // Host exited — certainly idle.
            }

            StatusResponse status = await client.RequestAsync<StatusResponse>(IpcKinds.Status, null, cancellationToken);
            if (status.State == "idle")
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException("The host never returned to idle after stop.");
    }

    /// <summary>The host is single-instance per user; stray hosts from earlier runs
    /// would shadow this test's.</summary>
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
