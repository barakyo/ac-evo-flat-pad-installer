using EvoMods.App.Pages;

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.UI;

namespace EvoMods.App;

/// <summary>
/// The shell. Holds navigation and nothing else — every screen is a page.
/// </summary>
/// <remarks>
/// Deliberately introduced at the second screen rather than the first: a shell designed against one
/// page bakes in that page's shape, and these two are different in kind (a status readout and a list
/// that acts on the game).
/// </remarks>
public sealed partial class MainWindow : Window
{
    /// <summary>Comfortable size in DIPs, sized to the widest screen rather than to the display.</summary>
    /// <remarks>
    /// The Camera page is the constraint: a 210 label, a 300 slider, a 96 readout and the gaps
    /// between them, plus page padding and the 190 pane. Everything else fits inside that.
    /// </remarks>
    private const int PreferredWidth = 1040;
    private const int PreferredHeight = 760;

    // DllImport rather than LibraryImport: the source-generated form requires AllowUnsafeBlocks,
    // which is a lot of blast radius to take on for one call that returns a number.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    public MainWindow()
    {
        InitializeComponent();
        Title = "EvoMods Manager";
        BrandTitleBar();
        RightSize();
    }

    /// <summary>Open at a sensible size, centred, rather than at whatever WinUI picks.</summary>
    /// <remarks>
    /// ⚠️ <see cref="AppWindow.Resize"/> takes PHYSICAL pixels, not DIPs. Passing a comfortable
    /// number straight in gives a window that is right on a 100% display and progressively tinier as
    /// scaling goes up — the opposite of what it looks like it does. Hence the DPI scale.
    /// </remarks>
    private void RightSize()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0)
            scale = 1;

        var size = new Windows.Graphics.SizeInt32(
            (int)(PreferredWidth * scale), (int)(PreferredHeight * scale));
        AppWindow.Resize(size);

        // Keep it from being dragged smaller than its own content.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(880 * scale);
            presenter.PreferredMinimumHeight = (int)(560 * scale);
        }

        // Centre on the work area, so it does not land under the taskbar on a short display.
        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        AppWindow.Move(new Windows.Graphics.PointInt32(
            display.WorkArea.X + ((display.WorkArea.Width - size.Width) / 2),
            display.WorkArea.Y + ((display.WorkArea.Height - size.Height) / 2)));
    }

    /// <summary>Paint the caption bar in the brand's own dark, rather than the system's.</summary>
    /// <remarks>
    /// The title bar is drawn by Windows and follows the OS light/dark setting, so on a machine set
    /// to light it stays light no matter how dark the app is — a white strip above a near-black
    /// window. An app has to opt out of that explicitly; nothing about RequestedTheme reaches it.
    /// <para>
    /// Guarded because customisation is not available everywhere. Where it is not, the default bar is
    /// mismatched but perfectly usable, which is a better outcome than a crash on startup.
    /// </para>
    /// </remarks>
    private void BrandTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        AppWindowTitleBar bar = AppWindow.TitleBar;
        bar.BackgroundColor = Hex(0x0D, 0x0F, 0x15);
        bar.ForegroundColor = Hex(0xEB, 0xEF, 0xF4);
        bar.InactiveBackgroundColor = Hex(0x0D, 0x0F, 0x15);
        bar.InactiveForegroundColor = Hex(0x6C, 0x76, 0x86);

        // ⚠️ The bar's own colour, NOT Colors.Transparent. Transparent means "show whatever is
        // behind", and with no system backdrop set there is nothing behind, so the caption buttons
        // composite to white — a light block in the corner of an otherwise dark window, which is
        // exactly what it looked like.
        bar.ButtonBackgroundColor = Hex(0x0D, 0x0F, 0x15);
        bar.ButtonInactiveBackgroundColor = Hex(0x0D, 0x0F, 0x15);
        bar.ButtonForegroundColor = Hex(0xEB, 0xEF, 0xF4);
        bar.ButtonInactiveForegroundColor = Hex(0x6C, 0x76, 0x86);
        bar.ButtonHoverBackgroundColor = Hex(0x20, 0x24, 0x2C);
        bar.ButtonHoverForegroundColor = Hex(0xEB, 0xEF, 0xF4);
        bar.ButtonPressedBackgroundColor = Hex(0x26, 0x29, 0x2F);
        bar.ButtonPressedForegroundColor = Hex(0xEB, 0xEF, 0xF4);
    }

    private static Color Hex(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    private void OnNavLoaded(object sender, RoutedEventArgs e) =>
        Nav.SelectedItem = Nav.MenuItems[0];

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is not NavigationViewItem item)
            return;

        ContentFrame.Navigate(item.Tag switch
        {
            "game" => typeof(GamePage),
            "flatpad" => typeof(FlatPadPage),
            "tracks" => typeof(TracksPage),
            "filters" => typeof(FiltersPage),
            "camera" => typeof(CameraPage),
            _ => typeof(HomePage),
        });
    }
}
