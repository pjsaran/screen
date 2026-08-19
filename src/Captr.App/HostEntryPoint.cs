namespace Captr.App;

/// <summary>
/// Bootstraps the headless recording host role. Owns host startup and shutdown;
/// if it fails, no recording can be started on this machine.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ NEVER register this host as a Windows Service, and never move it to a logon
/// task or resident agent (SPEC §4). DXGI Desktop Duplication cannot capture from
/// session 0 — a service would start, appear healthy, and record nothing but black.
/// The host must run in the user's interactive session, started on demand, exiting
/// when idle. This is a deliberate, load-bearing design decision; do not "fix" it.
/// </para>
/// </remarks>
public static class HostEntryPoint
{
    /// <summary>Runs the recording host until it is idle long enough to exit.</summary>
    public static int Run(string[] args)
    {
        // Placeholder until WP7 (host lifecycle). Exits immediately with success so
        // the role switch is testable from day one.
        return 0;
    }
}
