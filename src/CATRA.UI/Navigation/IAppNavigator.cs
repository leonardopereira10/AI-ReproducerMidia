namespace CATRA.UI.Navigation;

/// <summary>
/// High-level navigation targets for the view models. Keeps view models free
/// of concrete view references (they depend on this abstraction instead of
/// <c>typeof(SomeView)</c>).
/// </summary>
public interface IAppNavigator
{
    /// <summary>Navigates to the Home / library screen (Tela 1).</summary>
    void GoToHome();

    /// <summary>Navigates to the media detail screen (Tela 2) for the given item.</summary>
    void GoToMediaDetail(int mediaItemId);

    /// <summary>Navigates to the player screen (Tela 4) for the given episode.</summary>
    void GoToPlayer(int episodeId);

    /// <summary>Goes back in the navigation stack; falls back to Home.</summary>
    void GoBack();
}
