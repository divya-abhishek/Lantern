using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lantern.Desktop.ViewModels;

namespace Lantern.Desktop.Views;

/// <summary>Library grid, search/sort pills and the detail panel.</summary>
public partial class LibraryView : UserControl
{
    /// <summary>Below this width the detail panel gets narrower instead of the grid collapsing.</summary>
    private const double TwoColumnWidth = 1040;

    public LibraryView() => InitializeComponent();

    private LanternViewModel? ViewModel => DataContext as LanternViewModel;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout(e.NewSize.Width);

    /// <summary>
    /// The library keeps its two panes at every size: the detail panel narrows, and the search box
    /// drops under the title instead of squeezing it.
    /// </summary>
    private void ApplyLayout(double width)
    {
        var twoColumns = width >= TwoColumnWidth;

        SideColumn.Width = new GridLength(twoColumns ? 380 : 320);
        GapColumn.Width = new GridLength(twoColumns ? 22 : 14);
        StackRow.Height = new GridLength(0);

        // Search: beside the title when there is room, underneath it when there is not.
        SearchColumn.Width = twoColumns ? GridLength.Auto : new GridLength(0);
        SearchRow.Height = twoColumns ? new GridLength(0) : GridLength.Auto;
        Grid.SetColumn(SearchPanel, twoColumns ? 1 : 0);
        Grid.SetRow(SearchPanel, twoColumns ? 0 : 1);

        SearchPanel.Width = twoColumns ? 360 : double.NaN;
        SearchPanel.Margin = twoColumns ? new Thickness(16, 8, 0, 0) : new Thickness(0, 16, 0, 0);

        MainWindow.Note(
            $"layout  library width={width:0} twoColumns={twoColumns} " +
            $"searchRow={Grid.GetRow(SearchPanel)} side={SideColumn.Width.Value:0}");
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if (ViewModel?.SearchCommand.CanExecute(null) == true) ViewModel.SearchCommand.Execute(null);
    }

    /// <summary>
    /// The empty library offers the one action that helps: clear the filter when folders exist,
    /// otherwise go straight to Settings to add one.
    /// </summary>
    private void OnEmptyAction(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        if (!vm.HasFolders)
        {
            (Application.Current.MainWindow as MainWindow)?.GoTo("settings");
            return;
        }

        if (vm.ClearSearchCommand.CanExecute(null)) vm.ClearSearchCommand.Execute(null);
    }

    private void OnKindMovies(object sender, RoutedEventArgs e) => SetKind("movies");

    private void OnKindShows(object sender, RoutedEventArgs e) => SetKind("tv");

    private void OnKindAll(object sender, RoutedEventArgs e) => SetKind("all");

    private void OnSortAdded(object sender, RoutedEventArgs e) => SetSort("added");

    private void OnSortName(object sender, RoutedEventArgs e) => SetSort("name");

    private void OnSortYear(object sender, RoutedEventArgs e) => SetSort("year");

    private void SetKind(string kind)
    {
        if (ViewModel is { } vm) vm.SelectedKind = kind;
    }

    private void SetSort(string sort)
    {
        if (ViewModel is { } vm) vm.SelectedSort = sort;
    }
}
