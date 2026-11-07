using System.Windows.Controls;

namespace Lantern.Desktop.Views;

/// <summary>Home: the two core functions, the connected device and the resume shelf.</summary>
public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private void OnOpenMedia(object sender, System.Windows.RoutedEventArgs e) =>
        (System.Windows.Application.Current.MainWindow as MainWindow)?.GoTo("library");

    private void OnOpenMirror(object sender, System.Windows.RoutedEventArgs e) =>
        (System.Windows.Application.Current.MainWindow as MainWindow)?.GoTo("mirror");

    private void OnOpenDevices(object sender, System.Windows.RoutedEventArgs e) =>
        (System.Windows.Application.Current.MainWindow as MainWindow)?.GoTo("devices");
}
