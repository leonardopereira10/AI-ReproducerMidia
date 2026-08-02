using System.Windows;

namespace CATRA.UI.Views;

/// <summary>
/// Minimal single-line input dialog (rename episode, etc.). Returns the
/// entered text through <see cref="Value"/>, or <c>null</c> when cancelled.
/// </summary>
public partial class InputDialog : Window
{
    /// <summary>Creates the dialog with its prompt text and initial value.</summary>
    public InputDialog(string title, string label, string initialValue)
    {
        InitializeComponent();
        Title = title;
        LabelTextBlock.Text = label;
        InputTextBox.Text = initialValue;
        InputTextBox.SelectAll();
        Loaded += (_, _) => InputTextBox.Focus();
    }

    /// <summary>Entered value, or <c>null</c> when the user cancelled.</summary>
    public string? Value { get; private set; }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Value = InputTextBox.Text;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Value = null;
        DialogResult = false;
    }
}
