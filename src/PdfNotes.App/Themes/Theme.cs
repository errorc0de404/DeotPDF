using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace PdfNotes.App.Themes;

public enum AppTheme { Light, Dark }

/// <summary>
/// Design tokens and the runtime theme (spec 3.1·3.2, D13a). Every frame color, spacing, corner and font size is defined
/// once in the dictionaries of <c>Themes/</c>. Frame colors are used through <c>DynamicResource</c> (code: <c>SetResourceReference</c>),
/// so <see cref="Apply"/> swaps the palette and accent dictionaries while the app runs. Paper-side marks (Canvas.xaml) do not
/// depend on the theme; code draws them with <see cref="Brush"/>. Each UI thread gets its own copy (WPF styles belong to one thread).
/// </summary>
public static class Theme
{
    // Order of the merged dictionaries; only the palette and the accent entries are replaced.
    private const int PaletteIndex = 2, AccentIndex = 3;
    private const double LightSubtle = 0.16, DarkSubtle = 0.32;

    [ThreadStatic] private static ResourceDictionary? resources;
    [ThreadStatic] private static ResourceDictionary? accents, canvas, palette, accent;

    public static AppTheme Mode { get; private set; } = AppTheme.Light;
    public static AccentColor Accent { get; private set; } = AccentColor.Blue;
    public static ThemeChoice Choice { get; private set; } = ThemeChoice.System;

    /// <summary>Replaces the Windows app theme read from the registry (validation runs).</summary>
    public static Func<AppTheme>? SystemThemeOverride { get; set; }

    /// <summary>Raised after <see cref="Apply"/>; resource references update themselves, drawings cached in code repaint here.</summary>
    public static event EventHandler? Changed;

    /// <summary>Tokens, paper-side colors, the current palette and accent, and the common control styles.</summary>
    public static ResourceDictionary Resources => resources ??= Load();

    /// <summary>
    /// Makes the tokens and control styles visible to <paramref name="element"/>; call before its XAML loads. With an
    /// Application they are app-wide (dialogs, menus, tooltips); without one (tests) only for the element.
    /// </summary>
    public static void Attach(FrameworkElement element)
    {
        // Every window's title bar follows the theme (D13d); a window gets its handle after this call.
        if (element is Window window) window.SourceInitialized += (_, _) => SetTitleBar(window);
        var root = Resources;
        if (Application.Current is { } app && app.Dispatcher.CheckAccess())
        {
            if (!app.Resources.MergedDictionaries.Contains(root)) app.Resources.MergedDictionaries.Add(root);
        }
        else if (!element.Resources.MergedDictionaries.Contains(root)) element.Resources.MergedDictionaries.Add(root);
    }

    /// <summary>Font, text and background tokens of a window built in code (dialogs).</summary>
    public static void UseDialogTokens(Window window)
    {
        window.SetResourceReference(Control.FontFamilyProperty, "Font.Ui");
        window.SetResourceReference(Control.FontSizeProperty, "FontSize.Body");
        window.SetResourceReference(Control.BackgroundProperty, "Surface.Background");
        window.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
    }

    /// <summary>The Windows app theme (Settings › Personalization › Colors): dark when AppsUseLightTheme is 0.</summary>
    public static AppTheme SystemTheme()
    {
        if (SystemThemeOverride is { } read) return read();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return AppTheme.Light; }
    }

    /// <summary>Applies the theme setting: <see cref="ThemeChoice.System"/> uses the Windows app theme and keeps following it.</summary>
    public static void Use(ThemeChoice choice, AccentColor color)
    {
        Choice = choice;
        Apply(choice switch { ThemeChoice.Light => AppTheme.Light, ThemeChoice.Dark => AppTheme.Dark, _ => SystemTheme() }, color);
    }

    /// <summary>Windows changed its app theme (WM_SETTINGCHANGE "ImmersiveColorSet"): follow it when the setting says so.</summary>
    public static void SystemThemeChanged()
    {
        if (Choice == ThemeChoice.System) Apply(SystemTheme(), Accent);
    }

    /// <summary>The color of an accent in the current theme (settings window swatches).</summary>
    public static Color AccentOf(AccentColor color)
    {
        accents ??= Dictionary("Accents");
        return (Color)accents[$"Accent.{color}.{Mode}"];
    }

    /// <summary>Switches the palette and the accent color in place (every window updates).</summary>
    public static void Apply(AppTheme mode, AccentColor color)
    {
        if (mode == Mode && color == Accent) return;
        Mode = mode;
        Accent = color;
        if (palette is null) return;
        palette = Dictionary(mode.ToString());
        accent = AccentDictionary(palette);
        if (resources is { } root)
        {
            root.MergedDictionaries[PaletteIndex] = palette;
            root.MergedDictionaries[AccentIndex] = accent;
        }
        if (Application.Current is { } app && app.Dispatcher.CheckAccess())
            foreach (Window window in app.Windows) SetTitleBar(window);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private const int ImmersiveDarkMode = 20; // DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+, Windows 11)

    /// <summary>Dark or light title bar for the current theme; ignored where Windows does not support it.</summary>
    public static void SetTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        int dark = Mode == AppTheme.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref dark, sizeof(int)) == 0)
            SetWindowPos(handle, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); // no move/size/z-order/activate, frame changed: repaint now
    }

    /// <summary>Whether the window's title bar is dark, or null when Windows does not report it (validation).</summary>
    public static bool? IsTitleBarDark(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle != 0 && DwmGetWindowAttribute(handle, ImmersiveDarkMode, out int dark, sizeof(int)) == 0 ? dark != 0 : null;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    /// <summary>A color token for code drawing (frozen).</summary>
    /// <remarks>Reads only the color dictionaries, so it also works on a thread that never shows a window.</remarks>
    public static Brush Brush(string key)
    {
        LoadColors();
        return (canvas![key] ?? palette![key] ?? accent![key]) as SolidColorBrush ?? throw new KeyNotFoundException("Theme brush " + key);
    }

    public static Color Color(string key) => ((SolidColorBrush)Brush(key)).Color;

    private static void LoadColors()
    {
        if (palette is not null) return;
        canvas = Dictionary("Canvas");
        palette = Dictionary(Mode.ToString());
        accent = AccentDictionary(palette);
    }

    private static ResourceDictionary Load()
    {
        LoadColors();
        var root = new ResourceDictionary();
        root.MergedDictionaries.Add(Dictionary("Tokens"));
        root.MergedDictionaries.Add(canvas!);
        root.MergedDictionaries.Add(palette!);
        root.MergedDictionaries.Add(accent!);
        root.MergedDictionaries.Add(Dictionary("Controls"));
        return root;
    }

    private static ResourceDictionary Dictionary(string name) =>
        (ResourceDictionary)Application.LoadComponent(new Uri($"/DeotPDF;component/Themes/{name}.xaml", UriKind.Relative));

    /// <summary>Accent (the color), Accent.Subtle (checked/selected backgrounds: the accent over the bar color) and Accent.Foreground (text on the accent).</summary>
    private static ResourceDictionary AccentDictionary(ResourceDictionary colors)
    {
        accents ??= Dictionary("Accents");
        var color = (Color)accents[$"Accent.{Accent}.{Mode}"];
        var bar = ((SolidColorBrush)colors["Bar.Background"]).Color;
        return new ResourceDictionary
        {
            ["Accent"] = Frozen(color),
            ["Accent.Subtle"] = Frozen(Blend(color, bar, Mode == AppTheme.Light ? LightSubtle : DarkSubtle)),
            ["Accent.Foreground"] = Frozen((Color)accents[$"Accent.Foreground.{Mode}"]),
        };
    }

    private static Color Blend(Color top, Color bottom, double amount) => System.Windows.Media.Color.FromRgb(
        (byte)Math.Round(bottom.R + (top.R - bottom.R) * amount),
        (byte)Math.Round(bottom.G + (top.G - bottom.G) * amount),
        (byte)Math.Round(bottom.B + (top.B - bottom.B) * amount));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
