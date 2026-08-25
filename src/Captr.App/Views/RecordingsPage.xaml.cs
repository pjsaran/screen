using System.Windows;
using System.Windows.Controls;

using Captr.App.ViewModels;

namespace Captr.App.Views;

/// <summary>
/// The recordings page. Owns the "more actions" menu and the typed-confirmation
/// dialog for deletion — deleting a recording ALWAYS requires typing the session
/// name (SPEC §7/§13). The list refreshes itself while the page is on screen; the
/// folder watcher behind that is started and stopped here so it does not hold a
/// directory handle for the life of the application.
/// </summary>
public partial class RecordingsPage : Page, IPageLifecycle, IDisposable
{
    private readonly RecordingsViewModel _viewModel;

    public RecordingsPage(RecordingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => OnEntering();
        Unloaded += (_, _) => OnLeaving();
    }

    /// <inheritdoc />
    public void OnEntering()
    {
        _ = _viewModel.RefreshAsync();
        _viewModel.StartWatching();
    }

    /// <inheritdoc />
    public void OnLeaving() => _viewModel.StopWatching();

    /// <summary>Opens the row's overflow menu under its button. The menu is declared
    /// on the button in XAML so each row's items bind to that row.</summary>
    private void OnMoreClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    /// <summary>
    /// Queues this recording to the destinations that do not have it yet.
    /// </summary>
    /// <remarks>
    /// A Click handler rather than a Command binding, deliberately. A ContextMenu is
    /// hosted in its own popup visual tree, so a
    /// <c>{Binding DataContext.X, RelativeSource={RelativeSource AncestorType=ItemsControl}}</c>
    /// inside one finds no ancestor, resolves to null, and produces a menu item that
    /// looks enabled and does nothing at all when clicked. Reaching the view model
    /// through the page — which does have it — is the reliable way.
    /// </remarks>
    private async void OnResendClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RecordingRow row)
        {
            await _viewModel.ResendAsync(row);
        }
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecordingRow row)
        {
            return;
        }

        // The typed confirmation: the user must type the exact session folder name.
        var dialog = new TypedConfirmationWindow(
            title: "Delete recording?",
            explanation:
                $"This permanently deletes the recording '{row.Name}' including all its segments " +
                "and its integrity record. This cannot be undone.\n\n" +
                $"Type the recording's name to confirm:  {row.Name}",
            requiredText: row.Name)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() == true)
        {
            await _viewModel.DeleteConfirmedAsync(row);
        }
    }

    public void Dispose()
    {
        _viewModel.Dispose();
        GC.SuppressFinalize(this);
    }
}
