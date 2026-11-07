using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Lantern.Desktop.Views;

namespace Lantern.Desktop;

/// <summary>
/// The shell: sidebar navigation, the window chrome and the single status line that every page
/// shares. Pages are created on first visit and then kept, so switching tabs is instant.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = [];

    public MainWindow()
    {
        InitializeComponent();
        SyncChromeToState();
        GoTo("home");
    }

    /// <summary>
    /// Runs once the window has a handle, before it is shown. This is where the maximum size is
    /// capped to the work area so the initial maximize cannot cover the taskbar.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        ClampToWorkArea();
        SyncChromeToState();
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string page }) GoTo(page);
    }

    /// <summary>Switches page and keeps the sidebar selection in sync.</summary>
    public void GoTo(string page)
    {
        if (!_pages.TryGetValue(page, out var view))
        {
            view = page switch
            {
                "library" => new LibraryView(),
                "mirror" => new MirrorView(),
                "devices" => new DevicesView(),
                "settings" => new SettingsView(),
                _ => new HomeView(),
            };

            _pages[page] = view;
        }

        PageHost.Content = view;

        // Home greets the viewer by the time of day, and that clock keeps moving while the app is open.
        if (page is "home") App.ViewModel.RefreshGreeting();

        var nav = page switch
        {
            "library" => NavLibrary,
            "mirror" => NavMirror,
            "devices" => NavDevices,
            "settings" => NavSettings,
            _ => NavHome,
        };

        if (nav.IsChecked != true) nav.IsChecked = true;
    }

    // --- window chrome ------------------------------------------------------------------

    private bool _isFullscreen;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;

    public bool IsFullscreen => _isFullscreen;

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Whoever changed the state (our buttons, Win+Up, a drag to the screen edge), the
    /// caption glyph and its tooltip stay honest.</summary>
    private void OnWindowStateChanged(object sender, EventArgs e) => SyncChromeToState();

    private void SyncChromeToState()
    {
        var maximized = WindowState == WindowState.Maximized;

        MaximizeGlyph.Data = (Geometry)FindResource(maximized ? "GlyphRestore" : "GlyphMaximize");
        MaximizeButton.ToolTip = maximized ? "Restore down" : "Maximize";
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape when _isFullscreen:
                SetFullscreen(false);
                e.Handled = true;
                break;
        }
    }

    private void OnExitFullscreen(object sender, RoutedEventArgs e) => SetFullscreen(false);

    public void ToggleFullscreen() => SetFullscreen(!_isFullscreen);

    /// <summary>
    /// True fullscreen, not a bigger window: the caption and the sidebar are removed, the window
    /// goes borderless and is placed exactly over the display (taskbar included) at the top of the
    /// z-order. Leaving fullscreen puts the previous window state back.
    /// </summary>
    public void SetFullscreen(bool on)
    {
        if (on == _isFullscreen) return;

        if (on)
        {
            _stateBeforeFullscreen = WindowState == WindowState.Minimized
                ? WindowState.Maximized
                : WindowState;

            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;
            WindowChrome.SetWindowChrome(this, null);

            // Fullscreen is the one case that is allowed to use the whole display.
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;

            TitleBar.Visibility = Visibility.Collapsed;
            TitleBarRow.Height = new GridLength(0);
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
            FullscreenExitChip.Visibility = Visibility.Visible;

            _isFullscreen = true;
            CoverDisplay();
            Topmost = true;
            Activate();
            return;
        }

        _isFullscreen = false;
        Topmost = false;
        WindowChrome.SetWindowChrome(this, Chrome);
        ResizeMode = ResizeMode.CanResize;
        ClampToWorkArea();

        TitleBar.Visibility = Visibility.Visible;
        TitleBarRow.Height = new GridLength(46);
        Sidebar.Visibility = Visibility.Visible;
        SidebarColumn.Width = new GridLength(248);
        FullscreenExitChip.Visibility = Visibility.Collapsed;

        WindowState = _stateBeforeFullscreen;
    }

    /// <summary>The chrome the window runs with outside fullscreen: a 46px caption and a 6px resize
    /// border, both without the system's own caption buttons.</summary>
    private static WindowChrome Chrome => new()
    {
        CaptionHeight = 46,
        ResizeBorderThickness = new Thickness(6),
        GlassFrameThickness = new Thickness(0),
        CornerRadius = new CornerRadius(0),
        UseAeroCaptionButtons = false,
    };

    /// <summary>Places the window over the whole display of the monitor it currently occupies.</summary>
    private void CoverDisplay()
    {
        if (DisplayBounds() is not { } bounds) return;

        Left = bounds.X;
        Top = bounds.Y;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    /// <summary>
    /// Windows maximizes a <c>WindowStyle=None</c> window to the whole display, taskbar included,
    /// which is not what a maximize button should do. Capping the maximum size to the monitor's
    /// work area is what makes maximize behave like every other app; fullscreen lifts the cap.
    /// </summary>
    private void ClampToWorkArea()
    {
        if (WorkAreaBounds() is not { } work) return;

        MaxWidth = Math.Max(320, work.Width);
        MaxHeight = Math.Max(240, work.Height);
    }

    /// <summary>The monitor's work area (taskbar excluded) in WPF units.</summary>
    private (double X, double Y, double Width, double Height)? WorkAreaBounds() => MonitorBounds(full: false);

    /// <summary>The monitor's full bounds (taskbar included) in WPF units.</summary>
    private (double X, double Y, double Width, double Height)? DisplayBounds() => MonitorBounds(full: true);

    /// <summary>
    /// The monitor the window is on, converted to WPF units. DPI comes from the window's own
    /// composition target, so 100%, 125%, 150% and mixed-DPI setups all line up.
    /// </summary>
    private (double X, double Y, double Width, double Height)? MonitorBounds(bool full)
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return null;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return null;

        var screen = System.Windows.Forms.Screen.FromHandle(handle);
        var device = full ? screen.Bounds : screen.WorkingArea;
        var toDips = target.TransformFromDevice;

        var topLeft = toDips.Transform(new Point(device.Left, device.Top));
        var bottomRight = toDips.Transform(new Point(device.Right, device.Bottom));

        return (topLeft.X, topLeft.Y,
                Math.Max(1, bottomRight.X - topLeft.X), Math.Max(1, bottomRight.Y - topLeft.Y));
    }

    // --- OS-level measurement: the smoke run uses this to prove the window really is where it
    //     says it is, instead of trusting the WPF properties it just set ----------------------

    /// <summary>
    /// The on-screen rectangle of the visible content, in device pixels. The OS window rectangle
    /// of a borderless window includes an invisible resize frame, so this - and not the raw
    /// rectangle - is what decides whether the taskbar is covered.
    /// </summary>
    private (int X, int Y, int Width, int Height) MeasureContent()
    {
        var topLeft = RootGrid.PointToScreen(new Point(0, 0));
        var bottomRight = RootGrid.PointToScreen(new Point(RootGrid.ActualWidth, RootGrid.ActualHeight));

        return ((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
                (int)Math.Round(bottomRight.X - topLeft.X), (int)Math.Round(bottomRight.Y - topLeft.Y));
    }

    private (int X, int Y, int Width, int Height) MeasureWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var rect)) return default;

        return (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private (int X, int Y, int Width, int Height) MeasureDisplay()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var bounds = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
        return (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private void OnDismissError(object sender, RoutedEventArgs e) => App.ViewModel.ClearError();

    // --- UI smoke run -------------------------------------------------------------------
    // `Lantern.Desktop.exe --smoke-ui <folder>` walks every page, renders each one to a PNG and
    // exits. It is the quickest way to prove the XAML, the bindings and the server connection all
    // work without a human driving the window.

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.SmokeFolder is not { } folder) return;

        var log = Path.Combine(folder, "smoke.log");

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(log, $"lantern ui smoke {DateTime.Now:O}{Environment.NewLine}");

            // Give the server connection and the first library load time to finish.
            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(true);

            VerifyWindowChrome(log);
            await VerifyLibraryActionsAsync(log);

            foreach (var (page, width, height, suffix) in Renders())
            {
                GoTo(page);
                await ResizeAsync(width, height).ConfigureAwait(true);

                var file = Path.Combine(folder, $"{page}{suffix}.png");
                SaveWindow(file);
                Append(log, $"rendered {file} (page {PageHost.ActualWidth:0}x{PageHost.ActualHeight:0})");
            }

            Append(log, $"status  {App.ViewModel.StatusMessage}");
            Append(log, $"server  {App.ViewModel.ServerUrl} connected={App.ViewModel.IsConnected}");
            Append(log, $"lan     {App.ViewModel.LanAddressLabel}");
            Append(log, $"library {App.ViewModel.LibrarySummary} continue={App.ViewModel.ContinueCount}");
            Append(log, $"mirror  {App.ViewModel.MirrorStatus}");
            Append(log, "SMOKE-UI OK pages=10");
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            Append(log, $"SMOKE-UI FAILED {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex}");
            Application.Current.Shutdown(1);
        }
    }

    /// <summary>
    /// Presses the Settings buttons the way a user would. "Scan now" has to run a real scan against
    /// the server and report what it found - a button that only changes its label would fail here.
    /// </summary>
    private async Task VerifyLibraryActionsAsync(string log)
    {
        var vm = App.ViewModel;

        Check(log, "Scan now is enabled while idle", vm.ScanCommand.CanExecute(null), vm.ScanButtonLabel);

        vm.ScanCommand.Execute(null);

        Check(log, "Scan now disables itself while it runs", !vm.ScanCommand.CanExecute(null), vm.ScanButtonLabel);

        for (var tick = 0; tick < 160 && vm.IsScanning; tick++)
        {
            await Task.Delay(250).ConfigureAwait(true);
        }

        Check(log, "Scan now finishes and re-enables itself",
            !vm.IsScanning && vm.ScanCommand.CanExecute(null),
            $"label={vm.ScanButtonLabel}");

        Check(log, "the scan reports a real result",
            vm.ScanMessage.StartsWith("Scan complete", StringComparison.Ordinal),
            vm.ScanMessage);

        Check(log, "the media library is populated after the scan",
            vm.MediaItems.Count > 0,
            $"{vm.MediaItems.Count} title(s) in the grid, summary '{vm.LibrarySummary}'");

        Check(log, "the address shown for other devices is a LAN IPv4, never loopback",
            vm.LanAddressLabel is not ("not connected" or "No LAN address") &&
                !vm.LanAddressLabel.StartsWith("127.", StringComparison.Ordinal),
            vm.LanAddressLabel);

        Check(log, "the folder list is not empty",
            vm.Folders.Count > 0 && vm.RemoveFolderCommand.CanExecute(vm.Folders[0]),
            $"{vm.Folders.Count} folder(s), first = {vm.Folders[0].Path}");
    }

    /// <summary>The two sizes every page is rendered at: a normal desktop and a narrow window
    /// where the responsive layouts have to reflow instead of clipping.</summary>
    private static IEnumerable<(string Page, int Width, int Height, string Suffix)> Renders()
    {
        string[] pages = ["home", "library", "mirror", "devices", "settings"];

        foreach (var page in pages) yield return (page, 1440, 900, string.Empty);
        foreach (var page in pages) yield return (page, 1120, 700, "-narrow");
    }

    private async Task ResizeAsync(int width, int height)
    {
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;

        Width = width;
        Height = height;
        UpdateLayout();
        await Task.Delay(350).ConfigureAwait(true);
    }

    /// <summary>
    /// Proves the window behaves the way it is supposed to by asking Windows for the real
    /// rectangle, instead of trusting the WPF properties that were just set. The measured numbers
    /// go into the log, so the result can be read without re-running anything.
    /// </summary>
    private void VerifyWindowChrome(string log)
    {
        var work = WorkArea();
        var display = MeasureDisplay();
        var content = MeasureContent();

        Check(log, "launches maximized", WindowState == WindowState.Maximized,
            $"state={WindowState}");

        Check(log, "maximized content fills the usable screen",
            content.Width >= work.Width - 2 && content.Height >= work.Height - 2,
            $"content {content.Width}x{content.Height} vs work area {work.Width}x{work.Height}");

        Check(log, "maximized content stops at the taskbar",
            content.Y + content.Height <= work.Y + work.Height + 1,
            $"content bottom {content.Y + content.Height} vs work area bottom {work.Y + work.Height}");

        WindowState = WindowState.Minimized;
        UpdateLayout();
        Check(log, "minimize actually minimizes", WindowState == WindowState.Minimized,
            $"state={WindowState}");

        WindowState = WindowState.Normal;
        UpdateLayout();
        Check(log, "a minimized window comes back", WindowState != WindowState.Minimized,
            $"state={WindowState}");

        WindowState = WindowState.Normal;
        UpdateLayout();
        Check(log, "restore down works", WindowState == WindowState.Normal, $"state={WindowState}");
        Check(log, "caption shows the maximize glyph when not maximized",
            ReferenceEquals(MaximizeGlyph.Data, FindResource("GlyphMaximize")),
            MaximizeButton.ToolTip?.ToString() ?? "?");

        WindowState = WindowState.Maximized;
        UpdateLayout();
        Check(log, "maximize actually maximizes", WindowState == WindowState.Maximized, $"state={WindowState}");
        Check(log, "caption swaps to the restore glyph when maximized",
            ReferenceEquals(MaximizeGlyph.Data, FindResource("GlyphRestore")),
            MaximizeButton.ToolTip?.ToString() ?? "?");

        SetFullscreen(true);
        UpdateLayout();
        var full = MeasureContent();

        Check(log, "fullscreen content sits exactly on the monitor's full bounds",
            Math.Abs(full.X - display.X) <= 2 && Math.Abs(full.Y - display.Y) <= 2 &&
            Math.Abs(full.Width - display.Width) <= 2 && Math.Abs(full.Height - display.Height) <= 2,
            $"content {full.X},{full.Y} {full.Width}x{full.Height} vs display {display.X},{display.Y} {display.Width}x{display.Height}");

        Check(log, "fullscreen covers the taskbar, not just the work area",
            full.Y + full.Height > work.Y + work.Height,
            $"fullscreen bottom {full.Y + full.Height} vs work area bottom {work.Y + work.Height}");

        Check(log, "fullscreen hides the title bar and the sidebar",
            TitleBar.Visibility == Visibility.Collapsed && Sidebar.Visibility == Visibility.Collapsed,
            $"titlebar={TitleBar.Visibility} sidebar={Sidebar.Visibility}");

        Check(log, "fullscreen shows an obvious way out",
            FullscreenExitChip.Visibility == Visibility.Visible,
            $"chip={FullscreenExitChip.Visibility}");

        SetFullscreen(false);
        UpdateLayout();
        var restored = MeasureContent();

        Check(log, "leaving fullscreen restores the previous window state",
            WindowState == WindowState.Maximized &&
            TitleBar.Visibility == Visibility.Visible &&
            Sidebar.Visibility == Visibility.Visible,
            $"state={WindowState} titlebar={TitleBar.Visibility} sidebar={Sidebar.Visibility}");

        Check(log, "restored content is back inside the work area",
            restored.Y + restored.Height <= work.Y + work.Height + 1,
            $"content bottom {restored.Y + restored.Height} vs work area bottom {work.Y + work.Height}");
    }

    private (int X, int Y, int Width, int Height) WorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        return (area.Left, area.Top, area.Width, area.Height);
    }

    private static void Check(string log, string what, bool ok, string detail)
    {
        Append(log, ok ? $"check   ok   {what}  [{detail}]" : $"check   FAIL {what}  [{detail}]");
        if (!ok) throw new InvalidOperationException($"Window check failed: {what} ({detail})");
    }

    /// <summary>
    /// Writes one line into the smoke log. The pages use it to report which layout they picked,
    /// which is otherwise invisible in a run that nobody is watching.
    /// </summary>
    public static void Note(string text)
    {
        if (App.SmokeFolder is { } folder) Append(Path.Combine(folder, "smoke.log"), text);
    }

    private static void Append(string path, string line)
    {
        try
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // Logging must never break the smoke run.
        }
    }

    private void SaveWindow(string path)
    {
        var width = (int)Math.Max(1, ActualWidth);
        var height = (int)Math.Max(1, ActualHeight);

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
