using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace PdfNotes.UI.Views;

/// <summary>The app's windows other than the main one: the theme's colors and the UI font (the WPF Theme.Attach + UseDialogTokens).</summary>
public static class DialogLook
{
    public static void Apply(Window window)
    {
        window.Bind(TemplatedControl.BackgroundProperty, window.GetResourceObservable("Window.Background"));
        window.Bind(TemplatedControl.ForegroundProperty, window.GetResourceObservable("Text.Primary"));
        window.Bind(TemplatedControl.FontFamilyProperty, window.GetResourceObservable("Font.Ui"));
        window.Bind(TemplatedControl.FontSizeProperty, window.GetResourceObservable("FontSize.Body"));
        window.ShowInTaskbar = false;
    }

    /// <summary>The WPF ScreenArea.Fit: a preferred size clamped to the work area of the owner's screen, resizable, with a minimum.</summary>
    public static void Fit(Window window, double width, double height, double minWidth, double minHeight)
    {
        window.SizeToContent = SizeToContent.Manual;
        window.CanResize = true;
        window.Width = width;
        window.Height = height;
        window.MinWidth = minWidth;
        window.MinHeight = minHeight;
        window.Opened += (_, _) =>
        {
            if ((window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is not { } screen) return;
            double scale = screen.Scaling > 0 ? screen.Scaling : 1;
            double areaWidth = screen.WorkingArea.Width / scale, areaHeight = screen.WorkingArea.Height / scale;
            if (window.Width > areaWidth) window.Width = Math.Max(minWidth, areaWidth);
            if (window.Height > areaHeight) window.Height = Math.Max(minHeight, areaHeight);
            // Centered at the preferred size, the shrunk window may start above or left of the work area: its title bar stays on it.
            var area = screen.WorkingArea;
            int x = Math.Clamp(window.Position.X, area.X, Math.Max(area.X, area.Right - (int)(window.Width * scale)));
            int y = Math.Clamp(window.Position.Y, area.Y, Math.Max(area.Y, area.Bottom - (int)(window.Height * scale)));
            if (x != window.Position.X || y != window.Position.Y) window.Position = new PixelPoint(x, y);
        };
    }
}
