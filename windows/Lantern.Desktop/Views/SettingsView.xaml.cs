using System.Windows;
using System.Windows.Controls;

namespace Lantern.Desktop.Views;

/// <summary>
/// Folders, server state and the About panel.
///
/// The layout is responsive: two columns while there is room for them, one column below the
/// breakpoint, so the folder card never squeezes its buttons into the card edge.
/// </summary>
public partial class SettingsView : UserControl
{
    /// <summary>Below this width the side panel moves under the folder card.</summary>
    private const double TwoColumnWidth = 1040;

    public SettingsView() => InitializeComponent();

    /// <summary>Connecting a TV or projector lives on the Devices page, so Settings only points there.</summary>
    private void OnOpenDevices(object sender, RoutedEventArgs e) =>
        (System.Windows.Application.Current.MainWindow as MainWindow)?.GoTo("devices");

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout(e.NewSize.Width);

    private void ApplyLayout(double width)
    {
        var twoColumns = width >= TwoColumnWidth;

        SideColumn.Width = twoColumns ? new GridLength(400) : new GridLength(0);
        GapColumn.Width = twoColumns ? new GridLength(22) : new GridLength(0);
        StackRow.Height = twoColumns ? new GridLength(0) : GridLength.Auto;

        Grid.SetColumn(ServerAndAbout, twoColumns ? 2 : 0);
        Grid.SetRow(ServerAndAbout, twoColumns ? 0 : 1);
        ServerAndAbout.Margin = twoColumns ? new Thickness(0) : new Thickness(0, 18, 0, 0);

        MainWindow.Note($"layout  settings width={width:0} twoColumns={twoColumns} side={SideColumn.Width.Value:0}");
    }
}
