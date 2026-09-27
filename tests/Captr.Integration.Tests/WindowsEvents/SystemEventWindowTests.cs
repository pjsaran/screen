using Captr.Core.WindowsEvents;

using Shouldly;

using Windows.Win32;
using Windows.Win32.Foundation;

namespace Captr.Integration.Tests.WindowsEvents;

/// <summary>
/// The host's event window against the real Win32 message pump. Runs anywhere with
/// a window station (trait Ffmpeg-free, CI-safe on windows-latest).
/// </summary>
[Trait("Category", "Os")]
public class SystemEventWindowTests
{
    [Fact]
    public async Task Posted_system_messages_surface_as_events()
    {
        using var window = new SystemEventWindow();
        window.Handle.ShouldNotBe(0);

        var timeChanged = new TaskCompletionSource();
        var displayChanged = new TaskCompletionSource();
        window.TimeChanged += () => timeChanged.TrySetResult();
        window.DisplayChanged += () => displayChanged.TrySetResult();

        PInvoke.PostMessage((HWND)window.Handle, PInvoke.WM_TIMECHANGE, 0, 0);
        PInvoke.PostMessage((HWND)window.Handle, PInvoke.WM_DISPLAYCHANGE, 0, 0);

        await timeChanged.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await displayChanged.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Broadcasts_reach_the_window()
    {
        // Suspend/resume, display changes, the clock, and shutdown all arrive as
        // BROADCASTS to top-level windows. The window used to be message-only, and
        // message-only windows receive no broadcasts at all — so none of those ever
        // arrived, and the posted-message test above could not tell. A private,
        // registered message is broadcast here: harmless to every other window.
        using var window = new SystemEventWindow();
        uint probe = PInvoke.RegisterWindowMessage("Captr.SystemEventWindowTests." + Guid.NewGuid().ToString("N"));
        var received = new TaskCompletionSource();
        window.MessageReceived += message =>
        {
            if (message == probe)
            {
                received.TrySetResult();
            }
        };

        await Task.Run(() => Broadcast(probe), TestContext.Current.CancellationToken);

        await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private static unsafe void Broadcast(uint message) =>
        PInvoke.SendMessageTimeout(
            HWND.HWND_BROADCAST, message, 0, 0,
            Windows.Win32.UI.WindowsAndMessaging.SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, 2000, null);

    [Fact]
    public void Dispose_shuts_the_message_thread_down_cleanly()
    {
        var window = new SystemEventWindow();
        window.Dispose();

        // Disposing twice must be harmless.
        window.Dispose();
    }
}
