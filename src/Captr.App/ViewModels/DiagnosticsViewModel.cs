using System.Diagnostics;

using Captr.Core.Diagnostics;
using Captr.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Captr.App.ViewModels;

/// <summary>
/// The diagnostics page: export the support bundle (no video, no secrets — proven
/// by test, SPEC §9) and jump to the logs.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    [ObservableProperty]
    private string _message = "";

    private static string LogFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Captr", "logs");

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
    private static void OpenLogs()
    {
        Directory.CreateDirectory(LogFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", LogFolder) { UseShellExecute = true });
    }
}
