using System.Windows;
using System.Windows.Controls;
using CATRA.Core.Enums;

namespace CATRA.UI.Controls;

/// <summary>
/// Local/DLNA profile toggle (ST-19, Tela 2). Two radio buttons styled as a
/// pill; the <see cref="SelectedProfile"/> dependency property is the single
/// source of truth so the detail view model can bind it two-way. The control is
/// disabled by the host while a window is active.
/// </summary>
public partial class ProfileRadioSwitch : UserControl
{
    /// <summary>Identifies the <see cref="SelectedProfile"/> dependency property.</summary>
    public static readonly DependencyProperty SelectedProfileProperty =
        DependencyProperty.Register(
            nameof(SelectedProfile),
            typeof(ProcessProfile),
            typeof(ProfileRadioSwitch),
            new FrameworkPropertyMetadata(
                ProcessProfile.Local,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSelectedProfileChanged));

    private bool _syncing;

    /// <summary>Creates the control.</summary>
    public ProfileRadioSwitch()
    {
        InitializeComponent();
        SyncRadios();
    }

    /// <summary>The selected pre-processing profile (Local/DLNA).</summary>
    public ProcessProfile SelectedProfile
    {
        get => (ProcessProfile)GetValue(SelectedProfileProperty);
        set => SetValue(SelectedProfileProperty, value);
    }

    private void LocalRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        SelectedProfile = ProcessProfile.Local;
    }

    private void DlnaRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        SelectedProfile = ProcessProfile.Dlna;
    }

    private static void OnSelectedProfileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ProfileRadioSwitch control)
        {
            control.SyncRadios();
        }
    }

    private void SyncRadios()
    {
        if (LocalRadio is null || DlnaRadio is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            LocalRadio.IsChecked = SelectedProfile == ProcessProfile.Local;
            DlnaRadio.IsChecked = SelectedProfile == ProcessProfile.Dlna;
        }
        finally
        {
            _syncing = false;
        }
    }
}
