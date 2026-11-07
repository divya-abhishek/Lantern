using System.Windows;
using System.Windows.Controls;
using Lantern.Desktop.ViewModels;

namespace Lantern.Desktop.Views;

/// <summary>
/// Screen Mirror settings. The values fed to the encoder are chosen here; the capability report
/// on the right is measured from this PC instead of being hard coded.
///
/// The settings card needs room for its option pills, so below the breakpoint the status card
/// moves underneath it instead of squeezing both and clipping the pills.
/// </summary>
public partial class MirrorView : UserControl
{
    private const double TwoColumnWidth = 1040;

    public MirrorView() => InitializeComponent();

    private LanternViewModel? ViewModel => DataContext as LanternViewModel;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout(e.NewSize.Width);

    private void ApplyLayout(double width)
    {
        var twoColumns = width >= TwoColumnWidth;

        SideColumn.Width = twoColumns ? new GridLength(400) : new GridLength(0);
        GapColumn.Width = twoColumns ? new GridLength(22) : new GridLength(0);
        StackRow.Height = twoColumns ? new GridLength(0) : GridLength.Auto;

        Grid.SetColumn(StatusCard, twoColumns ? 2 : 0);
        Grid.SetRow(StatusCard, twoColumns ? 0 : 1);
        StatusCard.Margin = twoColumns ? new Thickness(0) : new Thickness(0, 18, 0, 0);

        MainWindow.Note($"layout  mirror width={width:0} twoColumns={twoColumns} side={SideColumn.Width.Value:0}");
    }

    private void OnSourceLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: MirrorDisplayOption display } button)
        {
            button.IsChecked = Equals(ViewModel?.SelectedDisplay, display);
        }
    }

    private void OnSourceChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: MirrorDisplayOption display } && ViewModel is { } vm)
        {
            vm.SelectedDisplay = display;
        }
    }
}
