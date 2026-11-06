using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using PdfNotes.Editor.Services;

namespace PdfNotes.UI.Themes;

/// <summary>
/// The theme setting (spec 3.2) on Avalonia: light, dark or the system's (Avalonia follows the OS itself), and the accent
/// color. The palette lives in Themes/Colors.axaml; this sets the theme variant and the three accent brushes
/// (<c>Accent</c>, <c>Accent.Subtle</c>, <c>Accent.Foreground</c>) for the variant actually shown, as the WPF Theme does.
/// </summary>
public static class DesktopTheme
{
    private const double LightSubtle = 0.16, DarkSubtle = 0.32;

    public static ThemeChoice Choice { get; private set; } = ThemeChoice.System;
    public static AccentColor Accent { get; private set; } = AccentColor.Blue;

    public static void Use(Application app, ThemeChoice choice, AccentColor accent)
    {
        Choice = choice;
        Accent = accent;
        app.ActualThemeVariantChanged -= Refresh;
        app.ActualThemeVariantChanged += Refresh;
        app.RequestedThemeVariant = choice switch { ThemeChoice.Light => ThemeVariant.Light, ThemeChoice.Dark => ThemeVariant.Dark, _ => ThemeVariant.Default };
        SetAccent(app);
    }

    public static bool IsDark(Application app) => app.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>The color of an accent in the theme shown now (settings window swatches).</summary>
    public static Color AccentOf(Application app, AccentColor accent) =>
        app.TryGetResource($"Accent.{accent}.{(IsDark(app) ? "Dark" : "Light")}", null, out var value) && value is Color c ? c : Colors.DodgerBlue;

    private static void Refresh(object? sender, EventArgs e) { if (sender is Application app) SetAccent(app); }

    private static void SetAccent(Application app)
    {
        bool dark = IsDark(app);
        var color = AccentOf(app, Accent);
        var foreground = app.TryGetResource($"Accent.Foreground.{(dark ? "Dark" : "Light")}", null, out var f) && f is Color fc ? fc : Colors.White;
        app.Resources["Accent"] = new SolidColorBrush(color);
        app.Resources["Accent.Subtle"] = new SolidColorBrush(color, dark ? DarkSubtle : LightSubtle);
        app.Resources["Accent.Foreground"] = new SolidColorBrush(foreground);
        // Fluent controls (check boxes, sliders, focus) take the same accent.
        app.Resources["SystemAccentColor"] = color;
    }
}
