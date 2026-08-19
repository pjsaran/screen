using Captr.Core.Displays;
using Captr.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Captr.App.ViewModels;

/// <summary>
/// The settings page: an editable copy of <see cref="CaptrSettings"/> plus the
/// per-display include checkboxes. Saving validates first and surfaces every field
/// error inline (SPEC §8); nothing reaches disk while invalid.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store = new();

    [ObservableProperty]
    private int _frameRate;

    [ObservableProperty]
    private string _qualityPreset = "";

    [ObservableProperty]
    private string _qualityOverride = "";

    [ObservableProperty]
    private string _workingFolder = "";

    [ObservableProperty]
    private string _outputPattern = "";

    [ObservableProperty]
    private int _retentionDays;

    [ObservableProperty]
    private bool _startMinimised;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private string _hotkeyStart = "";

    [ObservableProperty]
    private string _hotkeyPause = "";

    [ObservableProperty]
    private string _hotkeyStop = "";

    [ObservableProperty]
    private string _validationText = "";

    [ObservableProperty]
    private string _savedText = "";

    public System.Collections.ObjectModel.ObservableCollection<DisplayChoice> Displays { get; } = [];

    public IReadOnlyList<string> PresetNames { get; } =
        [.. Captr.Core.Encoders.QualityPresets.All.Select(p => p.Name)];

    public SettingsViewModel() => Load();

    private void Load()
    {
        CaptrSettings settings = _store.Load();
        FrameRate = settings.FrameRate;
        QualityPreset = settings.QualityPreset;
        QualityOverride = settings.QualityOverride?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        WorkingFolder = settings.WorkingFolder;
        OutputPattern = settings.OutputPattern;
        RetentionDays = settings.RetentionDays;
        StartMinimised = settings.StartMinimised;
        CloseToTray = settings.CloseToTray;
        HotkeyStart = settings.Hotkeys.Start;
        HotkeyPause = settings.Hotkeys.Pause;
        HotkeyStop = settings.Hotkeys.Stop;

        Displays.Clear();
        try
        {
            foreach (DisplayInfo display in new DisplayEnumerator().Enumerate())
            {
                Displays.Add(new DisplayChoice(
                    display.StableId,
                    $"Display {display.WindowsDisplayNumber} — {display.FriendlyName}",
                    Included: !settings.ExcludedDisplayIds.Contains(display.StableId, StringComparer.OrdinalIgnoreCase)));
            }
        }
        catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or InvalidOperationException)
        {
            // No display info (e.g. remote session) — selection stays as stored.
        }
    }

    [RelayCommand]
    private void Save()
    {
        SavedText = "";
        ValidationText = "";

        int? qualityOverride = null;
        if (!string.IsNullOrWhiteSpace(QualityOverride))
        {
            if (!int.TryParse(QualityOverride, out int parsed))
            {
                ValidationText = "Quality override must be a whole number, or empty to use the preset.";
                return;
            }

            qualityOverride = parsed;
        }

        CaptrSettings current = _store.Load();
        CaptrSettings updated = current with
        {
            FrameRate = FrameRate,
            QualityPreset = QualityPreset,
            QualityOverride = qualityOverride,
            WorkingFolder = WorkingFolder,
            OutputPattern = OutputPattern,
            RetentionDays = RetentionDays,
            StartMinimised = StartMinimised,
            CloseToTray = CloseToTray,
            Hotkeys = new HotkeySettings { Start = HotkeyStart, Pause = HotkeyPause, Stop = HotkeyStop },
            // Selection is stored as EXCLUSIONS (SPEC §5): unticked = excluded.
            ExcludedDisplayIds = [.. Displays.Where(d => !d.Included).Select(d => d.StableId)],
        };

        try
        {
            _store.Save(updated);
            SavedText = "Saved.";
        }
        catch (SettingsValidationException exception)
        {
            ValidationText = string.Join(Environment.NewLine, exception.Errors.Select(e => $"{e.Field}: {e.Message}"));
        }
    }

    [RelayCommand]
    private void Reset()
    {
        _store.Save(CaptrSettings.CreateDefault());
        Load();
        SavedText = "Reset to defaults.";
    }
}

/// <summary>One display checkbox row.</summary>
public sealed partial class DisplayChoice : ObservableObject
{
    [ObservableProperty]
    private bool _included;

    public DisplayChoice(string stableId, string label, bool Included)
    {
        StableId = stableId;
        Label = label;
        _included = Included;
    }

    public string StableId { get; }

    public string Label { get; }
}
