using Captr.Core.Common;
using Captr.Core.Displays;
using Captr.Core.Encoders;
using Captr.Core.Secrets;
using Captr.Core.Sessions;
using Captr.Core.Settings;
using Captr.Core.Transfers;

namespace Captr.Core.Diagnostics;

/// <summary>
/// Answers "is this installation actually going to work?" before someone finds out
/// the hard way. Owns the list of checks the Diagnostics page and
/// <c>captr doctor</c> both show, so the two can never disagree.
/// </summary>
/// <remarks>
/// <para>
/// Every check is READ-ONLY and cheap: it looks at settings, the file system, the
/// display topology, the encoder cache, and the transfer queue. Nothing here starts
/// a host, launches FFmpeg, or touches the network — running diagnostics must never
/// change what it is diagnosing, and it must work on a machine where something is
/// already broken.
/// </para>
/// <para>
/// Each result carries a FINDING (what is true) and, when something is wrong, WHAT
/// TO DO about it. A diagnostic that reports a problem without naming the fix just
/// moves the puzzle; the whole value of this page is that a novice can read it and
/// know the next step.
/// </para>
/// </remarks>
public static class HealthReport
{
    /// <summary>Runs every check, in the order a person would want to read them:
    /// can it record at all, then where things go, then what is outstanding.</summary>
    public static IReadOnlyList<HealthCheck> Run()
    {
        var checks = new List<HealthCheck>();

        CaptrSettings? settings = CheckSettings(checks);
        CheckFfmpeg(checks);
        CheckEncoderSupport(checks);
        CheckDisplays(checks, settings);
        CheckEncoder(checks, settings);
        CheckWorkingFolder(checks, settings);
        CheckDestinations(checks, settings);
        CheckOrphanedSecrets(checks, settings);
        CheckTransfers(checks);

        return checks;
    }

    /// <summary>Where each piece of Captr's state lives on this machine, for the
    /// "open the folder and look" case that no amount of reporting replaces.</summary>
    public static IReadOnlyList<StorageLocation> Locations()
    {
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Captr");

        string workingFolder;
        try
        {
            workingFolder = new SettingsStore().Load().WorkingFolder;
        }
        catch (Exception exception) when (exception is IOException or SettingsValidationException)
        {
            workingFolder = Path.Combine(appData, "Sessions");
        }

        return
        [
            new("Recordings", workingFolder,
                "One folder per recording: its segments, journal, integrity record, and the finished file."),
            new("Settings", Path.Combine(appData, "settings.json"),
                "Everything on the Settings page. Contains no secrets."),
            new("Logs", Path.Combine(appData, "logs"),
                "What the recorder and the transfer worker did, one file per day."),
            new("Transfer queue", Path.Combine(appData, "transfers.db"),
                "The queue behind the Transfers page. Survives a reboot, which is how an interrupted transfer resumes."),
            new("Encoder cache", Path.Combine(appData, "encoder-cache.json"),
                "Which encoder was proven fastest on this hardware, so starting a recording is instant."),
        ];
    }

    private static CaptrSettings? CheckSettings(List<HealthCheck> checks)
    {
        string path = SettingsStore.DefaultSettingsPath();

        try
        {
            var store = new SettingsStore();
            CaptrSettings settings = store.Load();

            // A silent recovery is worse than a loud one: the user needs to know
            // something happened to their settings, even though they got them back.
            if (store.RecoveredFromPreviousVersion is { } reason)
            {
                checks.Add(new(
                    "Settings",
                    HealthLevel.Attention,
                    $"Recovered from the previous saved version because {reason}.",
                    "Your settings are back, but check them over — anything changed since the last save is gone. " +
                    $"The damaged file, if there was one, is kept beside {path} as .corrupt."));
                return settings;
            }

            checks.Add(new(
                "Settings",
                HealthLevel.Ok,
                File.Exists(path) ? $"Loaded from {path}." : "Using defaults; nothing has been saved yet.",
                null));
            return settings;
        }
        catch (SettingsValidationException exception)
        {
            checks.Add(new(
                "Settings",
                HealthLevel.Problem,
                "The settings file has problems: " +
                string.Join("; ", exception.Errors.Select(e => $"{e.Field} — {e.Message}")),
                "Open Settings, fix what is listed, and save. Recording refuses to start while settings are invalid."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            checks.Add(new(
                "Settings",
                HealthLevel.Problem,
                $"{path} could not be read: {exception.Message}",
                "Check the file is not open elsewhere and that your account can read it."));
        }

        return null;
    }

    private static void CheckFfmpeg(List<HealthCheck> checks)
    {
        try
        {
            string path = Supervision.FfmpegLocator.FindFfmpeg();
            checks.Add(new("FFmpeg", HealthLevel.Ok, $"Found at {path}.", null));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            checks.Add(new(
                "FFmpeg",
                HealthLevel.Problem,
                "The bundled FFmpeg could not be found: " + exception.Message,
                "Captr cannot record without it. Re-run the installer — it ships FFmpeg alongside the application."));
        }
    }

    /// <summary>
    /// What the bundled FFmpeg build can encode WITH — read from the
    /// capabilities.json the build pipeline generated, so it stays a cheap file read
    /// (no process is launched; this page never changes what it diagnoses). Whether a
    /// listed encoder actually works on THIS machine's GPU is a separate question,
    /// answered by the trial-proven "Encoder" check below.
    /// </summary>
    private static void CheckEncoderSupport(List<HealthCheck> checks)
    {
        try
        {
            FfmpegCapabilities capabilities =
                FfmpegCapabilities.LoadFrom(Supervision.FfmpegLocator.FindFfmpeg());

            var available = new List<string>(capabilities.HardwareEncoders);
            foreach (string software in capabilities.SoftwareEncoders)
            {
                available.Add($"{software} (software)");
            }

            if (available.Count == 0)
            {
                checks.Add(new(
                    "Encoder support",
                    HealthLevel.Problem,
                    "The bundled FFmpeg reports no usable video encoder at all.",
                    "Re-run the installer — the FFmpeg it ships includes both hardware and software encoders."));
                return;
            }

            checks.Add(new(
                "Encoder support",
                HealthLevel.Ok,
                $"The bundled FFmpeg ships {available.Count} encoder(s): {string.Join(", ", available)}.",
                capabilities.HasSoftwareFallback
                    ? null
                    : "There is no software fallback encoder in this build, so a machine without a working " +
                      "GPU encoder cannot record. Re-run the installer."));
        }
        catch (Exception exception) when (
            exception is IOException or System.Text.Json.JsonException
                or FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException)
        {
            checks.Add(new(
                "Encoder support",
                HealthLevel.Problem,
                "The bundled FFmpeg's capabilities.json could not be read: " + exception.Message,
                "Re-run the installer — it ships this file alongside FFmpeg."));
        }
    }

    private static void CheckDisplays(List<HealthCheck> checks, CaptrSettings? settings)
    {
        IReadOnlyList<DisplayInfo> displays;
        try
        {
            displays = new DisplayEnumerator().Enumerate();
        }
        catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or InvalidOperationException)
        {
            checks.Add(new(
                "Displays",
                HealthLevel.Problem,
                "No displays could be enumerated: " + exception.Message,
                "This normally means there is no interactive desktop — for example a Remote Desktop session " +
                "that has been disconnected. Captr records what is on a real screen."));
            return;
        }

        IReadOnlyList<string> excluded = settings?.ExcludedDisplayIds ?? [];
        int included = displays.Count(d => !excluded.Contains(d.StableId, StringComparer.OrdinalIgnoreCase));

        if (displays.Count == 0)
        {
            checks.Add(new("Displays", HealthLevel.Problem, "No displays are attached.", "Attach a display."));
        }
        else if (included == 0)
        {
            checks.Add(new(
                "Displays",
                HealthLevel.Problem,
                $"All {displays.Count} attached display(s) are excluded, so there is nothing to record.",
                "Tick at least one display in Settings."));
        }
        else
        {
            checks.Add(new(
                "Displays",
                HealthLevel.Ok,
                $"{included} of {displays.Count} attached display(s) will be recorded: " +
                string.Join(", ", displays
                    .Where(d => !excluded.Contains(d.StableId, StringComparer.OrdinalIgnoreCase))
                    .Select(d => $"Display {d.WindowsDisplayNumber} ({d.Width}×{d.Height})")),
                null));
        }
    }

    /// <summary>
    /// Whether this machine has already proven an encoder for the CURRENT settings.
    /// A miss is not a fault — it just means the next recording spends a few seconds
    /// proving one — so it is reported as information, not as a problem.
    /// </summary>
    private static void CheckEncoder(List<HealthCheck> checks, CaptrSettings? settings)
    {
        if (settings is null)
        {
            return;
        }

        CachedEncoder? cached = TryReadCachedEncoder(settings);
        if (cached is null)
        {
            checks.Add(new(
                "Encoder",
                HealthLevel.Ok,
                "Not measured on this hardware yet.",
                "The first recording after an install, a driver update, or a change to the encoding settings " +
                "spends a few seconds proving which encoder works best. Every recording after that starts instantly."));
            return;
        }

        bool software = cached.EncoderName.Contains("openh264", StringComparison.OrdinalIgnoreCase)
            || cached.EncoderName.Contains("libx26", StringComparison.OrdinalIgnoreCase);
        bool gdiCapture = cached.CaptureMethod == CaptureMethod.Gdi;

        string finding = $"{cached.EncoderName}, writing about {ByteSize.Format(cached.BytesPerHour)} per hour"
            + (gdiCapture ? ", using compatibility (GDI) screen capture." : ".");

        // GDI capture on top of a software encoder is the virtual-desktop signature
        // (AWS WorkSpaces and similar). It records fine — it just costs CPU, and
        // saying why stops anyone chasing a "faulty" GPU that machine does not have.
        string? advice = (software, gdiCapture) switch
        {
            (true, true) =>
                "This machine's display driver does not support GPU screen capture — normal on a virtual " +
                "desktop such as AWS WorkSpaces. Recording works but uses noticeably more CPU.",
            (true, false) =>
                "This is a software encoder: no GPU encoder passed its trial on this machine. It works, but " +
                "it costs noticeably more CPU. Updating the graphics driver is the usual fix.",
            (false, true) =>
                "GPU screen capture is unavailable here, so frames are read through GDI. Recording works " +
                "but uses more CPU than usual.",
            _ => null,
        };

        checks.Add(new(
            "Encoder",
            software || gdiCapture ? HealthLevel.Attention : HealthLevel.Ok,
            finding,
            advice));
    }

    private static CachedEncoder? TryReadCachedEncoder(CaptrSettings settings)
    {
        try
        {
            IReadOnlyList<DisplayInfo> displays = new DisplayEnumerator().Enumerate();
            if (displays.Count == 0)
            {
                return null;
            }

            // The canvas the arrangement planner would build for the included
            // displays; the cache is keyed on it, so it has to match exactly.
            IReadOnlyList<DisplayInfo> included =
            [
                .. displays.Where(d =>
                    !settings.ExcludedDisplayIds.Contains(d.StableId, StringComparer.OrdinalIgnoreCase)),
            ];
            if (included.Count == 0)
            {
                return null;
            }

            ArrangementPlan arrangement = ArrangementPlanner.Plan(
                [.. included.Select(d => new CaptureSource(d.DxgiOutputIndex, d.Width, d.Height, d.VirtualX, d.VirtualY))]);
            // InformationalVersion, not Version: it must be byte-for-byte what
            // SessionPlanner puts into the fingerprint when the cache is written,
            // or this check reports "not measured" forever.
            BuildInfo build = BuildInfo.Current();
            string fingerprint = EncoderCache.BuildFingerprint(
                arrangement.CanvasWidth, arrangement.CanvasHeight,
                build.InformationalVersion, build.FfmpegBuildId,
                settings.FrameRate, settings.SpeedPreset, settings.Quality);

            return new EncoderCache().TryGet(fingerprint);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or SharpGen.Runtime.SharpGenException)
        {
            return null;
        }
    }

    private static void CheckWorkingFolder(List<HealthCheck> checks, CaptrSettings? settings)
    {
        if (settings is null)
        {
            return;
        }

        string folder = settings.WorkingFolder;
        try
        {
            Directory.CreateDirectory(folder);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
            long free = drive.AvailableFreeSpace;

            // Turn free bytes into the only unit that means anything here: how long
            // you could keep recording. Falls back to the measured rate when there is
            // one, and to a conservative estimate when there is not.
            long bytesPerHour = TryReadCachedEncoder(settings)?.BytesPerHour ?? 4L * 1024 * 1024 * 1024;
            double hours = free / (double)bytesPerHour;

            HealthLevel level = hours switch
            {
                < 0.5 => HealthLevel.Problem,
                < 2 => HealthLevel.Attention,
                _ => HealthLevel.Ok,
            };

            checks.Add(new(
                "Disk space",
                level,
                $"{ByteSize.Format(free)} free on {drive.Name} — roughly {hours:F1} hours of recording.",
                level == HealthLevel.Ok
                    ? null
                    : "Free some space, choose a working folder on a bigger drive, or lower the frame rate or " +
                      "quality. Recording refuses to start with less than 30 minutes of room."));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            checks.Add(new(
                "Disk space",
                HealthLevel.Problem,
                $"The working folder {folder} cannot be used: {exception.Message}",
                "Choose a different working folder in Settings."));
        }
    }

    /// <summary>
    /// Destinations, and — for SharePoint — whether the secret each one points at is
    /// actually still in Windows Credential Manager. A destination whose credential
    /// has been removed looks perfectly fine in Settings and fails on every transfer,
    /// which is exactly the kind of thing this page exists to catch.
    /// </summary>
    private static void CheckDestinations(List<HealthCheck> checks, CaptrSettings? settings)
    {
        if (settings is null)
        {
            return;
        }

        IReadOnlyList<DestinationSettings> enabled = [.. settings.Destinations.Where(d => d.Enabled)];
        if (enabled.Count == 0)
        {
            checks.Add(new(
                "Destinations",
                HealthLevel.Ok,
                "None. Recordings stay in the working folder.",
                "That is a supported choice. Add a destination in Settings if you want finished recordings " +
                "copied somewhere automatically."));
            return;
        }

        var missingSecret = new List<string>();
        var missingDriveId = new List<string>();
        foreach (DestinationSettings destination in enabled.Where(d => d.Kind == DestinationKind.SharePoint))
        {
            if (destination.CredentialName is null || !CredentialVault.Exists(destination.CredentialName))
            {
                missingSecret.Add(destination.Name);
            }

            // Saved by a version of Captr from before the Drive ID field existed.
            // Deliberately NOT a settings-validation error — that would block
            // RECORDING over a transfer-side gap — but every upload will fail until
            // it is filled in, so it belongs on this page.
            if (string.IsNullOrWhiteSpace(destination.SharePointDriveId))
            {
                missingDriveId.Add(destination.Name);
            }
        }

        if (missingSecret.Count == 0 && missingDriveId.Count == 0)
        {
            checks.Add(new(
                "Destinations",
                HealthLevel.Ok,
                $"{enabled.Count} enabled: {string.Join(", ", enabled.Select(d => d.Name))}.",
                null));
            return;
        }

        var findings = new List<string>();
        if (missingSecret.Count > 0)
        {
            findings.Add($"No client secret is stored for: {string.Join(", ", missingSecret)}.");
        }

        if (missingDriveId.Count > 0)
        {
            findings.Add($"No Drive ID is set for: {string.Join(", ", missingDriveId)}.");
        }

        checks.Add(new(
            "Destinations",
            HealthLevel.Problem,
            string.Join(" ", findings),
            "Open each one in Settings, fill in what is missing, and press Test connection. " +
            "Transfers to it will fail until you do."));
    }

    /// <summary>
    /// Stored secrets that no destination points at any more.
    /// </summary>
    /// <remarks>
    /// Reported rather than deleted, deliberately. An entry can be left behind by a
    /// version of Captr that did not clean up after itself — but it can equally have
    /// been created on purpose with <c>captr auth set-secret</c> for a script, and
    /// silently deleting somebody's credential is not a thing a diagnostics page gets
    /// to do. Naming it, with the command to remove it, is.
    /// </remarks>
    private static void CheckOrphanedSecrets(List<HealthCheck> checks, CaptrSettings? settings)
    {
        if (settings is null)
        {
            return;
        }

        IReadOnlyList<string> stored = CredentialVault.ListNames();
        if (stored.Count == 0)
        {
            return;
        }

        HashSet<string> referenced = new(
            settings.Destinations.Select(d => d.CredentialName).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        string[] orphans = [.. stored.Where(name => !referenced.Contains(name))];
        if (orphans.Length == 0)
        {
            return;
        }

        checks.Add(new(
            "Stored secrets",
            HealthLevel.Attention,
            $"{orphans.Length} stored secret(s) belong to no destination: {string.Join(", ", orphans)}.",
            "Harmless, but no longer used — usually a destination that was removed or renamed by an older " +
            "version of Captr. Remove one with:  captr auth delete \"<name>\""));
    }

    private static void CheckTransfers(List<HealthCheck> checks)
    {
        IReadOnlyList<TransferItem> items;
        try
        {
            items = new TransferQueue().List();
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            checks.Add(new(
                "Transfers",
                HealthLevel.Problem,
                "The transfer queue could not be read: " + exception.Message,
                "Close any other copy of Captr and try again."));
            return;
        }

        int needsAttention = items.Count(i =>
            i.State is TransferQueue.StateManualRetry or TransferQueue.StatePausedAuth or TransferQueue.StateCancelled);
        int waiting = items.Count(i => i.State is TransferQueue.StatePending or TransferQueue.StateInProgress);

        checks.Add(needsAttention == 0
            ? new(
                "Transfers",
                HealthLevel.Ok,
                waiting == 0
                    ? $"Nothing outstanding ({items.Count(i => i.State == TransferQueue.StateCompleted)} completed)."
                    : $"{waiting} in flight, nothing stuck.",
                null)
            : new(
                "Transfers",
                HealthLevel.Attention,
                $"{needsAttention} transfer(s) have stopped and are waiting for a person.",
                "Open the Transfers page: each one shows the server's own words and a Retry button."));
    }
}

/// <summary>How much a check's result matters.</summary>
public enum HealthLevel
{
    /// <summary>Working, or a deliberate choice. Nothing to do.</summary>
    Ok,

    /// <summary>Working, but worth knowing about.</summary>
    Attention,

    /// <summary>Something will not work until this is fixed.</summary>
    Problem,
}

/// <summary>One diagnostic result.</summary>
/// <param name="Name">The short subject, e.g. "Disk space".</param>
/// <param name="Finding">What is true right now, in one sentence.</param>
/// <param name="WhatToDo">The fix, or null when nothing needs doing.</param>
public sealed record HealthCheck(string Name, HealthLevel Level, string Finding, string? WhatToDo);

/// <summary>One place Captr keeps something on this machine.</summary>
public sealed record StorageLocation(string Name, string Path, string Explanation);
