namespace CATRA.UI.Services;

/// <summary>
/// Folder-picker abstraction so view models never instantiate a Win32 common
/// dialog directly (keeps them headless-testable). The WPF implementation
/// (<see cref="FolderPicker"/>) wraps <c>Microsoft.Win32.OpenFolderDialog</c>.
/// </summary>
public interface IFolderPicker
{
    /// <summary>
    /// Shows a folder-picker with the given title. Returns the selected path, or
    /// <c>null</c> when the user cancels.
    /// </summary>
    string? PickFolder(string title);
}
