namespace CATRA.Core.Navigation;

/// <summary>
/// Abstraction for in-app navigation (frame + back stack).
/// </summary>
public interface INavigationService
{
    bool CanGoBack { get; }

    void Navigate(Type pageType);

    void GoBack();
}
