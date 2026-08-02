using System.Windows.Controls;
using CATRA.Core.Navigation;

namespace CATRA.UI.Navigation;

/// <summary>
/// WPF Frame-based implementation of <see cref="INavigationService"/> (frame + back stack).
/// </summary>
public sealed class FrameNavigationService : INavigationService
{
    private Frame? _frame;

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    /// <summary>
    /// Attaches the host frame (called once by the shell window).
    /// </summary>
    public void Attach(Frame frame) => _frame = frame;

    public void Navigate(Type pageType)
    {
        if (_frame is null)
        {
            throw new InvalidOperationException("Navigation frame is not attached.");
        }

        var page = Activator.CreateInstance(pageType)
            ?? throw new InvalidOperationException($"Cannot create page of type '{pageType.FullName}'.");

        _frame.Navigate(page);
    }

    public void GoBack()
    {
        if (_frame?.CanGoBack == true)
        {
            _frame.GoBack();
        }
    }
}
