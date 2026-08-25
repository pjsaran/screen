using System.Windows.Controls;

using Captr.App.ViewModels;

namespace Captr.App.Views;

/// <summary>
/// The diagnostics page; all behaviour lives in <see cref="DiagnosticsViewModel"/>.
/// The checks are re-run every time the page is opened, because their whole value is
/// being true NOW — disk fills up, credentials get removed, and a display gets
/// unplugged while the application is running.
/// </summary>
public partial class DiagnosticsPage : Page, IPageLifecycle
{
    private readonly DiagnosticsViewModel _viewModel;

    public DiagnosticsPage(DiagnosticsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <inheritdoc />
    public void OnEntering() => _ = _viewModel.RefreshAsync();

    /// <inheritdoc />
    public void OnLeaving()
    {
        // Nothing to release: the checks are a one-shot read with no timer behind them.
    }
}
