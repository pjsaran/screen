namespace Captr.Core.Cli;

/// <summary>
/// The documented exit codes of the captr command line (SPEC §10: "exit codes are
/// meaningful and documented, distinguishing at minimum success, idle, paused, and
/// error, so a scheduled task can branch on them"). Owns the contract — these values
/// are public API; changing one breaks someone's scheduled task.
/// </summary>
public static class ExitCodes
{
    /// <summary>The command did what was asked (including the idempotent cases:
    /// start-while-recording, stop-while-idle).</summary>
    public const int Success = 0;

    /// <summary>The command itself failed. Details on stderr.</summary>
    public const int Error = 1;

    /// <summary>Bad arguments or usage. Details on stderr.</summary>
    public const int Usage = 2;

    /// <summary>No recording host answered and none could be started.</summary>
    public const int HostUnreachable = 3;

    /// <summary>`status`: nothing is recording.</summary>
    public const int Idle = 10;

    /// <summary>`status`: a recording exists but is paused.</summary>
    public const int Paused = 11;
}
