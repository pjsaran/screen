using System.Windows;

namespace Captr.App;

/// <summary>
/// The WPF application object for the UI role. Owns UI-wide resources and startup of
/// the main window; holds no recording state whatsoever (SPEC §4: "the UI is a view" —
/// it can be closed, killed, or relaunched mid-recording with no effect on capture).
/// </summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Placeholder window until WP10 builds the real navigation shell.
        var window = new Window
        {
            Title = "Captr",
            Width = 900,
            Height = 600,
        };
        window.Show();
    }
}
