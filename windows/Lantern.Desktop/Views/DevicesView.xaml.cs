using System.Windows;
using System.Windows.Controls;
using Lantern.Core.Dtos;
using Lantern.Desktop.ViewModels;

namespace Lantern.Desktop.Views;

/// <summary>
/// Pairing, discovery and the trusted-device list.
///
/// Below the breakpoint the trusted-device panel moves under the pairing card, so the discovery
/// list and the address block never get squeezed.
/// </summary>
public partial class DevicesView : UserControl
{
    private const double TwoColumnWidth = 1040;

    public DevicesView() => InitializeComponent();

    private LanternViewModel? ViewModel => DataContext as LanternViewModel;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout(e.NewSize.Width);

    private void ApplyLayout(double width)
    {
        var twoColumns = width >= TwoColumnWidth;

        SideColumn.Width = twoColumns ? new GridLength(420) : new GridLength(0);
        GapColumn.Width = twoColumns ? new GridLength(22) : new GridLength(0);
        StackRow.Height = twoColumns ? new GridLength(0) : GridLength.Auto;

        Grid.SetColumn(TrustedCard, twoColumns ? 2 : 0);
        Grid.SetRow(TrustedCard, twoColumns ? 0 : 1);
        TrustedCard.Margin = twoColumns ? new Thickness(0) : new Thickness(0, 18, 0, 0);

        MainWindow.Note($"layout  devices width={width:0} twoColumns={twoColumns} side={SideColumn.Width.Value:0}");
    }



    /// <summary>The address box is hidden until the viewer asks for it, the same way the Android
    /// Devices page hides it: nobody has to read an address unless they are typing one.</summary>


    /// <summary>Connects to a server the UDP discovery probe found on the LAN.</summary>

}
