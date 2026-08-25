using System.Windows.Controls;
using System.Windows.Threading;

using Captr.App.ViewModels;

namespace Captr.App.Views;

/// <summary>
/// The transfer page; all behaviour lives in <see cref="TransfersViewModel"/>. The
/// page polls while it is visible so a transfer's progress is watchable without
/// pressing Refresh, and stops the moment it is not (SPEC §4: the UI is a view, and
/// an unwatched view must cost nothing).
/// </summary>
public partial class TransfersPage : Page, IPageLifecycle
{
    /// <summary>How often the queue is re-read while the page is open. Transfers are
    /// measured in seconds to minutes, so this is about watchability, not precision.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly TransfersViewModel _viewModel;
    private readonly DispatcherTimer _timer;

    public TransfersPage(TransfersViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => _ = _viewModel.RefreshAsync();
        Loaded += (_, _) => OnEntering();
        Unloaded += (_, _) => OnLeaving();
    }

    /// <inheritdoc />
    public void OnEntering()
    {
        _ = _viewModel.RefreshAsync();
        _timer.Start();
    }

    /// <inheritdoc />
    public void OnLeaving() => _timer.Stop();
}
