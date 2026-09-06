using Microsoft.Win32;

namespace Captr.Core.WindowsEvents;

/// <summary>
/// What this machine does to itself when nobody touches the keyboard: starts a
/// screen saver, locks, or both. Captr never changes any of it — this type exists
/// purely so a recording can say up front what is going to happen to it.
/// </summary>
/// <remarks>
/// <para>
/// WHY CAPTR CANNOT JUST PREVENT THIS. <see cref="ExecutionStateHolder"/> already
/// holds a system-and-display-required execution state for the whole session, which
/// stops the machine sleeping and stops the display powering off — mandatory,
/// because a powered-off display makes Desktop Duplication hand back black frames.
/// The screen saver and the workstation lock are a different mechanism entirely:
/// they run off the INPUT-idle timer (how long since a real keystroke or mouse
/// move), which no execution-state request touches. Nothing in the Windows API
/// suppresses the Group Policy inactivity lock; the only thing that defeats it is
/// synthesising fake input, which overrides a security control the machine's owner
/// deliberately set. So Captr reports and does not override.
/// </para>
/// <para>
/// The two outcomes are not equally bad, which is why they are reported separately.
/// A LOCKING screen saver or an inactivity lock switches Windows to the secure
/// desktop, capture access is lost, and the recording shows an honest gap that
/// closes by itself on unlock. A NON-locking screen saver stays on the ordinary
/// desktop, so capture keeps working perfectly and faithfully records the screen
/// saver — no gap, no warning, and the content silently gone. Silent is worse.
/// </para>
/// <para>
/// Registry only, no Win32: <c>SystemParametersInfo</c> reports the CURRENT process
/// window station's settings, and reading policy keys directly also shows what a
/// domain policy has imposed, which is the case that actually matters on a managed
/// machine such as an AWS WorkSpace.
/// </para>
/// </remarks>
/// <param name="ScreenSaverTimeout">Idle time before the screen saver starts, or
/// null when no screen saver is armed.</param>
/// <param name="ScreenSaverLocks">Whether that screen saver demands the logon screen
/// on resume ("On resume, display logon screen").</param>
/// <param name="InactivityLockTimeout">Idle time before Windows locks the
/// workstation outright, from the machine inactivity limit policy; null when unset.</param>
public sealed record IdleLockPolicy(
    TimeSpan? ScreenSaverTimeout,
    bool ScreenSaverLocks,
    TimeSpan? InactivityLockTimeout)
{
    /// <summary>Nothing will interrupt a recording — the machine is left alone.</summary>
    public static readonly IdleLockPolicy Undisturbed = new(null, false, null);

    /// <summary>
    /// How long the machine may sit without input before the desktop is taken away
    /// and the recording gaps, or null when that never happens.
    /// </summary>
    public TimeSpan? TimeUntilCaptureIsLost =>
        Shortest(ScreenSaverLocks ? ScreenSaverTimeout : null, InactivityLockTimeout);

    /// <summary>
    /// How long before the screen saver is recorded INSTEAD of the screen, or null.
    /// Only a non-locking screen saver does this; a locking one takes the desktop
    /// away instead, which is <see cref="TimeUntilCaptureIsLost"/>.
    /// </summary>
    public TimeSpan? TimeUntilTheScreenSaverIsRecorded =>
        ScreenSaverLocks ? null : ScreenSaverTimeout;

    /// <summary>True when a recording left running unattended will be affected.</summary>
    public bool WillInterruptARecording =>
        TimeUntilCaptureIsLost is not null || TimeUntilTheScreenSaverIsRecorded is not null;

    /// <summary>
    /// One or two plain sentences saying what will happen and when — the same words
    /// for the Diagnostics page and for the note journaled at recording start, so
    /// the two can never disagree. Empty when nothing will happen.
    /// </summary>
    public string Describe()
    {
        var sentences = new List<string>();

        if (TimeUntilCaptureIsLost is { } lockAfter)
        {
            sentences.Add(
                $"This machine locks after {Humanise(lockAfter)} without keyboard or mouse activity. " +
                "Recording keeps running, but there is no desktop to capture while it is locked, so that " +
                "time shows up as a gap and capture resumes by itself on unlock.");
        }

        if (TimeUntilTheScreenSaverIsRecorded is { } screenSaverAfter)
        {
            sentences.Add(
                $"A screen saver is set to start after {Humanise(screenSaverAfter)} without keyboard or " +
                "mouse activity. It does not lock the machine, so if it starts it is what gets recorded — " +
                "no gap and no warning, just the screen saver instead of your screen.");
        }

        return string.Join(" ", sentences);
    }

    /// <summary>
    /// Reads the policy from the registry. Never throws: a machine whose registry
    /// cannot be read is reported as undisturbed, because a diagnostic failing loudly
    /// about its own plumbing helps nobody.
    /// </summary>
    public static IdleLockPolicy Read()
    {
        try
        {
            return new IdleLockPolicy(
                ScreenSaverTimeout: ReadScreenSaverTimeout(),
                ScreenSaverLocks: ReadDesktopFlag("ScreenSaverIsSecure"),
                InactivityLockTimeout: ReadInactivityLimit());
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or IOException
                                              or UnauthorizedAccessException)
        {
            return Undisturbed;
        }
    }

    /// <summary>
    /// A screen saver is armed only when all three agree: switched on, given a
    /// non-zero timeout, and actually assigned a screen saver. Leaving "(None)"
    /// selected keeps a stale timeout in the registry that nothing acts on, so
    /// checking the timeout alone would warn about a screen saver that cannot run.
    /// </summary>
    private static TimeSpan? ReadScreenSaverTimeout()
    {
        if (!ReadDesktopFlag("ScreenSaveActive"))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(ReadDesktopValue("SCRNSAVE.EXE")))
        {
            return null;
        }

        return int.TryParse(ReadDesktopValue("ScreenSaveTimeOut"), out int seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    /// <summary>Machine inactivity limit (seconds); 0 or absent means never.</summary>
    private static TimeSpan? ReadInactivityLimit()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        return key?.GetValue("InactivityTimeoutSecs") is int seconds && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    /// <summary>These values are REG_SZ holding "1" or "0", not DWORDs — a decision
    /// Windows made long ago and never revisited.</summary>
    private static bool ReadDesktopFlag(string name) => ReadDesktopValue(name) == "1";

    /// <summary>
    /// Reads a Control Panel\Desktop value, letting a Group Policy setting win over
    /// the user's own. That order is the point of this check: on a managed machine
    /// the user's Personalisation page may say one thing while policy enforces
    /// another, and policy is what will actually happen to the recording.
    /// </summary>
    private static string? ReadDesktopValue(string name)
    {
        using (RegistryKey? policy = Registry.CurrentUser.OpenSubKey(
            @"Software\Policies\Microsoft\Windows\Control Panel\Desktop"))
        {
            if (policy?.GetValue(name) is string fromPolicy && !string.IsNullOrWhiteSpace(fromPolicy))
            {
                return fromPolicy;
            }
        }

        using RegistryKey? user = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        return user?.GetValue(name) as string;
    }

    private static TimeSpan? Shortest(TimeSpan? left, TimeSpan? right) =>
        (left, right) switch
        {
            (null, null) => null,
            ({ } only, null) => only,
            (null, { } only) => only,
            ({ } a, { } b) => a <= b ? a : b,
        };

    /// <summary>"15 minutes", "1 minute", "1 hour 30 minutes" — never "00:15:00".</summary>
    private static string Humanise(TimeSpan span)
    {
        int totalMinutes = (int)Math.Round(span.TotalMinutes);
        if (totalMinutes < 1)
        {
            return $"{(int)span.TotalSeconds} seconds";
        }

        if (totalMinutes < 60)
        {
            return totalMinutes == 1 ? "1 minute" : $"{totalMinutes} minutes";
        }

        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        string hourPart = hours == 1 ? "1 hour" : $"{hours} hours";
        return minutes == 0 ? hourPart : $"{hourPart} {(minutes == 1 ? "1 minute" : $"{minutes} minutes")}";
    }
}
