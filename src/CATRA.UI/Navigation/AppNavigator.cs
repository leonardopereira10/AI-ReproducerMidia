using CATRA.Core.Navigation;
using CATRA.UI.Views;

namespace CATRA.UI.Navigation;

/// <summary>
/// Maps high-level navigation intents to concrete views over
/// <see cref="INavigationService"/>.
/// </summary>
public sealed class AppNavigator : IAppNavigator
{
    private readonly INavigationService _navigation;

    /// <summary>Creates the navigator over the frame navigation service.</summary>
    public AppNavigator(INavigationService navigation)
    {
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
    }

    /// <inheritdoc />
    public void GoToHome() => _navigation.Navigate(typeof(HomeView));

    /// <inheritdoc />
    public void GoToMediaDetail(int mediaItemId) =>
        _navigation.Navigate(typeof(MediaDetailView), mediaItemId);

    /// <inheritdoc />
    public void GoToPlayer(int episodeId) =>
        _navigation.Navigate(typeof(PlayerView), episodeId);

    /// <inheritdoc />
    public void GoBack()
    {
        if (_navigation.CanGoBack)
        {
            _navigation.GoBack();
        }
        else
        {
            GoToHome();
        }
    }
}
