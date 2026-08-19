using System.Windows.Controls;
using System.Windows.Threading;

using Captr.App.ViewModels;

namespace Captr.App.Views;

/// <summary>The status page. Owns only its refresh timer; all state lives in
/// <see cref="StatusViewModel"/>.</summary>
public partial class StatusPage : Page
{
    private readonly StatusViewModel _viewModel;
    private readonly DispatcherTimer _thumbnailTimer;

    public StatusPage(StatusViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // Thumbnails refresh slowly — they are a glance aid, not a video preview.
        _thumbnailTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _thumbnailTimer.Tick += (_, _) => _viewModel.RefreshDisplays();
        Loaded += (_, _) => _thumbnailTimer.Start();
        Unloaded += (_, _) => _thumbnailTimer.Stop();
    }
}
