using System.Windows.Controls;

using Captr.App.ViewModels;

namespace Captr.App.Views;

/// <summary>The delivery page; all behaviour lives in <see cref="DeliveryViewModel"/>.</summary>
public partial class DeliveryPage : Page
{
    public DeliveryPage(DeliveryViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }
}
