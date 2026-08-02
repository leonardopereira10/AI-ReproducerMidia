using System.Windows.Controls;
using CATRA.Core.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace CATRA.UI.Navigation;

/// <summary>
/// WPF Frame-based implementation of <see cref="INavigationService"/> (frame +
/// back stack). Pages are created through the DI container when a service
/// provider is available (constructor injection), falling back to
/// <see cref="Activator"/> for parameterless pages.
/// </summary>
public sealed class FrameNavigationService : INavigationService
{
    private readonly IServiceProvider? _services;
    private Frame? _frame;

    /// <summary>Creates the service; the DI container supplies the provider.</summary>
    public FrameNavigationService(IServiceProvider? services = null)
    {
        _services = services;
    }

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    /// <summary>
    /// Attaches the host frame (called once by the shell window).
    /// </summary>
    public void Attach(Frame frame) =>
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));

    public void Navigate(Type pageType, object? parameter = null)
    {
        if (_frame is null)
        {
            throw new InvalidOperationException("Navigation frame is not attached.");
        }

        ArgumentNullException.ThrowIfNull(pageType);

        var page = CreatePage(pageType)
            ?? throw new InvalidOperationException($"Cannot create page of type '{pageType.FullName}'.");

        _frame.Navigate(page);

        if (page is INavigationAware aware)
        {
            aware.OnNavigatedTo(parameter);
        }
    }

    public void GoBack()
    {
        if (_frame?.CanGoBack == true)
        {
            _frame.GoBack();
        }
    }

    private object? CreatePage(Type pageType)
    {
        if (_services is not null)
        {
            try
            {
                return ActivatorUtilities.CreateInstance(_services, pageType);
            }
            catch (InvalidOperationException)
            {
                // Fall through to the parameterless activator below.
            }
        }

        return Activator.CreateInstance(pageType);
    }
}
