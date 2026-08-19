using Captr.Core.WindowsEvents;

using Shouldly;

using Windows.Win32;
using Windows.Win32.Foundation;

namespace Captr.Integration.Tests.WindowsEvents;

/// <summary>
/// The host's event window against the real Win32 message pump. Runs anywhere with
/// a window station (trait Ffmpeg-free, CI-safe on windows-latest).
/// </summary>
public class MessageOnlyWindowTests
{
    [Fact]
    public async Task Posted_system_messages_surface_as_events()
    {
        using var window = new MessageOnlyWindow();
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
    public void Dispose_shuts_the_message_thread_down_cleanly()
    {
        var window = new MessageOnlyWindow();
        window.Dispose();

        // Disposing twice must be harmless.
        window.Dispose();
    }
}
