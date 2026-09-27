using System.Globalization;
using System.Text;

using Captr.Core.Common;

namespace Captr.Core.Diagnostics;

/// <summary>
/// Writes a local report when a Captr process meets an error nothing else handled —
/// the window, the recorder, or the command line — so "it just vanished" becomes a
/// file with the version, the machine, and the exception in it. Owns where crash
/// reports go, what is in them, and how many are kept.
/// </summary>
/// <remarks>
/// Reports go in the logs folder, which the support bundle already collects, named
/// <c>crash-&lt;role&gt;-&lt;UTC time&gt;.txt</c>. Every line passes through the same
/// secret scrubbing as the bundle. Only the newest <see cref="KeepNewest"/> are kept,
/// so a crash loop cannot fill the disk. Nothing is ever sent anywhere.
/// </remarks>
public static class CrashReporter
{
    /// <summary>How many crash reports are kept.</summary>
    public const int KeepNewest = 10;

    private static int Installed;

    /// <summary>
    /// Records every unhandled exception in this process from now on. Unobserved task
    /// exceptions are recorded and marked observed; a terminating one is recorded
    /// before the process ends. Idempotent.
    /// </summary>
    public static void Install(string role)
    {
        if (Interlocked.Exchange(ref Installed, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(role, e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()),
                e.IsTerminating ? "The process ended." : "The process continued.");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(role, e.Exception, "A background task failed; the process continued.");
            e.SetObserved();
        };
    }

    /// <summary>Writes one report and returns its path, or null if it could not be
    /// written (a crash report must never cause a second failure).</summary>
    public static string? Write(string role, Exception exception, string outcome) =>
        Write(CaptrPaths.Logs, role, exception, outcome, DateTimeOffset.UtcNow);

    internal static string? Write(string folder, string role, Exception exception, string outcome, DateTimeOffset nowUtc)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder,
                $"crash-{role}-{nowUtc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.txt");

            var report = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"Captr crash report — {role}")
                .AppendLine(CultureInfo.InvariantCulture, $"When (UTC):   {nowUtc:O}")
                .AppendLine(CultureInfo.InvariantCulture, $"Outcome:      {outcome}")
                .AppendLine(CultureInfo.InvariantCulture, $"Version:      {Version()}")
                .AppendLine(CultureInfo.InvariantCulture, $"Windows:      {Environment.OSVersion.VersionString}")
                .AppendLine(CultureInfo.InvariantCulture, $".NET:         {Environment.Version}")
                .AppendLine(CultureInfo.InvariantCulture, $"Process:      {Environment.ProcessId} ({Environment.ProcessPath})")
                .AppendLine()
                .AppendLine("Exception (secret-like values are masked):");
            foreach (string line in exception.ToString().Split('\n'))
            {
                report.AppendLine(SupportBundle.ScrubLine(line.TrimEnd('\r')));
            }

            File.WriteAllText(path, report.ToString());
            PruneOldReports(folder);
            return path;
        }
        catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Version and commit, read straight from the assembly: deliberately NOT
    /// BuildInfo.Current(), which locates and verifies FFmpeg - far too much to ask
    /// of a process that is falling over.</summary>
    private static string Version() =>
        typeof(CrashReporter).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "unknown";

    private static void PruneOldReports(string folder)
    {
        foreach (string old in Directory.GetFiles(folder, "crash-*.txt")
                     .OrderByDescending(File.GetCreationTimeUtc)
                     .ThenByDescending(path => path, StringComparer.Ordinal)
                     .Skip(KeepNewest))
        {
            File.Delete(old);
        }
    }
}
