using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Captr.Core.WindowsEvents;

/// <summary>
/// A hidden message-only window on a dedicated thread, translating the Windows
/// messages of SPEC §6's events table into .NET events. Owns the window and its
/// message loop; if it dies, the host goes deaf to sleep, lock, display, and
/// shutdown events — so its loop never throws outward.
/// </summary>
/// <remarks>
/// Events are raised ON THE MESSAGE THREAD. Handlers must be quick and must
/// marshal real work elsewhere; a blocked handler blocks every subsequent system
/// message. The session engine posts to its own loop for exactly this reason.
/// </remarks>
public sealed class MessageOnlyWindow : IDisposable
{
    private const string WindowClassName = "CaptrHostEvents";

    // A window CLASS owns its procedure, so the procedure must be static and
    // process-lifetime: routing per-window through this registry is what lets two
    // MessageOnlyWindow instances (e.g. in tests) coexist. The delegate is stored
    // statically so the GC can never collect it out from under the OS.
    private static readonly WNDPROC StaticWindowProcedure = RouteMessage;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, MessageOnlyWindow> Instances = new();
    private static int ClassRegistered;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _windowReady = new();
    private Exception? _startupFailure;
    private nint _hwnd;

    /// <summary>The system is about to suspend: close the segment, flush.</summary>
    public event Action? SuspendRequested;

    /// <summary>The system resumed: start a new segment, record the gap.</summary>
    public event Action? Resumed;

    /// <summary>Lock/unlock and remote-session transitions; parameter is the
    /// WTS event code (see <see cref="SessionChangeKind"/>).</summary>
    public event Action<SessionChangeKind>? SessionChanged;

    /// <summary>Display topology or mode changed (raw — the consumer debounces).</summary>
    public event Action? DisplayChanged;

    /// <summary>The system clock jumped.</summary>
    public event Action? TimeChanged;

    /// <summary>Windows is shutting down or logging off — finalise quickly.</summary>
    public event Action? EndSessionRequested;

    /// <summary>The created window handle (for ShutdownBlock registration).</summary>
    public nint Handle => _hwnd;

    public MessageOnlyWindow()
    {
        _thread = new Thread(MessageLoop)
        {
            Name = "Captr host events",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // Plain thread-startup handshake (not a task wait): the message thread
        // signals once the window exists or creation failed.
        _windowReady.Wait();
        if (_startupFailure is not null)
        {
            throw new InvalidOperationException("Failed to create the host event window.", _startupFailure);
        }
    }

    private unsafe void MessageLoop()
    {
        try
        {
            if (Interlocked.Exchange(ref ClassRegistered, 1) == 0)
            {
                fixed (char* className = WindowClassName)
                {
                    var windowClass = new WNDCLASSW
                    {
                        lpfnWndProc = StaticWindowProcedure,
                        lpszClassName = className,
                        hInstance = (HINSTANCE)Marshal.GetHINSTANCE(typeof(MessageOnlyWindow).Module),
                    };
                    PInvoke.RegisterClass(in windowClass);
                }
            }

            // HWND_MESSAGE parent = a message-only window: no UI, just a mailbox.
            HWND hwnd = PInvoke.CreateWindowEx(
                0, WindowClassName, "Captr host events", 0,
                0, 0, 0, 0, HWND.HWND_MESSAGE, null, null, null);
            if (hwnd.IsNull)
            {
                _startupFailure = new InvalidOperationException("CreateWindowEx returned null.");
                _windowReady.Set();
                return;
            }

            // Lock/unlock and RDP transitions only arrive after registering.
            PInvoke.WTSRegisterSessionNotification(hwnd, PInvoke.NOTIFY_FOR_THIS_SESSION);

            _hwnd = (nint)hwnd.Value;
            Instances[_hwnd] = this;
            _windowReady.Set();

            while (PInvoke.GetMessage(out MSG message, hwnd, 0, 0).Value > 0)
            {
                PInvoke.TranslateMessage(in message);
                PInvoke.DispatchMessage(in message);
            }

            PInvoke.WTSUnRegisterSessionNotification(hwnd);
        }
        catch (Exception exception)
        {
            _startupFailure = exception;
            _windowReady.Set();
        }
    }

    private static unsafe LRESULT RouteMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        return Instances.TryGetValue((nint)hwnd.Value, out MessageOnlyWindow? instance)
            ? instance.WindowProcedure(hwnd, message, wParam, lParam)
            : PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private LRESULT WindowProcedure(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_POWERBROADCAST:
                if (wParam == PInvoke.PBT_APMSUSPEND)
                {
                    SuspendRequested?.Invoke();
                }
                else if (wParam == PInvoke.PBT_APMRESUMEAUTOMATIC || wParam == PInvoke.PBT_APMRESUMESUSPEND)
                {
                    Resumed?.Invoke();
                }

                break;

            case PInvoke.WM_WTSSESSION_CHANGE:
                SessionChanged?.Invoke((SessionChangeKind)wParam.Value);
                break;

            case PInvoke.WM_DISPLAYCHANGE:
            case PInvoke.WM_DEVICECHANGE:
                DisplayChanged?.Invoke();
                break;

            case PInvoke.WM_TIMECHANGE:
                TimeChanged?.Invoke();
                break;

            case PInvoke.WM_QUERYENDSESSION:
                EndSessionRequested?.Invoke();
                // TRUE = we do not veto; the ShutdownBlock reason buys the
                // finalisation time (SPEC §6: block, finalise quickly, release).
                return (LRESULT)1;

            case PInvoke.WM_CLOSE:
                PInvoke.DestroyWindow(hwnd);
                PInvoke.PostQuitMessage(0);
                return (LRESULT)0;
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd != 0)
        {
            nint hwnd = _hwnd;
            _hwnd = 0;
            PInvoke.PostMessage((HWND)hwnd, PInvoke.WM_CLOSE, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(5));
            Instances.TryRemove(hwnd, out _);
        }
    }
}

/// <summary>WTS session-change codes the host reacts to (values are the Win32
/// WTS_* constants).</summary>
public enum SessionChangeKind : uint
{
    ConsoleConnect = 1,
    ConsoleDisconnect = 2,
    RemoteConnect = 3,
    RemoteDisconnect = 4,
    SessionLock = 7,
    SessionUnlock = 8,
}
