using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.System.Power;
using Windows.Win32.System.Threading;

namespace Captr.Core.WindowsEvents;

/// <summary>
/// Keeps the system awake AND the display on for the lifetime of a recording
/// session. MANDATORY per SPEC §6: once Windows powers the display off, Desktop
/// Duplication silently returns black frames — the recording continues, looks
/// healthy, and contains nothing. Owns the power request; disposing releases it so
/// an idle machine can sleep again.
/// </summary>
/// <remarks>
/// A POWER REQUEST, not <c>SetThreadExecutionState</c>. The execution state belongs
/// to the calling thread: it was set at the top of an async method, on whichever pool
/// thread happened to run it, and "released" at the end on a different one — which
/// cleared nothing, so the machine stayed awake after the recording. Worse, when the
/// pool retired the original thread, the display-required request went with it in
/// the middle of the recording: exactly the black-frames failure this class exists to
/// prevent. A power request belongs to its handle, so which thread touches it does not
/// matter, and <c>powercfg /requests</c> shows it with a reason a person can read.
/// </remarks>
public sealed class ExecutionStateHolder : IDisposable
{
    private const string Reason = "Captr is recording the screen";

    private readonly SafeFileHandle _request;
    private bool _released;

    public unsafe ExecutionStateHolder()
    {
        fixed (char* reason = Reason)
        {
            var context = new REASON_CONTEXT
            {
                Version = PInvoke.POWER_REQUEST_CONTEXT_VERSION,
                Flags = POWER_REQUEST_CONTEXT_FLAGS.POWER_REQUEST_CONTEXT_SIMPLE_STRING,
            };
            context.Reason.SimpleReasonString = reason;
            _request = PInvoke.PowerCreateRequest(context);
        }

        if (_request.IsInvalid
            || !PInvoke.PowerSetRequest(_request, POWER_REQUEST_TYPE.PowerRequestSystemRequired)
            || !PInvoke.PowerSetRequest(_request, POWER_REQUEST_TYPE.PowerRequestDisplayRequired))
        {
            // Not fatal to the recording, but never silent: without it the display
            // may power off and the rest of the footage be black.
            throw new InvalidOperationException(
                "Windows refused Captr's request to keep the display on while recording " +
                $"(error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}).");
        }
    }

    /// <summary>Releases the request. Also called by Dispose; idempotent, and safe
    /// from any thread.</summary>
    public void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        PInvoke.PowerClearRequest(_request, POWER_REQUEST_TYPE.PowerRequestDisplayRequired);
        PInvoke.PowerClearRequest(_request, POWER_REQUEST_TYPE.PowerRequestSystemRequired);
        _request.Dispose();
    }

    public void Dispose() => Release();

    /// <summary>Whether ANY process currently holds the display on — the machine's
    /// real state, which is what the tests of this class have to read.</summary>
    internal static unsafe bool IsDisplayRequiredSystemWide()
    {
        EXECUTION_STATE state = 0;
        PInvoke.CallNtPowerInformation(
            POWER_INFORMATION_LEVEL.SystemExecutionState, null, 0, &state, sizeof(EXECUTION_STATE));
        return (state & EXECUTION_STATE.ES_DISPLAY_REQUIRED) != 0;
    }
}
