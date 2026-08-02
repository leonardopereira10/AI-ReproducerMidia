using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CATRA.UI.Controls;

/// <summary>
/// Reusable media card (Tela 1). The DataContext is a
/// <see cref="CATRA.Core.Library.MediaItemSummary"/> supplied by the host
/// items control; the click is routed through <see cref="Command"/>.
/// </summary>
public partial class MediaCardControl : UserControl
{
    /// <summary>Identifies the <see cref="Command"/> dependency property.</summary>
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(
            nameof(Command),
            typeof(ICommand),
            typeof(MediaCardControl),
            new PropertyMetadata(null));

    /// <summary>Creates the control.</summary>
    public MediaCardControl()
    {
        InitializeComponent();
    }

    /// <summary>Command invoked when the card is clicked (parameter: DataContext).</summary>
    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }
}
