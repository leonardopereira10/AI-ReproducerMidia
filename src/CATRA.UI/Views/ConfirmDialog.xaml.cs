using System.Windows;

namespace CATRA.UI.Views;

/// <summary>
/// Minimal two-choice dialog with custom button labels (e.g. the RN-03
/// "Continuar de MM:SS?" prompt with "Continuar" / "Do início"). Returns
/// <c>true</c> when the accept button is chosen.
/// </summary>
public partial class ConfirmDialog : Window
{
    /// <summary>Creates the dialog with its message and button labels.</summary>
    public ConfirmDialog(string title, string message, string acceptText, string cancelText)
    {
        InitializeComponent();
        Title = title;
        MessageTextBlock.Text = message;
        AcceptButton.Content = acceptText;
        CancelButton.Content = cancelText;
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
        => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
