using System.Windows.Controls;

namespace CATRA.UI.Controls;

/// <summary>
/// DLNA cast button + device dropdown (ST-08, RF-06). Idle: "📺 Transmitir"
/// opens the discovered-renderer list ("🔍 Procurando..." during discovery,
/// 10s retry while open). While casting: "📺 {device}" opens remote
/// Play/Pause/Stop + volume. Binds to the inherited
/// <see cref="CATRA.UI.ViewModels.PlayerViewModel"/> data context.
/// </summary>
public partial class CastButtonControl : UserControl
{
    /// <summary>Creates the control.</summary>
    public CastButtonControl()
    {
        InitializeComponent();
    }
}
