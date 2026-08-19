using System.Windows.Controls;

using Captr.App.ViewModels;

namespace Captr.App.Views;

/// <summary>The diagnostics page; all behaviour lives in <see cref="DiagnosticsViewModel"/>.</summary>
public partial class DiagnosticsPage : Page
{
    public DiagnosticsPage(DiagnosticsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
