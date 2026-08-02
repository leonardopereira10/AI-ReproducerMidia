namespace CATRA.UI.Navigation;

/// <summary>
/// Implemented by the shell window so the player (Tela 4) can hide/restore
/// the app chrome when entering fullscreen. Keeps CATRA.UI free of any
/// concrete window type (the shell lives in CATRA.App).
/// </summary>
public interface IFullscreenHost
{
    /// <summary>Shows or hides the custom title bar (and its layout row).</summary>
    void SetTitleBarVisible(bool visible);
}
