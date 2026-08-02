using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CATRA.Core.Navigation;
using CATRA.UI.Navigation;
using CATRA.UI.ViewModels;

namespace CATRA.UI.Views;

/// <summary>
/// Player screen (Tela 4, RF-05): hosts the ST-05 <c>VideoHostControl</c>
/// (black letterbox, aspect ratio preserved) plus the custom transport bar.
/// View-only concerns live here: 3s control auto-hide (DispatcherTimer +
/// MouseMove), F11/ESC fullscreen against the shell window and DPI-aware
/// renderer sizing. All playback logic sits in <see cref="PlayerViewModel"/>.
/// </summary>
public partial class PlayerView : Page, INavigationAware
{
    private static readonly TimeSpan AutoHideDelay = TimeSpan.FromSeconds(3);

    private readonly PlayerViewModel _viewModel;
    private readonly DispatcherTimer _autoHideTimer;

    private Window? _hookedWindow;
    private WindowState _windowStateBeforeFullscreen = WindowState.Normal;
    private bool _fullscreenApplied;

    /// <summary>Creates the page with its view model (DI via navigation service).</summary>
    public PlayerView(PlayerViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _autoHideTimer = new DispatcherTimer { Interval = AutoHideDelay };
        _autoHideTimer.Tick += (_, _) => HideControls();

        // Keep the bar visible while the pointer rests on it.
        TransportBar.MouseEnter += (_, _) => _autoHideTimer.Stop();
        TransportBar.MouseLeave += (_, _) => RestartAutoHide();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <inheritdoc />
    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is int episodeId)
        {
            _ = OpenAsync(episodeId);
        }
    }

    private async Task OpenAsync(int episodeId)
    {
        try
        {
            await _viewModel.OpenAsync(episodeId);
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = $"Falha ao iniciar reprodução: {ex.Message}";
            _viewModel.HasError = true;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hookedWindow = Window.GetWindow(this);
        if (_hookedWindow is not null)
        {
            _hookedWindow.PreviewKeyDown += OnWindowPreviewKeyDown;
        }

        ShowControls();
        Focus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _autoHideTimer.Stop();

        if (_hookedWindow is not null)
        {
            _hookedWindow.PreviewKeyDown -= OnWindowPreviewKeyDown;
            _hookedWindow = null;
        }

        if (_fullscreenApplied)
        {
            ApplyFullscreen(false);
        }

        _viewModel.ReleasePlayback();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                _viewModel.ToggleFullscreenCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape when _viewModel.IsFullscreen:
                _viewModel.IsFullscreen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.IsFullscreen):
                ApplyFullscreen(_viewModel.IsFullscreen);
                break;

            case nameof(PlayerViewModel.IsSeeking):
                if (_viewModel.IsSeeking)
                {
                    ShowControls();
                    _autoHideTimer.Stop(); // never hide mid-drag
                }
                else
                {
                    RestartAutoHide();
                }

                break;
        }
    }

    /// <summary>
    /// Fullscreen: maximized + topmost with the shell title bar hidden
    /// (the shell window is already borderless). ESC/F11 restore.
    /// </summary>
    private void ApplyFullscreen(bool fullscreen)
    {
        var window = _hookedWindow ?? Window.GetWindow(this);
        if (window is null)
        {
            return;
        }

        if (fullscreen)
        {
            _windowStateBeforeFullscreen = window.WindowState;
            window.WindowState = WindowState.Maximized;
            window.Topmost = true;
            (window as IFullscreenHost)?.SetTitleBarVisible(false);
            _fullscreenApplied = true;
        }
        else
        {
            window.Topmost = false;
            window.WindowState = _windowStateBeforeFullscreen == WindowState.Minimized
                ? WindowState.Normal
                : _windowStateBeforeFullscreen;
            (window as IFullscreenHost)?.SetTitleBarVisible(true);
            _fullscreenApplied = false;
        }
    }

    private void VideoHost_Loaded(object sender, RoutedEventArgs e)
    {
        _viewModel.AttachVideoOutput(VideoHost.WindowHandle);
        UpdateVideoHostSize();
    }

    private void VideoArea_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateVideoHostSize();

    /// <summary>
    /// Aspect-fits the video host into the available area (letterbox) and
    /// tells the renderer the real device-pixel size.
    /// </summary>
    private void UpdateVideoHostSize()
    {
        if (!VideoHost.IsLoaded)
        {
            return;
        }

        double availWidth = VideoArea.ActualWidth;
        double availHeight = VideoArea.ActualHeight;
        double ratio = _viewModel.AspectRatio;
        if (availWidth <= 0d || availHeight <= 0d || ratio <= 0d)
        {
            return;
        }

        double width = availWidth;
        double height = width / ratio;
        if (height > availHeight)
        {
            height = availHeight;
            width = height * ratio;
        }

        VideoHost.Width = width;
        VideoHost.Height = height;

        double scale = GetDeviceScale();
        _viewModel.ResizeVideoOutput(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    private double GetDeviceScale()
    {
        HwndSource? source = (HwndSource?)PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    private void RootGrid_MouseMove(object sender, MouseEventArgs e)
        => ShowControls();

    private void ShowControls()
    {
        TransportBar.Visibility = Visibility.Visible;
        RestartAutoHide();
    }

    private void HideControls()
    {
        _autoHideTimer.Stop();
        TransportBar.Visibility = Visibility.Hidden;
    }

    private void RestartAutoHide()
    {
        _autoHideTimer.Stop();
        _autoHideTimer.Start();
    }
}
