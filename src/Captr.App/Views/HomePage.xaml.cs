using System.Windows.Controls;
using System.Windows.Threading;

using Captr.App.ViewModels;

namespace Captr.App.Views;

/// <summary>
/// The status page. Owns only its preview timer; all state lives in
/// <see cref="HomeViewModel"/>. The timer runs ONLY while the page is on screen —
/// see <see cref="IPageLifecycle"/> — so navigating away releases the GPU capture
/// devices behind the previews instead of leaving them running for the life of the
/// application.
/// </summary>
public partial class HomePage : Page, IPageLifecycle, IDisposable
{
    /// <summary>How often the previews update. They are a glance aid ("yes, that is
    /// the right screen"), not a video preview, so a slow refresh is correct and
    /// costs almost nothing.</summary>
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromSeconds(3);

    private readonly HomeViewModel _viewModel;
    private readonly DispatcherTimer _previewTimer;

    public HomePage(HomeViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        _previewTimer = new DispatcherTimer { Interval = PreviewInterval };
        _previewTimer.Tick += (_, _) => _viewModel.RefreshDisplays();
        Loaded += (_, _) => OnEntering();
        Unloaded += (_, _) => OnLeaving();
    }

    /// <inheritdoc />
    public void OnEntering()
    {
        _viewModel.RefreshDisplays();
        _previewTimer.Start();
    }

    /// <inheritdoc />
    public void OnLeaving()
    {
        _previewTimer.Stop();
        _viewModel.ReleasePreviews();
    }

    public void Dispose()
    {
        _previewTimer.Stop();
        _viewModel.Dispose();
        GC.SuppressFinalize(this);
    }
}
