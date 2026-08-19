namespace Captr.Core.Settings;

/// <summary>
/// The user's configuration, complete (SPEC §8 — and deliberately nothing more; every
/// tunable not listed in §8 is a documented constant in code, not a setting). Owns
/// the persisted shape of <c>settings.json</c>. Immutable: changing a setting means
/// building a new instance via <c>with</c> and saving it through
/// <see cref="SettingsStore"/>, which validates first.
/// </summary>
public sealed record CaptrSettings
{
    /// <summary>The schema version THIS build writes. Bump it when the shape changes,
    /// and add a migration in <c>Migrations/</c> so older files load forward.</summary>
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }

    /// <summary>
    /// Stable identities of displays the user chose NOT to record. Stored as
    /// exclusions so a newly attached display is recorded by default (SPEC §5).
    /// Never contains a DXGI index — indices reorder when cables move.
    /// </summary>
    public IReadOnlyList<string> ExcludedDisplayIds { get; init; } = [];

    /// <summary>Capture frame rate, identical for every display (SPEC §5).</summary>
    public int FrameRate { get; init; } = 15;

    /// <summary>Named quality preset; see <c>Captr.Core.Encoding.QualityPreset</c>.
    /// The default keeps small text legible — the primary use case (SPEC §5).</summary>
    public string QualityPreset { get; init; } = "sharp-text";

    /// <summary>Optional numeric quality override (encoder QP/CQ value) for users who
    /// want direct control; <see langword="null"/> means "use the preset".</summary>
    public int? QualityOverride { get; init; }

    /// <summary>Where sessions record before delivery. Segments, journal, and
    /// heartbeat all live in a per-session subfolder of this path.</summary>
    public string WorkingFolder { get; init; } = DefaultWorkingFolder();

    /// <summary>Naming pattern for finished recordings; see
    /// <see cref="Captr.Core.Naming.OutputNamer.SupportedTokens"/>.</summary>
    public string OutputPattern { get; init; } = Naming.OutputNamer.DefaultPattern;

    /// <summary>Days to keep local working files after every destination has
    /// confirmed delivery (SPEC §7). Deletion additionally requires free-space
    /// pressure and never happens without delivery confirmation.</summary>
    public int RetentionDays { get; init; } = 14;

    /// <summary>Where finished recordings are copied. Empty means "keep local only".</summary>
    public IReadOnlyList<DestinationSettings> Destinations { get; init; } = [];

    /// <summary>Global hotkeys ("Ctrl+Alt+F9" style); empty string disables one.</summary>
    public HotkeySettings Hotkeys { get; init; } = new();

    /// <summary>Start the UI minimised to the tray.</summary>
    public bool StartMinimised { get; init; }

    /// <summary>Closing the window hides to tray instead of exiting (SPEC §9).</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>A fresh, valid default configuration.</summary>
    public static CaptrSettings CreateDefault() => new() { SchemaVersion = CurrentSchemaVersion };

    private static string DefaultWorkingFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Captr", "Sessions");
}

/// <summary>Global hotkey combinations (SPEC §8/§9). Parsed and registered by the UI;
/// stored here as display strings so settings stay human-readable.</summary>
public sealed record HotkeySettings
{
    public string Start { get; init; } = string.Empty;
    public string Pause { get; init; } = string.Empty;
    public string Stop { get; init; } = string.Empty;
}

/// <summary>
/// One delivery destination (SPEC §7). Carries NO secret material — SharePoint
/// credentials live in Windows Credential Manager under <see cref="CredentialName"/>
/// (see <c>Captr.Core.Secrets</c>); this record only points at them.
/// </summary>
public sealed record DestinationSettings
{
    /// <summary>User-visible name, also used to key delivery-queue entries.</summary>
    public required string Name { get; init; }

    public required DestinationKind Kind { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>For <see cref="DestinationKind.Folder"/>: the target directory
    /// (local or UNC).</summary>
    public string? FolderPath { get; init; }

    /// <summary>For <see cref="DestinationKind.SharePoint"/>: the Graph drive to
    /// upload into, e.g. a site's document library.</summary>
    public string? SharePointSiteUrl { get; init; }

    /// <summary>For SharePoint: folder path within the document library.</summary>
    public string? SharePointFolder { get; init; }

    /// <summary>For SharePoint: the Entra tenant id of the app registration.</summary>
    public string? TenantId { get; init; }

    /// <summary>For SharePoint: the app registration's client id. NOT a secret —
    /// the client secret/certificate lives in the credential vault under
    /// <see cref="CredentialName"/>.</summary>
    public string? ClientId { get; init; }

    /// <summary>Credential Manager entry name holding this destination's secret.
    /// The secret itself never appears in settings (SPEC §7).</summary>
    public string? CredentialName { get; init; }
}

/// <summary>The two destination kinds SPEC §7 defines.</summary>
public enum DestinationKind
{
    Folder,
    SharePoint,
}
