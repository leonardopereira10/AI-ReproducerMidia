namespace CATRA.Core.Navigation;

/// <summary>
/// Implemented by pages that receive a navigation parameter (e.g. the media
/// item id when navigating Home → Detail).
/// </summary>
public interface INavigationAware
{
    /// <summary>Called right after the page is navigated to.</summary>
    void OnNavigatedTo(object? parameter);
}
