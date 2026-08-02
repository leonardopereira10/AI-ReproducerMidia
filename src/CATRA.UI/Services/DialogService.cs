using System.Windows;
using CATRA.UI.Views;
using Microsoft.Win32;

namespace CATRA.UI.Services;

/// <summary>
/// Default <see cref="IDialogService"/>: WPF message boxes, the minimal
/// <see cref="InputDialog"/> and the common file picker.
/// </summary>
public sealed class DialogService : IDialogService
{
    /// <inheritdoc />
    public void ShowMessage(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    /// <inheritdoc />
    public string? Prompt(string title, string label, string initialValue)
    {
        var dialog = new InputDialog(title, label, initialValue)
        {
            Owner = Application.Current.MainWindow,
        };

        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    /// <inheritdoc />
    public string? OpenFile(string title, string filter)
    {
        var picker = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
        };

        return picker.ShowDialog(Application.Current.MainWindow) == true ? picker.FileName : null;
    }
}
