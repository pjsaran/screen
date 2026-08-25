using System.Collections.ObjectModel;
using System.Windows.Threading;

using Captr.App.Services;
using Captr.Core.Displays;
using Captr.Core.Encoders;
using Captr.Core.Ipc;
using Captr.Core.Secrets;
using Captr.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Captr.App.ViewModels;

/// <summary>
/// The settings page: an editable copy of <see cref="CaptrSettings"/> plus the
/// per-display include checkboxes and the transfer workflow. Saving validates first
/// and surfaces every field error inline (SPEC §8); nothing reaches disk while
/// invalid.
/// </summary>
/// <remarks>
/// The three encoding choices are deliberately separate, because they answer three
/// different questions: <see cref="SelectedRate"/> is how often the screen is
/// sampled, <see cref="SelectedPreset"/> is how much CPU the encoder may spend per
/// frame, and <see cref="SelectedQuality"/> is how good the result has to look. Each
/// list carries its own trade-off in the label, so nobody has to know what "CRF"
/// means to choose sensibly.
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store = new();

    /// <summary>The host connection when the UI has one — settings go through it so
    /// a running recording can enforce SPEC §8's capture/quality lock.</summary>
    private readonly HostConnection? _host;

    /// <summary>What was on disk when the current save began — the reference point
    /// for working out which stored secrets are no longer referenced.</summary>
    private CaptrSettings _settingsBeforeSave = CaptrSettings.CreateDefault();

    [ObservableProperty]
    private CaptureRate? _selectedRate;

    [ObservableProperty]
    private SpeedPreset? _selectedPreset;

    [ObservableProperty]
    private QualityLevel? _selectedQuality;

    [ObservableProperty]
    private string _workingFolder = "";

    [ObservableProperty]
    private string _outputPattern = "";

    [ObservableProperty]
    private int _retentionDays;

    /// <summary>How many times a failing transfer is retried automatically before it
    /// parks and waits for a person.</summary>
    [ObservableProperty]
    private int _maxAttempts;

    [ObservableProperty]
    private int _firstRetrySeconds;

    [ObservableProperty]
    private int _maxRetrySeconds;

    [ObservableProperty]
    private bool _startMinimised;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _minimiseWhileRecording;

    [ObservableProperty]
    private string _recordToggleHotkey = "";

    [ObservableProperty]
    private string _pauseToggleHotkey = "";

    [ObservableProperty]
    private string _validationText = "";

    /// <summary>
    /// The "Saved." confirmation, shown beside the Save button and cleared again a few
    /// seconds later by <see cref="_savedTextTimer"/>. It used to sit under the page
    /// title and stay there for the rest of the session, so a message about a save
    /// made ten minutes ago still read as if it had just happened.
    /// </summary>
    [ObservableProperty]
    private string _savedText = "";

    /// <summary>
    /// Wipes the "Saved." confirmation a few seconds after it appears. Validation
    /// ERRORS are deliberately not on a timer: they describe something the user still
    /// has to fix, and a message that disappears before it is read is worse than none.
    /// </summary>
    private readonly DispatcherTimer _savedTextTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public ObservableCollection<DisplayChoice> Displays { get; } = [];

    /// <summary>The transfer workflow, in order: where each finished recording goes.</summary>
    public ObservableCollection<DestinationSettings> Destinations { get; } = [];

    // The three encoding lists, straight from the catalogues so the UI and the CLI
    // can never offer different options.
    public static IReadOnlyList<CaptureRate> Rates => CaptureRates.All;

    public static IReadOnlyList<SpeedPreset> Presets => SpeedPresets.All;

    public static IReadOnlyList<QualityLevel> Qualities => QualityLevels.All;

    public SettingsViewModel(HostConnection? host = null)
    {
        _savedTextTimer.Tick += (_, _) =>
        {
            _savedTextTimer.Stop();
            SavedText = "";
        };

        _host = host;
        Load();
    }

    private void Load()
    {
        CaptrSettings settings = _store.Load();

        SelectedRate = CaptureRates.Find(settings.FrameRate) ?? CaptureRates.Find(CaptureRates.Default);
        SelectedPreset = SpeedPresets.FindOrDefault(settings.SpeedPreset);
        SelectedQuality = QualityLevels.FindOrDefault(settings.Quality);

        WorkingFolder = settings.WorkingFolder;
        OutputPattern = settings.OutputPattern;
        RetentionDays = settings.RetentionDays;
        MaxAttempts = settings.Retries.MaxAttempts;
        FirstRetrySeconds = settings.Retries.FirstRetrySeconds;
        MaxRetrySeconds = settings.Retries.MaxRetrySeconds;
        StartMinimised = settings.StartMinimised;
        CloseToTray = settings.CloseToTray;
        MinimiseWhileRecording = settings.MinimiseWhileRecording;
        RecordToggleHotkey = settings.Hotkeys.RecordToggle;
        PauseToggleHotkey = settings.Hotkeys.PauseToggle;

        Destinations.Clear();
        foreach (DestinationSettings destination in settings.Destinations)
        {
            Destinations.Add(destination);
        }

        Displays.Clear();
        try
        {
            foreach (DisplayInfo display in new DisplayEnumerator().Enumerate())
            {
                Displays.Add(new DisplayChoice(
                    display.StableId,
                    $"Display {display.WindowsDisplayNumber} — {display.FriendlyName}",
                    $"{display.Width} × {display.Height}",
                    included: !settings.ExcludedDisplayIds.Contains(display.StableId, StringComparer.OrdinalIgnoreCase)));
            }
        }
        catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or InvalidOperationException)
        {
            // No display info (e.g. remote session) — selection stays as stored.
        }
    }

    /// <summary>Adds or replaces a destination in the workflow. Called by the
    /// destination editor dialog once the user has filled it in.</summary>
    public void UpsertDestination(DestinationSettings destination, DestinationSettings? replacing)
    {
        int index = replacing is null ? -1 : Destinations.IndexOf(replacing);
        if (index >= 0)
        {
            Destinations[index] = destination;
        }
        else
        {
            Destinations.Add(destination);
        }
    }

    /// <summary>Removes a destination. Removing the last one is fine: no destinations
    /// means recordings simply stay in the working folder.</summary>
    /// <remarks>The destination's stored secret is not deleted here — it goes when the
    /// change is SAVED (see <see cref="ForgetUnusedSecrets"/>), so abandoning an edit
    /// cannot destroy a credential that is still in use.</remarks>
    public void RemoveDestination(DestinationSettings destination) => Destinations.Remove(destination);

    /// <summary>
    /// Deletes Windows Credential Manager entries that the SAVED settings no longer
    /// point at, so removing or renaming a destination takes its secret with it.
    /// </summary>
    /// <remarks>
    /// Driven by comparing what was on disk before this save with what is on disk
    /// after it — never by the in-memory list, because a user who removes a
    /// destination and then closes the page without saving must keep their secret.
    /// A credential still referenced by ANY remaining destination is left alone, which
    /// is what makes this safe when two destinations share one entry.
    /// </remarks>
    private static void ForgetUnusedSecrets(CaptrSettings before, CaptrSettings after)
    {
        HashSet<string> stillUsed = new(
            after.Destinations.Select(d => d.CredentialName).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (string orphan in before.Destinations
                     .Select(d => d.CredentialName)
                     .OfType<string>()
                     .Where(name => !stillUsed.Contains(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            CredentialVault.Delete(orphan);
        }
    }

    [RelayCommand]
    private Task SaveAsync()
    {
        SavedText = "";
        ValidationText = "";

        CaptrSettings current = _store.Load();
        _settingsBeforeSave = current;
        CaptrSettings updated = current with
        {
            FrameRate = SelectedRate?.FramesPerSecond ?? CaptureRates.Default,
            SpeedPreset = SelectedPreset?.Name ?? SpeedPresets.DefaultName,
            Quality = SelectedQuality?.Name ?? QualityLevels.DefaultName,
            WorkingFolder = WorkingFolder,
            OutputPattern = OutputPattern,
            RetentionDays = RetentionDays,
            Retries = new RetrySettings
            {
                MaxAttempts = MaxAttempts,
                FirstRetrySeconds = FirstRetrySeconds,
                MaxRetrySeconds = MaxRetrySeconds,
            },
            StartMinimised = StartMinimised,
            CloseToTray = CloseToTray,
            MinimiseWhileRecording = MinimiseWhileRecording,
            Hotkeys = new HotkeySettings
            {
                RecordToggle = RecordToggleHotkey,
                PauseToggle = PauseToggleHotkey,
            },
            Destinations = [.. Destinations],

            // Selection is stored as EXCLUSIONS (SPEC §5): unticked = excluded.
            ExcludedDisplayIds = [.. Displays.Where(d => !d.Included).Select(d => d.StableId)],
        };

        return SaveThroughHostAsync(updated);
    }

    /// <summary>
    /// Saves through the recording host when one is running, so SPEC §8's lock is
    /// enforced: while recording, only degrading capture/quality changes are
    /// accepted, and an accepted one is applied live. With no host there is no
    /// recording, so the settings file is written directly.
    /// </summary>
    private async Task SaveThroughHostAsync(CaptrSettings updated)
    {
        try
        {
            if (_host is not null)
            {
                SetSettingsResponse response = await _host.RequestAsync<SetSettingsResponse>(
                    IpcKinds.SetSettings, new SetSettingsRequest(updated),
                    startHostIfNeeded: false, CancellationToken.None);

                if (response.Applied)
                {
                    SavedText = response.Message;
                    ForgetUnusedSecrets(_settingsBeforeSave, updated);
                }
                else
                {
                    ValidationText = response.Message;
                }

                return;
            }

            SaveDirectly(updated);
        }
        catch (HostUnreachableException)
        {
            // No host answered between the check and the request — nothing is
            // recording, so writing directly is correct.
            SaveDirectly(updated);
        }
        catch (SettingsValidationException exception)
        {
            ValidationText = Describe(exception);
        }
        catch (IpcRequestException exception)
        {
            ValidationText = exception.Message;
        }
    }

    private void SaveDirectly(CaptrSettings updated)
    {
        try
        {
            _store.Save(updated);
            ForgetUnusedSecrets(_settingsBeforeSave, updated);
            SavedText = "Saved.";
        }
        catch (SettingsValidationException exception)
        {
            ValidationText = Describe(exception);
        }
    }

    private static string Describe(SettingsValidationException exception) =>
        string.Join(Environment.NewLine, exception.Errors.Select(e => $"{e.Field}: {e.Message}"));

    /// <summary>Generated by [ObservableProperty]; restarts the clear-down timer every
    /// time a confirmation appears.</summary>
    partial void OnSavedTextChanged(string value)
    {
        _savedTextTimer.Stop();
        if (!string.IsNullOrEmpty(value))
        {
            _savedTextTimer.Start();
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

    public DisplayChoice(string stableId, string label, string resolution, bool included)
    {
        StableId = stableId;
        Label = label;
        Resolution = resolution;
        _included = included;
    }

    public string StableId { get; }

    public string Label { get; }

    public string Resolution { get; }
}
