using System.Windows.Controls;

namespace CATRA.UI.Controls;

/// <summary>
/// Card for the job currently being processed (ST-19, Tela 3). Binds to the
/// inherited <c>ProcessingQueueViewModel</c> DataContext (EP label/title,
/// progress %, active step, ETA and the cancel command).
/// </summary>
public partial class ProcessJobCardControl : UserControl
{
    /// <summary>Creates the control.</summary>
    public ProcessJobCardControl()
    {
        InitializeComponent();
    }
}
