using System.Text.Json.Serialization;

using Captr.Core.Encoders;

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
    public const int CurrentSchemaVersion = 3;

    public required int SchemaVersion { get; init; }

    /// <summary>
    /// Stable identities of displays the user chose NOT to record. Stored as
    /// exclusions so a newly attached display is recorded by default (SPEC §5).
    /// Never contains a DXGI index — indices reorder when cables move.
    /// </summary>
    public IReadOnlyList<string> ExcludedDisplayIds { get; init; } = [];

    /// <summary>Capture frame rate, identical for every display (SPEC §5). One of
    /// <see cref="CaptureRates.All"/>.</summary>
    /// <remarks>The JSON name is spelled <c>framerate</c> — one word, matching how
    /// the setting is spoken and how the UI labels it.</remarks>
    [JsonPropertyName("framerate")]
    public int FrameRate { get; init; } = CaptureRates.Default;

    /// <summary>How hard the encoder works per frame; see <see cref="SpeedPresets"/>.
    /// Faster presets cost less CPU and produce bigger files.</summary>
    /// <remarks>The JSON name is just <c>preset</c>: alongside <c>quality</c> it is
    /// unambiguous, and it matches the label on the settings page.</remarks>
    [JsonPropertyName("preset")]
    public string SpeedPreset { get; init; } = SpeedPresets.DefaultName;

    /// <summary>How good the picture must look, on the CRF scale; see
    /// <see cref="QualityLevels"/>.</summary>
    public string Quality { get; init; } = QualityLevels.DefaultName;

    /// <summary>Where sessions record before transfer. Segments, journal, and
    /// heartbeat all live in a per-session subfolder of this path.</summary>
    public string WorkingFolder { get; init; } = DefaultWorkingFolder();

    /// <summary>Naming pattern for finished recordings; see
    /// <see cref="Captr.Core.Naming.OutputNamer.SupportedTokens"/>. A destination may
    /// override it with its own <see cref="DestinationSettings.FileNamePattern"/>.</summary>
    public string OutputPattern { get; init; } = Naming.OutputNamer.DefaultPattern;

    /// <summary>Days to keep local working files after every destination has
    /// confirmed transfer (SPEC §7). Deletion additionally requires free-space
    /// pressure and never happens without transfer confirmation.</summary>
    public int RetentionDays { get; init; } = 14;

    /// <summary>
    /// Where finished recordings are copied when a recording stops, and under what
    /// name at each place. Empty is a valid, supported choice — it means "keep the
    /// recording locally and send it nowhere".
    /// </summary>
    public IReadOnlyList<DestinationSettings> Destinations { get; init; } = [];

    /// <summary>How hard Captr tries before a failed transfer waits for a person.</summary>
    public RetrySettings Retries { get; init; } = new();

    /// <summary>Global hotkeys ("Ctrl+Alt+F9" style); empty string disables one.</summary>
    public HotkeySettings Hotkeys { get; init; } = new();

    /// <summary>Start the UI minimised to the tray.</summary>
    public bool StartMinimised { get; init; }

    /// <summary>Closing the window hides to tray instead of exiting (SPEC §9).</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>
    /// Hide the window the moment recording starts, leaving Captr reachable only from
    /// the tray. Useful because the recorder's own window is usually the last thing
    /// you want in the recording.
    /// </summary>
    public bool MinimiseWhileRecording { get; init; }

    /// <summary>A fresh, valid default configuration.</summary>
    public static CaptrSettings CreateDefault() => new() { SchemaVersion = CurrentSchemaVersion };

    private static string DefaultWorkingFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Captr", "Sessions");
}

/// <summary>
/// Global hotkey combinations (SPEC §8/§9). Parsed and registered by the UI; stored
/// here as display strings so settings stay human-readable.
/// </summary>
/// <remarks>
/// Both hotkeys are TOGGLES rather than separate start/stop and pause/resume keys.
/// A toggle is what a hotkey is actually for: you press it without looking at the
/// screen, and pressing "stop" while idle or "start" while recording — the failure
/// mode of separate keys — cannot happen.
/// </remarks>
public sealed record HotkeySettings
{
    /// <summary>
    /// The combination that starts a recording when idle and stops it when recording
    /// or paused. Empty disables it.
    /// </summary>
    /// <remarks>
    /// Defaults are supplied rather than left blank, because a hotkey nobody
    /// configured is a feature nobody discovers. Ctrl+Alt+F9 and F10 are the
    /// convention among screen recorders and collide with very little — and if they
    /// do collide, registration fails loudly and names the combination (see
    /// <c>HotkeyManager.Conflicts</c>) rather than silently doing nothing.
    /// </remarks>
    public string RecordToggle { get; init; } = "Ctrl+Alt+F9";

    /// <summary>Pauses a running recording, resumes a paused one. Does nothing when
    /// idle (there is nothing to pause). Empty disables it.</summary>
    public string PauseToggle { get; init; } = "Ctrl+Alt+F10";
}

/// <summary>
/// How a failing transfer is retried (SPEC §7's "exponential backoff with jitter,
/// bounded"). Exposed as settings because the right answer depends on the
/// destination: a flaky VPN deserves patience, an office share that is either up or
/// down does not, and endless invisible retrying is what makes a queue feel broken.
/// </summary>
public sealed record RetrySettings
{
    /// <summary>
    /// How many automatic attempts a transfer gets before it stops and waits for a
    /// person. Reaching the limit never loses anything — the row parks with the last
    /// error and a Retry button, and the local recording is untouched.
    /// </summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>How long to wait before the FIRST retry. Each subsequent wait is
    /// roughly double the previous one.</summary>
    public int FirstRetrySeconds { get; init; } = 30;

    /// <summary>The ceiling on that doubling, so a long-running queue keeps checking
    /// periodically instead of drifting into waits measured in days.</summary>
    public int MaxRetrySeconds { get; init; } = 1800;
}

/// <summary>
/// One destination: a place finished recordings are copied to, and
/// the name they take there (SPEC §7). Carries NO secret material — SharePoint
/// credentials live in Windows Credential Manager under <see cref="CredentialName"/>
/// (see <c>Captr.Core.Secrets</c>); this record only points at them.
/// </summary>
public sealed record DestinationSettings
{
    /// <summary>User-visible name, also used to key transfer-queue entries. Must be
    /// unique: the queue tracks one row per (recording, destination name), which is
    /// what lets a retry re-send to ONLY the destination that failed.</summary>
    public required string Name { get; init; }

    public required DestinationKind Kind { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Naming pattern for the copy transferred HERE, using the same tokens as
    /// <see cref="CaptrSettings.OutputPattern"/>. Null or empty means "use the
    /// recording's own name", which is what most people want; a per-destination
    /// pattern exists for the case where an archive wants a different convention
    /// from the working copy.
    /// </summary>
    public string? FileNamePattern { get; init; }

    /// <summary>For <see cref="DestinationKind.Folder"/>: the target directory
    /// (local or UNC).</summary>
    public string? FolderPath { get; init; }

    /// <summary>For <see cref="DestinationKind.SharePoint"/>: the site the document
    /// library belongs to. Kept for the user's own reference and shown in the UI;
    /// the upload itself is addressed by <see cref="SharePointDriveId"/>.</summary>
    public string? SharePointSiteUrl { get; init; }

    /// <summary>
    /// For SharePoint: the Graph drive id of the document library uploads land in
    /// (the long <c>b!…</c> string from <c>GET /sites/{site-id}/drives</c>).
    /// </summary>
    /// <remarks>
    /// Required rather than resolved from the site URL at transfer time, deliberately:
    /// resolving needs an extra Graph permission and an extra round-trip on every
    /// upload, and a site can hold several libraries — the id says exactly which one.
    /// The editor's "Test connection" button verifies it before anything is queued.
    /// </remarks>
    public string? SharePointDriveId { get; init; }

    /// <summary>For SharePoint: folder path within the document library.</summary>
    public string? SharePointFolder { get; init; }

    /// <summary>For SharePoint: the Entra tenant id of the app registration.</summary>
    public string? TenantId { get; init; }

    /// <summary>For SharePoint: the app registration's client id. NOT a secret —
    /// the client secret/certificate lives in the credential vault under
    /// <see cref="CredentialName"/>.</summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Credential Manager entry name holding this destination's secret. The secret
    /// itself never appears in settings (SPEC §7) — this is only a pointer to it.
    /// </summary>
    /// <remarks>
    /// Users do not choose or type this. It is derived from the destination's name by
    /// <see cref="CredentialNameFor"/> and stored so that a later rename cannot orphan
    /// the secret it points at.
    /// </remarks>
    public string? CredentialName { get; init; }

    /// <summary>
    /// The Credential Manager entry name for a destination. Prefixed so Captr's
    /// entries are identifiable among everything else in a user's vault, and derived
    /// from the destination name so the two stay obviously connected when someone
    /// browses Credential Manager directly.
    /// </summary>
    public static string CredentialNameFor(string destinationName) =>
        "Captr:" + destinationName.Trim();
}

/// <summary>
/// The kinds of destination Captr knows about. Only <see cref="Folder"/> and
/// <see cref="SharePoint"/> are implemented (SPEC §7); the rest are listed so the
/// roadmap is visible where people look for it, and are rejected by
/// <see cref="SettingsValidator"/> rather than half-working.
/// See <see cref="DestinationKinds"/> for which is which.
/// </summary>
public enum DestinationKind
{
    /// <summary>A local drive, mapped drive, or UNC share.</summary>
    Folder,

    /// <summary>A SharePoint document library, via the Microsoft Graph API.</summary>
    SharePoint,

    /// <summary>Amazon S3 or an S3-compatible bucket. Not implemented.</summary>
    AmazonS3,

    /// <summary>Azure Blob Storage container. Not implemented.</summary>
    AzureBlob,

    /// <summary>Google Drive. Not implemented.</summary>
    GoogleDrive,

    /// <summary>An SFTP server. Not implemented.</summary>
    Sftp,
}

/// <summary>
/// What each <see cref="DestinationKind"/> is called and whether Captr can actually
/// transfer to it yet. Owns the single list the settings UI, the CLI, and the
/// validator all read, so a kind can never appear as available in one place and not
/// another.
/// </summary>
public static class DestinationKinds
{
    /// <summary>Every kind, implemented ones first.</summary>
    public static readonly IReadOnlyList<DestinationKindInfo> All =
    [
        new(DestinationKind.Folder, "Local or network folder",
            "A folder on this PC, a mapped drive, or a UNC share.", Implemented: true),
        new(DestinationKind.SharePoint, "SharePoint",
            "A SharePoint document library, uploaded through the Microsoft Graph API.", Implemented: true),
        new(DestinationKind.AmazonS3, "Amazon S3",
            "An S3 or S3-compatible bucket.", Implemented: false),
        new(DestinationKind.AzureBlob, "Azure Blob Storage",
            "A blob container in an Azure storage account.", Implemented: false),
        new(DestinationKind.GoogleDrive, "Google Drive",
            "A Google Drive folder.", Implemented: false),
        new(DestinationKind.Sftp, "SFTP",
            "An SFTP server.", Implemented: false),
    ];

    /// <summary>True when Captr can transfer to this kind today.</summary>
    public static bool IsImplemented(DestinationKind kind) =>
        All.First(k => k.Kind == kind).Implemented;

    /// <summary>The descriptor for a kind.</summary>
    public static DestinationKindInfo Describe(DestinationKind kind) =>
        All.First(k => k.Kind == kind);
}

/// <summary>One destination kind as the user sees it.</summary>
/// <param name="Implemented">False means "planned, not built" — choosing it is a
/// settings validation error, not a silent no-op.</param>
public sealed record DestinationKindInfo(
    DestinationKind Kind, string DisplayName, string Description, bool Implemented)
{
    /// <summary>What the picker shows, including the "coming soon" marker.</summary>
    public string Label => Implemented ? DisplayName : $"{DisplayName} (coming soon)";
}
