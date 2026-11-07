using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lantern.Desktop.ViewModels;

namespace Lantern.Desktop;

/// <summary>
/// The WPF entry point. One view model instance is shared by the whole shell: the pages are
/// just different views onto the same server connection.
/// </summary>
public partial class App : Application
{
    public static LanternViewModel ViewModel { get; private set; } = null!;

    /// <summary>
    /// Set by <c>--smoke-ui &lt;folder&gt;</c>: the shell then walks every page, renders each one to a
    /// PNG and exits. It is how the UI is verified without a human at the keyboard.
    /// </summary>
    public static string? SmokeFolder { get; private set; }

    /// <summary>
    /// Set by <c>--export-icon &lt;file.png&gt;</c>: writes the square master mark to a PNG at 1024px
    /// and exits, so the artwork can be inspected or handed to another tool. The icon that ships is
    /// generated from the same master by <c>tools\assets\build-lantern-assets.ps1</c>.
    /// </summary>
    public static string? IconFolder { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ParseArguments(e.Args);

        if (IconFolder is { } iconFile)
        {
            // A one-shot render: no shell window, no server, nothing to keep alive.
            ExportIcon(iconFile);
            Shutdown(0);
            return;
        }

        ViewModel = new LanternViewModel();

        // In smoke mode an exception must abort the run (non-zero exit) instead of being hidden.
        if (SmokeFolder is null)
        {
            DispatcherUnhandledException += (_, args) =>
            {
                ViewModel.ReportError(args.Exception.Message);
                args.Handled = true;
            };
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private static void ParseArguments(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--smoke-ui", StringComparison.OrdinalIgnoreCase))
            {
                SmokeFolder = i + 1 < args.Length
                    ? Path.GetFullPath(args[i + 1])
                    : Path.Combine(Path.GetTempPath(), "lantern-ui-smoke");
                continue;
            }

            if (string.Equals(args[i], "--export-icon", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                IconFolder = Path.GetFullPath(args[i + 1]);
            }
        }
    }

    /// <summary>
    /// Writes the app's square master mark to a transparent PNG. The artwork travels as a WPF
    /// resource (Assets/lantern.png), so this is a straight render of it - the file it produces is
    /// the same drawing the UI shows and the same one the .ico is built from.
    /// </summary>
    private static void ExportIcon(string path)
    {
        const int size = 1024;

        var source = (BitmapSource)Current.FindResource("LanternLogo");

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, size, size));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ViewModel?.Dispose();
        base.OnExit(e);
    }
}
