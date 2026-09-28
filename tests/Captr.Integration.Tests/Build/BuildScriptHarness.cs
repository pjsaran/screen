using System.Diagnostics;
using System.Text.RegularExpressions;

using Shouldly;

namespace Captr.Integration.Tests.Build;

/// <summary>
/// Runs the repository's PowerShell build scripts the way a developer or CI does —
/// `pwsh -NoProfile -File`, output captured — so their behaviour can be asserted
/// like any other code's.
/// </summary>
internal static partial class BuildScriptHarness
{
    /// <summary>The repository root, found by walking up to <c>Captr.slnx</c>.</summary>
    public static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Captr.slnx")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("could not locate the repo root");
        return directory.FullName;
    }

    public static string Script(string relativePath) => Path.Combine(RepoRoot(), relativePath);

    /// <summary>Runs a script and returns its exit code and combined output.</summary>
    /// <param name="environment">Variables to set for the child; a null value
    /// REMOVES the variable, so a test can prove a script works without one.</param>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string scriptPath, IEnumerable<string> arguments, IDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string name, string? value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        using Process process = Process.Start(startInfo)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Path.GetFileName(scriptPath)} did not finish.");
        }

        return (process.ExitCode, Unwrap(await stdout + await stderr));
    }

    /// <summary>
    /// The output as the script wrote it, not as PowerShell laid it out. With its
    /// output redirected, PowerShell draws an error at a fixed console width: the
    /// message is broken across "     | " continuation lines, with colour codes.
    /// Where the break falls depends on the length of the temp path - which contains
    /// the Windows user name - so an assertion on a phrase passed on one PC and
    /// failed on another. Colour codes are removed and continuation lines rejoined
    /// (PowerShell breaks at a space, so a single space restores the message).
    /// </summary>
    internal static string Unwrap(string output)
    {
        string plain = AnsiEscape().Replace(output, "");
        return ContinuationLine().Replace(plain, " ");
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiEscape();

    /// <summary>A line break followed by the indented "|" PowerShell continues an
    /// error message with.</summary>
    [GeneratedRegex(@"[ \t]*\r?\n[ \t]+\|[ \t]?")]
    private static partial Regex ContinuationLine();
}
