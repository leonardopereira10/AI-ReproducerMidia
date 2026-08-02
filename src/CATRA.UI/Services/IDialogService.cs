namespace CATRA.UI.Services;

/// <summary>
/// UI dialog abstraction so view models never touch windows/message boxes
/// directly (rename prompt, cover file picker, details dialog).
/// </summary>
public interface IDialogService
{
    /// <summary>Shows an informational message and waits for the user to close it.</summary>
    void ShowMessage(string title, string message);

    /// <summary>
    /// Shows a two-choice dialog with custom button labels. Returns <c>true</c>
    /// when the user picks <paramref name="acceptText"/> (e.g. the RN-03
    /// "Continuar de MM:SS?" prompt with "Continuar" / "Do início").
    /// </summary>
    bool Confirm(string title, string message, string acceptText, string cancelText);

    /// <summary>
    /// Prompts for a single line of text. Returns the entered value, or
    /// <c>null</c> when the user cancels.
    /// </summary>
    string? Prompt(string title, string label, string initialValue);

    /// <summary>
    /// Opens a file-picker. Returns the selected path, or <c>null</c> when the
    /// user cancels.
    /// </summary>
    string? OpenFile(string title, string filter);
}
