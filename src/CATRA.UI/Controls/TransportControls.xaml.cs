using System.Windows.Controls;

namespace CATRA.UI.Controls;

/// <summary>
/// Player transport bar (Tela 4): seek bar + time, play/pause/stop, volume,
/// skip intro (RN-04), transmit placeholder (ST-08), fullscreen and profile
/// indicator placeholder (ST-20). Binds to the inherited
/// <see cref="CATRA.UI.ViewModels.PlayerViewModel"/> data context.
/// </summary>
public partial class TransportControls : UserControl
{
    /// <summary>Creates the control.</summary>
    public TransportControls()
    {
        InitializeComponent();
    }
}
