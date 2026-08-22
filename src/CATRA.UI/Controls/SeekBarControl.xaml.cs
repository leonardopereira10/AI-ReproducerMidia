using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CATRA.UI.Converters;

namespace CATRA.UI.Controls;

/// <summary>
/// Custom seek bar (Tela 4): a templated <see cref="Slider"/> with a glowing
/// thumb, a 3px→8px hover-expand track and click/drag seeking. Clicking seeks
/// to the point (<c>IsMoveToPointEnabled</c>); dragging previews the position
/// with a timestamp tooltip and commits on release. Seek start/commit are
/// surfaced through <see cref="SeekStartedCommand"/>/<see cref="SeekCompletedCommand"/>
/// so the view model can suppress engine updates during the drag (no jumps).
/// </summary>
public partial class SeekBarControl : UserControl
{
    /// <summary>Identifies the <see cref="Value"/> dependency property (seconds).</summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(SeekBarControl), new PropertyMetadata(0d));

    /// <summary>Identifies the <see cref="Maximum"/> dependency property (seconds).</summary>
    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(SeekBarControl), new PropertyMetadata(0d));

    /// <summary>Identifies the <see cref="SeekStartedCommand"/> dependency property.</summary>
    public static readonly DependencyProperty SeekStartedCommandProperty =
        DependencyProperty.Register(nameof(SeekStartedCommand), typeof(ICommand), typeof(SeekBarControl), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="SeekCompletedCommand"/> dependency property.</summary>
    public static readonly DependencyProperty SeekCompletedCommandProperty =
        DependencyProperty.Register(nameof(SeekCompletedCommand), typeof(ICommand), typeof(SeekBarControl), new PropertyMetadata(null));

    private readonly ToolTip _dragTooltip;
    private bool _dragging;

    /// <summary>Creates the control and wires drag/click seek events.</summary>
    public SeekBarControl()
    {
        InitializeComponent();

        _dragTooltip = new ToolTip
        {
            Placement = PlacementMode.Relative,
            PlacementTarget = SeekBarSlider,
            Content = new TextBlock(),
        };

        // Thumb drag: preview + commit.
        SeekBarSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted));
        SeekBarSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));

        // Track click (IsMoveToPointEnabled moves the value): bracket it with
        // start/commit so the engine seeks exactly once, to the click point.
        SeekBarSlider.PreviewMouseLeftButtonDown += (_, _) => NotifySeekStarted();
        
        // Use AddHandler with handledEventsToo=true to ensure the event is captured
        // even when the Thumb captures the mouse (IsMoveToPointEnabled).
        SeekBarSlider.AddHandler(
            UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler((_, _) => NotifySeekCompleted()),
            handledEventsToo: true);

        SeekBarSlider.ValueChanged += (_, _) =>
        {
            if (_dragging)
            {
                UpdateDragTooltip();
            }
        };
    }

    /// <summary>Current position in seconds (two-way; engine-driven or dragged).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Duration in seconds (slider maximum).</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>Executed when a user seek gesture starts (drag or click).</summary>
    public ICommand? SeekStartedCommand
    {
        get => (ICommand?)GetValue(SeekStartedCommandProperty);
        set => SetValue(SeekStartedCommandProperty, value);
    }

    /// <summary>Executed when the gesture ends; commit the previewed position.</summary>
    public ICommand? SeekCompletedCommand
    {
        get => (ICommand?)GetValue(SeekCompletedCommandProperty);
        set => SetValue(SeekCompletedCommandProperty, value);
    }

    private void OnDragStarted(object sender, DragStartedEventArgs e)
    {
        _dragging = true;
        NotifySeekStarted();
        UpdateDragTooltip();
        _dragTooltip.IsOpen = true;
    }

    private void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        _dragTooltip.IsOpen = false;
        NotifySeekCompleted();
    }

    private void NotifySeekStarted()
    {
        if (SeekStartedCommand?.CanExecute(null) == true)
        {
            SeekStartedCommand.Execute(null);
        }
    }

    private void NotifySeekCompleted()
    {
        if (SeekCompletedCommand?.CanExecute(null) == true)
        {
            SeekCompletedCommand.Execute(null);
        }
    }

    private void UpdateDragTooltip()
    {
        if (_dragTooltip.Content is TextBlock text)
        {
            text.Text = TimeSpanToStringConverter.Format(TimeSpan.FromSeconds(Value));
        }

        // Center the tooltip above the thumb position along the track.
        double ratio = Maximum > 0d ? Math.Clamp(Value / Maximum, 0d, 1d) : 0d;
        double thumbX = ratio * SeekBarSlider.ActualWidth;
        double tooltipWidth = _dragTooltip.ActualWidth > 0d ? _dragTooltip.ActualWidth : 48d;
        _dragTooltip.HorizontalOffset = thumbX - (tooltipWidth / 2d);
        _dragTooltip.VerticalOffset = -8d;
    }
}
