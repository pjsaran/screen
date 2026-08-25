using System.Windows;
using System.Windows.Controls;

using Captr.App.ViewModels;
using Captr.Core.Settings;

namespace Captr.App.Views;

/// <summary>
/// The settings page. Owns only the dialogs it opens — the folder picker and the
/// destination editor; everything else lives in <see cref="SettingsViewModel"/>.
/// </summary>
public partial class SettingsPage : Page
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnBrowseWorkingFolderClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the working folder",
            InitialDirectory = Directory.Exists(_viewModel.WorkingFolder) ? _viewModel.WorkingFolder : "",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _viewModel.WorkingFolder = dialog.FolderName;
        }
    }

    private void OnAddDestinationClicked(object sender, RoutedEventArgs e) => EditDestination(null);

    private void OnEditDestinationClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DestinationSettings destination)
        {
            EditDestination(destination);
        }
    }

    /// <summary>
    /// Opens the destination editor and, if it was saved, commits the change
    /// immediately.
    /// </summary>
    /// <remarks>
    /// The dialog has its own "Save destination" button, so pressing it and then
    /// having to press Save again on the page underneath is a trap — and one with
    /// teeth, because the dialog stores the SharePoint secret in Windows Credential
    /// Manager as soon as it closes. Leaving the page without pressing Save then left
    /// a stored secret that settings.json did not point at, which looked exactly like
    /// "changing the secret did nothing". Committing here keeps the two in step.
    /// </remarks>
    private async void EditDestination(DestinationSettings? existing)
    {
        var dialog = new DestinationEditorWindow(existing) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result is { } destination)
        {
            _viewModel.UpsertDestination(destination, existing);
            await _viewModel.SaveCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// Removes a destination after confirming. This does not delete anything already
    /// transferred — it only stops future recordings being sent there — so a plain
    /// confirmation is enough; the typed confirmation is reserved for deleting
    /// footage (SPEC §7).
    /// </summary>
    private async void OnRemoveDestinationClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DestinationSettings destination)
        {
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Stop transferring recordings to '{destination.Name}'?\n\n" +
            "Files already transferred there are left exactly as they are.",
            "Remove destination",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer == MessageBoxResult.Yes)
        {
            _viewModel.RemoveDestination(destination);

            // Committing now is also what deletes the destination's stored secret
            // from Windows Credential Manager (see SettingsViewModel.ForgetUnusedSecrets),
            // which only ever happens against SAVED settings.
            await _viewModel.SaveCommand.ExecuteAsync(null);
        }
    }
}
