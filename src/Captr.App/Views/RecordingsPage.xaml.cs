using System.Windows;
using System.Windows.Controls;

using Captr.App.ViewModels;

namespace Captr.App.Views;

/// <summary>The recordings page. Owns the typed-confirmation dialog for deletion —
/// deleting a recording ALWAYS requires typing the session name (SPEC §7/§13).</summary>
public partial class RecordingsPage : Page
{
    private readonly RecordingsViewModel _viewModel;

    public RecordingsPage(RecordingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await _viewModel.RefreshAsync();
    }

    private async void OnClipClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecordingRow row)
        {
            return;
        }

        var dialog = new ClipRangeWindow(row.Folder) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            await _viewModel.ExtractClipAsync(row, dialog.FirstSegment, dialog.LastSegment);
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
}
