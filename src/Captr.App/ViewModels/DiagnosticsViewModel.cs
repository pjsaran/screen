using System.Collections.ObjectModel;
using System.Diagnostics;

using Captr.Core.Diagnostics;
using Captr.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Captr.App.ViewModels;

/// <summary>
/// The Diagnostics page. Answers, in order: is this installation healthy, where does
/// it keep things, what has it been saying, and how do I hand all of that to someone
/// who can help (SPEC §9/§11).
/// </summary>
/// <remarks>
/// <para>
/// It used to be two buttons and a version string, which meant the only way to find
/// out why something was not working was to export a zip and read it. The health
/// checks (<see cref="HealthReport"/>) put the common answers on the page itself:
/// no encoder proven yet, all displays excluded, a SharePoint secret that has gone
/// missing from Credential Manager, ninety minutes of disk left.
/// </para>
/// <para>
/// The checks live in Captr.Core rather than here so <c>captr doctor</c> reports
/// exactly the same findings, and so they can be tested without a window.
/// </para>
/// </remarks>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    /// <summary>How many lines of log are shown on the page. Enough to cover the
    /// last recording's worth of warnings without turning the page into a log
    /// viewer — the support bundle carries the full files.</summary>
    private const int RecentIssueLines = 40;

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private string _recentIssues = "";

    [ObservableProperty]
    private bool _hasRecentIssues;

    /// <summary>Exactly which build this installation is (SPEC §11) — the first
    /// thing any support conversation needs.</summary>
    public string BuildIdentity { get; } = Captr.Core.Common.BuildInfo.Current().ToDisplayText();

    /// <summary>One row per health check.</summary>
    public ObservableCollection<HealthCheck> Checks { get; } = [];

    /// <summary>Where Captr keeps each kind of file on this machine.</summary>
    public ObservableCollection<StorageLocation> Locations { get; } = [];

    private static string LogFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Captr", "logs");

    public DiagnosticsViewModel() => _ = RefreshAsync();

    /// <summary>Re-runs every check. Off the UI thread: the checks touch the file
    /// system and enumerate displays, neither of which is instant on a busy
    /// machine.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        (IReadOnlyList<HealthCheck> checks, IReadOnlyList<StorageLocation> locations, string issues) =
            await Task.Run(() => (HealthReport.Run(), HealthReport.Locations(), ReadRecentIssues()))
                .ConfigureAwait(true);

        Checks.Clear();
        foreach (HealthCheck check in checks)
        {
            Checks.Add(check);
        }

        Locations.Clear();
        foreach (StorageLocation location in locations)
        {
            Locations.Add(location);
        }

        RecentIssues = issues;
        HasRecentIssues = issues.Length > 0;
    }

    /// <summary>
    /// The most recent warnings and errors from today's log, newest last.
    /// </summary>
    /// <remarks>
    /// Deliberately only warnings and errors: the information lines are the normal
    /// story of a session and would bury the two lines that matter. Reading opens the
    /// file with full sharing because the running host has it open for writing.
    /// </remarks>
    private static string ReadRecentIssues()
    {
        try
        {
            if (!Directory.Exists(LogFolder))
            {
                return "";
            }

            string? newest = Directory.EnumerateFiles(LogFolder, "*.log")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .FirstOrDefault();
            if (newest is null)
            {
                return "";
            }

            using var stream = new FileStream(
                newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var interesting = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("[WRN]", StringComparison.Ordinal)
                    || line.Contains("[ERR]", StringComparison.Ordinal)
                    || line.Contains("[FTL]", StringComparison.Ordinal))
                {
                    interesting.Add(line);
                }
            }

            return interesting.Count == 0
                ? ""
                : string.Join(Environment.NewLine, interesting.TakeLast(RecentIssueLines));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    [RelayCommand]
    private async Task ExportBundleAsync()
    {
        try
        {
            string bundlePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"captr-support-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");

            await SupportBundle.CreateAsync(
                bundlePath, new SettingsStore().Load().WorkingFolder, LogFolder, CancellationToken.None);

            Message = $"Support bundle written to {bundlePath}. It contains no video and no secrets.";
        }
        catch (IOException exception)
        {
            Message = "Could not create the bundle: " + exception.Message;
        }
    }

    [RelayCommand]
    private static void OpenLogs() => Reveal(LogFolder);

    /// <summary>Opens one of the storage locations in File Explorer, selecting the
    /// file when the location is a file rather than a folder.</summary>
    [RelayCommand]
    private static void OpenLocation(StorageLocation location) => Reveal(location.Path);

    private static void Reveal(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
                return;
            }

            // A file: show it highlighted in its folder rather than opening it, which
            // is what someone asking "where is settings.json?" actually wants.
            string? parent = Path.GetDirectoryName(path);
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (parent is not null)
            {
                Directory.CreateDirectory(parent);
                Process.Start(new ProcessStartInfo("explorer.exe", parent) { UseShellExecute = true });
            }
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            // Explorer refusing to open is not worth interrupting the page for.
        }
    }
}
