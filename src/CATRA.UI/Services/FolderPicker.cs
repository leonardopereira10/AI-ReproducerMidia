using Microsoft.Win32;

namespace CATRA.UI.Services;

/// <summary>
/// WPF <see cref="IFolderPicker"/> backed by the native .NET 8
/// <see cref="OpenFolderDialog"/> common dialog.
/// </summary>
public sealed class FolderPicker : IFolderPicker
{
    /// <inheritdoc />
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
