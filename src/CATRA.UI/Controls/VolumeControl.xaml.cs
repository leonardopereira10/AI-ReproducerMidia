using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CATRA.UI.Controls;

/// <summary>
/// Volume slider (0–100) with a mute toggle (Tela 4). A dumb binding shell:
/// the host binds <see cref="Volume"/>, <see cref="IsMuted"/> and
/// <see cref="MuteCommand"/> to the player view model.
/// </summary>
public partial class VolumeControl : UserControl
{
    /// <summary>Identifies the <see cref="Volume"/> dependency property (0–100).</summary>
    public static readonly DependencyProperty VolumeProperty =
        DependencyProperty.Register(nameof(Volume), typeof(double), typeof(VolumeControl), new PropertyMetadata(100d));

    /// <summary>Identifies the <see cref="IsMuted"/> dependency property.</summary>
    public static readonly DependencyProperty IsMutedProperty =
        DependencyProperty.Register(nameof(IsMuted), typeof(bool), typeof(VolumeControl), new PropertyMetadata(false));

    /// <summary>Identifies the <see cref="MuteCommand"/> dependency property.</summary>
    public static readonly DependencyProperty MuteCommandProperty =
        DependencyProperty.Register(nameof(MuteCommand), typeof(ICommand), typeof(VolumeControl), new PropertyMetadata(null));

    /// <summary>Creates the control.</summary>
    public VolumeControl()
    {
        InitializeComponent();
    }

    /// <summary>Volume percentage (0–100), two-way.</summary>
    public double Volume
    {
        get => (double)GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, value);
    }

    /// <summary>Whether audio is muted (drives the 🔊/🔇 icon).</summary>
    public bool IsMuted
    {
        get => (bool)GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    /// <summary>Toggles mute.</summary>
    public ICommand? MuteCommand
    {
        get => (ICommand?)GetValue(MuteCommandProperty);
        set => SetValue(MuteCommandProperty, value);
    }
}
