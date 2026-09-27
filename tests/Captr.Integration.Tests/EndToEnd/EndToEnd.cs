using System.Diagnostics;

using Captr.Core.Common;

using Windows.Win32;

namespace Captr.Integration.Tests.EndToEnd;

/// <summary>One run of the real `captr` executable.</summary>
internal sealed record CliRun(int ExitCode, string Stdout, string Stderr)
{
    public override string ToString() => $"exit {ExitCode}\nstdout: {Stdout}\nstderr: {Stderr}";
}

/// <summary>
/// Driving the real product from the end-to-end suites: the CLI with a data root of
/// its own (<see cref="CaptrPaths.DataRootVariable"/>), cleanup that only ever touches
/// what the suite itself started, and copies of the payload that can be broken on
/// purpose without breaking the payload.
/// </summary>
internal static class EndToEnd
{
    public static async Task<CliRun> RunCliAsync(
        string cli, IEnumerable<string> arguments, string? dataRoot, string? stdin = null,
        IDictionary<string, string>? environment = null, TimeSpan? timeout = null, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? "",
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (dataRoot is not null)
        {
            startInfo.Environment[CaptrPaths.DataRootVariable] = dataRoot;
        }

        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        using Process process = Process.Start(startInfo)!;
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'captr {string.Join(' ', startInfo.ArgumentList)}' did not return.");
        }

        return new CliRun(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// Waits until <c>status</c> reports idle (exit 10) — the recording is stopped AND
    /// saved — or fails after the deadline.
    /// </summary>
    public static async Task WaitUntilIdleAsync(string cli, string dataRoot, TimeSpan deadline)
    {
        DateTime giveUp = DateTime.UtcNow + deadline;
        CliRun last;
        do
        {
            last = await RunCliAsync(cli, ["status", "--json"], dataRoot);
            if (last.ExitCode == Captr.Core.Cli.ExitCodes.Idle)
            {
                return;
            }

            await Task.Delay(1000);
        }
        while (DateTime.UtcNow < giveUp);

        throw new TimeoutException("The recorder did not return to idle: " + last);
    }

    /// <summary>
    /// Ends recorder processes this suite started — those launched since the test
    /// began from a folder the suite controls — and nothing else: a Captr the
    /// developer is running from Program Files is not ours to end.
    /// </summary>
    public static void KillHostsStartedSince(DateTime startedUtc, params string[] extraFolders)
    {
        List<string> ours = [.. extraFolders.Select(Path.GetFullPath)];
        try
        {
            ours.Add(Path.GetFullPath(PublishedPayload.Directory()));
        }
        catch (Exception exception) when (exception is Shouldly.ShouldAssertException)
        {
            // No publish folder: then only the extra folders are ours.
        }

        foreach (Process process in Process.GetProcessesByName("Captr.App").Concat(Process.GetProcessesByName("ffmpeg")))
        {
            using (process)
            {
                try
                {
                    string? image = process.MainModule?.FileName;
                    if (image is not null
                        && process.StartTime.ToUniversalTime() >= startedUtc.AddSeconds(-1)
                        && ours.Any(folder => image.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(10_000);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited meanwhile, or not ours to inspect.
                }
            }
        }
    }

    /// <summary>
    /// A copy of the published payload made of HARD LINKS — instant, and costing no
    /// disk — except the files named in <paramref name="except"/> (left out) and
    /// <paramref name="copyInstead"/> (real copies, so a test may alter them without
    /// altering the payload).
    /// </summary>
    public static string LinkedCopyOfPayload(string destination, string[]? except = null, string[]? copyInstead = null)
    {
        string source = PublishedPayload.Directory();
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (except?.Any(skip => relative.StartsWith(skip, StringComparison.OrdinalIgnoreCase)) == true)
            {
                continue;
            }

            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (copyInstead?.Contains(relative, StringComparer.OrdinalIgnoreCase) == true || !PInvoke.CreateHardLink(target, file))
            {
                File.Copy(file, target);
            }
        }

        return destination;
    }

    public static void DeleteQuietly(string folder)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(1000);
            }
        }
    }
}
