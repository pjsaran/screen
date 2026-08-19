using Windows.Win32;
using Windows.Win32.System.Power;

namespace Captr.Core.WindowsEvents;

/// <summary>
/// Keeps the system awake AND the display on for the lifetime of a recording
/// session. MANDATORY per SPEC §6: once Windows powers the display off, Desktop
/// Duplication silently returns black frames — the recording continues, looks
/// healthy, and contains nothing. Owns the execution-state request; disposing
/// releases it so an idle machine can sleep again.
/// </summary>
public sealed class ExecutionStateHolder : IDisposable
{
    private bool _released;

    public ExecutionStateHolder()
    {
        // ES_CONTINUOUS makes the request persist until explicitly cleared;
        // SYSTEM_REQUIRED prevents sleep; DISPLAY_REQUIRED prevents the black-frame
        // failure described above.
        PInvoke.SetThreadExecutionState(
            EXECUTION_STATE.ES_CONTINUOUS
            | EXECUTION_STATE.ES_SYSTEM_REQUIRED
            | EXECUTION_STATE.ES_DISPLAY_REQUIRED);
    }

    /// <summary>Releases the request. Also called by Dispose; idempotent.</summary>
    public void Release()
    {
        if (!_released)
        {
            _released = true;
            PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        }
    }

    public void Dispose() => Release();
}
