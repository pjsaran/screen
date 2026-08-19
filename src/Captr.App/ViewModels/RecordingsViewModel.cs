using System.Diagnostics;

using Captr.App.Services;
using Captr.Core.Ipc;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Captr.App.ViewModels;

/// <summary>
/// The recordings page: past sessions with duration, size, coverage, and actions —
/// open, play, verify, re-send (SPEC §9). Deletion happens in the view because it
/// requires the typed confirmation dialog; the view model only executes it.
/// </summary>
public sealed partial class RecordingsViewModel : ObservableObject
{
    private readonly HostConnection _host;

    public System.Collections.ObjectModel.ObservableCollection<RecordingRow> Recordings { get; } = [];

    [ObservableProperty]
    private string _message = "";

    public RecordingsViewModel(HostConnection host) => _host = host;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            ListRecordingsResponse response = await _host.RequestAsync<ListRecordingsResponse>(
                IpcKinds.ListRecordings, null, startHostIfNeeded: true, CancellationToken.None);
            Recordings.Clear();
            foreach (RecordingSummary summary in response.Recordings)
            {
                Recordings.Add(new RecordingRow(summary));
            }

            Message = Recordings.Count == 0 ? "No recordings yet." : "";
        }
        catch (Exception exception) when (exception is HostUnreachableException or IpcRequestException)
        {
            Message = exception.Message;
        }
    }

    [RelayCommand]
    private static void OpenFolder(RecordingRow row) =>
        Process.Start(new ProcessStartInfo("explorer.exe", row.Folder) { UseShellExecute = true });

    [RelayCommand]
    private static void Play(RecordingRow row)
    {
        string? output = Directory.EnumerateFiles(row.Folder, "*.mkv")
            .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("seg-", StringComparison.Ordinal));
        if (output is not null)
        {
            Process.Start(new ProcessStartInfo(output) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private async Task VerifyAsync(RecordingRow row)
    {
        try
        {
            VerifyResponse response = await _host.RequestAsync<VerifyResponse>(
                IpcKinds.Verify, new VerifyRequest(row.Folder), startHostIfNeeded: true, CancellationToken.None);
            row.VerifyResult = response.Intact
                ? "✔ Intact — every hash matches"
                : "✖ " + string.Join("; ", response.Problems);
        }
        catch (Exception exception) when (exception is HostUnreachableException or IpcRequestException)
        {
            row.VerifyResult = exception.Message;
        }
    }

    /// <summary>Executes a deletion AFTER the view has collected the typed
    /// confirmation (SPEC §7: explicit typed confirmation, always).</summary>
    public async Task DeleteConfirmedAsync(RecordingRow row)
    {
        await Task.Run(() => Directory.Delete(row.Folder, recursive: true));
        Recordings.Remove(row);
    }
}

/// <summary>One session row.</summary>
public sealed partial class RecordingRow : ObservableObject
{
    [ObservableProperty]
    private string _verifyResult = "";

    public RecordingRow(RecordingSummary summary)
    {
        Folder = summary.Folder;
        Started = summary.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        Duration = summary.RecordedSpan.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
        Size = $"{summary.TotalBytes / 1_000_000.0:F0} MB";
        Coverage = summary.GapCount == 0 ? "continuous" : $"{summary.GapCount} gap(s)";
        State = summary.Finalized ? "finalised" : "NOT finalised";
    }

    public string Folder { get; }

    public string Started { get; }

    public string Duration { get; }

    public string Size { get; }

    public string Coverage { get; }

    public string State { get; }

    public string Name => Path.GetFileName(Folder);
}
