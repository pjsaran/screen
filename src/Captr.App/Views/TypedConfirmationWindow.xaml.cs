using System.Windows;
using System.Windows.Controls;

namespace Captr.App.Views;

/// <summary>
/// The typed-confirmation dialog (SPEC §7: "deleting recordings from the UI
/// requires an explicit typed confirmation"). The destructive button only enables
/// once the typed text matches exactly.
/// </summary>
public partial class TypedConfirmationWindow : Window
{
    private readonly string _requiredText;

    public TypedConfirmationWindow(string title, string explanation, string requiredText)
    {
        _requiredText = requiredText;
        InitializeComponent();
        TitleText.Text = title;
        ExplanationText.Text = explanation;
        ConfirmationBox.Focus();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) =>
        ConfirmButton.IsEnabled = string.Equals(ConfirmationBox.Text, _requiredText, StringComparison.Ordinal);

    private void OnConfirmClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
