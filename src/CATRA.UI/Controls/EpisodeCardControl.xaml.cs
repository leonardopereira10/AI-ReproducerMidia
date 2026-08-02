using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CATRA.UI.Controls;

/// <summary>
/// Reusable episode card (Tela 2). The DataContext is an
/// <see cref="CATRA.Core.Library.EpisodeDetail"/> supplied by the host items
/// control; click and context-menu actions are routed through dependency
/// properties so the control stays free of view-model references.
/// </summary>
public partial class EpisodeCardControl : UserControl
{
    /// <summary>Identifies the <see cref="PlayCommand"/> dependency property.</summary>
    public static readonly DependencyProperty PlayCommandProperty =
        DependencyProperty.Register(nameof(PlayCommand), typeof(ICommand), typeof(EpisodeCardControl), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="ToggleWatchedCommand"/> dependency property.</summary>
    public static readonly DependencyProperty ToggleWatchedCommandProperty =
        DependencyProperty.Register(nameof(ToggleWatchedCommand), typeof(ICommand), typeof(EpisodeCardControl), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="RenameCommand"/> dependency property.</summary>
    public static readonly DependencyProperty RenameCommandProperty =
        DependencyProperty.Register(nameof(RenameCommand), typeof(ICommand), typeof(EpisodeCardControl), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="SetCoverCommand"/> dependency property.</summary>
    public static readonly DependencyProperty SetCoverCommandProperty =
        DependencyProperty.Register(nameof(SetCoverCommand), typeof(ICommand), typeof(EpisodeCardControl), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="DetailsCommand"/> dependency property.</summary>
    public static readonly DependencyProperty DetailsCommandProperty =
        DependencyProperty.Register(nameof(DetailsCommand), typeof(ICommand), typeof(EpisodeCardControl), new PropertyMetadata(null));

    /// <summary>Creates the control.</summary>
    public EpisodeCardControl()
    {
        InitializeComponent();
    }

    /// <summary>Left-click: play the episode (parameter: DataContext).</summary>
    public ICommand? PlayCommand
    {
        get => (ICommand?)GetValue(PlayCommandProperty);
        set => SetValue(PlayCommandProperty, value);
    }

    /// <summary>Context menu: mark/unmark watched (parameter: DataContext).</summary>
    public ICommand? ToggleWatchedCommand
    {
        get => (ICommand?)GetValue(ToggleWatchedCommandProperty);
        set => SetValue(ToggleWatchedCommandProperty, value);
    }

    /// <summary>Context menu: rename the episode (parameter: DataContext).</summary>
    public ICommand? RenameCommand
    {
        get => (ICommand?)GetValue(RenameCommandProperty);
        set => SetValue(RenameCommandProperty, value);
    }

    /// <summary>Context menu: configure the media item cover (no parameter).</summary>
    public ICommand? SetCoverCommand
    {
        get => (ICommand?)GetValue(SetCoverCommandProperty);
        set => SetValue(SetCoverCommandProperty, value);
    }

    /// <summary>Context menu: show episode details (parameter: DataContext).</summary>
    public ICommand? DetailsCommand
    {
        get => (ICommand?)GetValue(DetailsCommandProperty);
        set => SetValue(DetailsCommandProperty, value);
    }
}
