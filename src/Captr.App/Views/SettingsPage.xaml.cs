using System.Windows.Controls;

using Captr.App.ViewModels;

namespace Captr.App.Views;

/// <summary>The settings page; all behaviour lives in <see cref="SettingsViewModel"/>.</summary>
public partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
