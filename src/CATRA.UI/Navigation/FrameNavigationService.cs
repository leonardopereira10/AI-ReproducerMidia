using System.Windows;
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

        // Capture the page being left so its (transient) view model can be
        // disposed once navigation succeeds. Transient VMs subscribe to
        // singleton service events; without disposal each visit roots a new VM
        // in those events (growing leak — ST-19 follow-up).
        var leaving = _frame.Content;

        var page = CreatePage(pageType)
            ?? throw new InvalidOperationException($"Cannot create page of type '{pageType.FullName}'.");

        _frame.Navigate(page);
        TryDisposeDataContext(leaving);

        if (page is INavigationAware aware)
        {
            aware.OnNavigatedTo(parameter);
        }
    }

    public void GoBack()
    {
        if (_frame?.CanGoBack == true)
        {
            var leaving = _frame.Content;
            _frame.GoBack();
            TryDisposeDataContext(leaving);
        }
    }

    /// <summary>
    /// Disposes the data context (view model) of a page being navigated away
    /// from, when it is <see cref="IDisposable"/>. Guarded and idempotent: a
    /// throwing VM never breaks navigation. Extracted as a pure static helper so
    /// the disposal contract is unit-testable without a real WPF
    /// <see cref="Frame"/> (which cannot run headless).
    /// </summary>
    /// <param name="content">The <see cref="Frame.Content"/> being left.</param>
    /// <returns><c>true</c> when a disposable data context was disposed.</returns>
    public static bool TryDisposeDataContext(object? content)
    {
        if (content is FrameworkElement { DataContext: IDisposable disposable })
        {
            try
            {
                disposable.Dispose();
                return true;
            }
            catch
            {
                // A faulty VM must never crash navigation; disposal is best effort.
                return false;
            }
        }

        return false;
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
