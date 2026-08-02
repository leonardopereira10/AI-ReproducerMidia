namespace CATRA.Core.Navigation;

/// <summary>
/// Abstraction for in-app navigation (frame + back stack).
/// </summary>
public interface INavigationService
{
    bool CanGoBack { get; }

    /// <summary>
    /// Navigates to a page, optionally passing a parameter that is delivered to
    /// pages implementing <see cref="INavigationAware"/>.
    /// </summary>
    void Navigate(Type pageType, object? parameter = null);

    void GoBack();
}
