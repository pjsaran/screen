using System.Diagnostics;

using Shouldly;

namespace Captr.Integration.Tests.Cli;

/// <summary>
/// The command line must behave when its output is CAPTURED, not just when a human
/// watches it in a console (SPEC §10: scheduled and automated recording is a primary
/// use case, and schedulers capture output).
/// </summary>
/// <remarks>
/// Regression guard for a real bug: the CLI spawned the recording host with
/// inherited stdout/stderr handles, so the long-lived host held the redirection
/// pipe open and `captr start` never returned to a script — it hung for the entire
/// recording. Interactively it looked perfect. Trait Gpu because starting a
/// recording needs the real desktop and encoder.
/// </remarks>
[Trait("Category", "Gpu")]
public class CliRedirectionTests
{
    /// <summary>
    /// Runs against the PUBLISHED payload, not the dev build: the CLI finds the
    /// host beside itself, and only publish/ has the shipping layout where
    /// captr.exe and Captr.App.exe share one folder. Testing the shipped layout is
    /// also the point — the bug this guards was invisible until then.
    /// </summary>
    private static string CliPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Captr.slnx")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("could not locate the repo root");
        string cli = Path.Combine(directory.FullName, "publish", "captr.exe");
        File.Exists(cli).ShouldBeTrue(
            $"No published payload at {cli}. Run: pwsh build/build.ps1 -Publish -SkipTests");
        return cli;
    }

    /// <summary>Runs the CLI with output redirected, failing if it does not return
    /// promptly — the exact shape a scheduled task uses.</summary>
    private static async Task<(int ExitCode, string Output)> RunCapturedAsync(
        string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = CliPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;

        // Read to end: this is what hung. If any process still holds the write end
        // of these pipes, these tasks never complete.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: false);
            throw new TimeoutException(
                $"'captr {arguments}' did not return within {timeout.TotalSeconds:F0}s with output redirected — " +
                "the spawned host is holding the redirection pipe open.");
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    [Fact]
    public async Task Start_returns_promptly_when_its_output_is_captured_then_stop_and_status_do_too()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        KillStrayHosts();

        try
        {
            // start SUMMONS a host — the case that hung. 60 s is generous: the host
            // has to boot, run its recovery scan, prove an encoder by trial encode,
            // and measure the disk rate before it answers.
            (int startExit, string startOutput) = await RunCapturedAsync(
                "start --label redirect-test", TimeSpan.FromSeconds(60), cancellationToken);
            startExit.ShouldBe(0, startOutput);
            startOutput.ShouldNotBeEmpty();

            // A second start is the scheduler double-fire: still prompt, still 0.
            (int secondExit, _) = await RunCapturedAsync(
                "start", TimeSpan.FromSeconds(30), cancellationToken);
            secondExit.ShouldBe(0);

            (int statusExit, string statusOutput) = await RunCapturedAsync(
                "status", TimeSpan.FromSeconds(30), cancellationToken);
            statusExit.ShouldBe(0, "0 = recording");
            statusOutput.ShouldContain("recording");

            (int stopExit, _) = await RunCapturedAsync("stop", TimeSpan.FromSeconds(30), cancellationToken);
            stopExit.ShouldBe(0);
        }
        finally
        {
            try
            {
                await RunCapturedAsync("stop", TimeSpan.FromSeconds(30), CancellationToken.None);
            }
            catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
            {
            }

            KillStrayHosts();
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
