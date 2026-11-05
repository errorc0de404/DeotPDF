using Avalonia;
using Avalonia.Controls;
using PdfNotes.Core.Geometry;
using PdfNotes.Editor.Services;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// The last position in each document and the window placement (spec 24.1, D13h): app settings, never written into a
/// document, never Undo or the unsaved mark. The recent documents menu is in MainWindow.Ribbon.cs.
/// </summary>
public sealed partial class MainWindow
{
    public IReadOnlyList<string> RecentFiles => viewModel.Recent.Files;

    /// <summary>Remembers where the document was shown (page, point at the view center, zoom); new notes are not remembered.</summary>
    private void RememberPosition(OpenDocument? doc)
    {
        if (doc?.FilePath is not { } path || !ReferenceEquals(doc, Document) || Canvas.CenterAnchor is not { } anchor) return;
        viewModel.Recent.RememberPosition(path, anchor.PageIndex, anchor.PagePoint, Canvas.Zoom);
    }

    /// <summary>Shows a reopened document where it was left; fewer pages than before go to the last page.</summary>
    private void RestorePosition(string path)
    {
        var saved = viewModel.Recent.PositionOf(path);
        if (saved is null || Canvas.PageCount == 0) return;
        int page = Math.Clamp(saved.Page, 0, Canvas.PageCount - 1);
        Canvas.SetZoom(saved.Zoom);
        if (page == saved.Page) Canvas.ScrollToPagePoint(page, new Point2(saved.X, saved.Y));
        else Canvas.GoToPage(page);
    }

    // Placement is stored like the WPF app (the same settings file): DIPs of the screen the window is on, and the outer
    // window size (frame and title bar included; Avalonia's Width/Height are the client area).

    private double PlacementScale
    {
        get
        {
            try { return (Screens.ScreenFromWindow(this) ?? Screens.Primary)?.Scaling is > 0 and var s ? s : 1; }
            catch (ObjectDisposedException) { return 1; } // a closed window has no screen
        }
    }
    private Rect? normalBounds; // the last outer size and place while neither maximized nor minimized

    /// <summary>The frame and title bar around the client area (DIPs); zero until the window is shown.</summary>
    private Size FrameExtra => FrameSize is { } frame && frame.Width >= ClientSize.Width ? new(frame.Width - ClientSize.Width, frame.Height - ClientSize.Height) : default;

    private Rect? CurrentNormalBounds()
    {
        if (WindowState != WindowState.Normal || !IsVisible || Position.X <= -30000 || Position.Y <= -30000) return null; // minimized windows sit at -32000
        var extra = FrameExtra;
        return new Rect(Position.X / PlacementScale, Position.Y / PlacementScale, Bounds.Width + extra.Width, Bounds.Height + extra.Height);
    }

    /// <summary>Restores size, position and maximized state; a place off every screen opens centered, a size larger than the work area shrinks.</summary>
    private void RestoreWindowPlacement()
    {
        // Maximizing and minimizing move and resize the window before the state reads so: look once things settled.
        void Capture() => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (CurrentNormalBounds() is { } b) normalBounds = b; }, Avalonia.Threading.DispatcherPriority.Background);
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty || e.Property == BoundsProperty) Capture(); };
        PositionChanged += (_, _) => Capture();
        Opened += (_, _) => FitRestoredSize();
        if (settings.Window is not { } w || !double.IsFinite(w.Width) || !double.IsFinite(w.Height) || w.Width < MinWidth || w.Height < MinHeight) return;
        Width = w.Width;
        Height = w.Height;
        restoringOuterSize = true;
        if (double.IsFinite(w.Left) && double.IsFinite(w.Top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            // The screen the place was on (its own scale), else the main one.
            var screen = Screens.All.FirstOrDefault(s => s.Bounds.Contains(new PixelPoint((int)Math.Round(w.Left * s.Scaling), (int)Math.Round(w.Top * s.Scaling)))) ?? Screens.Primary;
            double scale = screen?.Scaling is > 0 and var s ? s : 1;
            Position = new PixelPoint((int)Math.Round(w.Left * scale), (int)Math.Round(w.Top * scale));
            Opened += (_, _) => KeepOnScreen();
        }
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    private bool restoringOuterSize; // the saved size includes the frame: the client size is made smaller once it is known

    /// <summary>The title bar must stay reachable: a remembered place off every screen (a monitor gone) moves onto the main one.</summary>
    private void KeepOnScreen()
    {
        if (Screens.All.Count == 0) return;
        var title = new PixelRect(Position, new PixelSize((int)Math.Max(100, Width * PlacementScale), (int)(32 * PlacementScale)));
        if (Screens.All.Any(s => s.WorkingArea.Intersect(title).Width >= 100)) return;
        var area = (Screens.Primary ?? Screens.All[0]).WorkingArea;
        Position = new PixelPoint(area.X + Math.Max(0, (area.Width - (int)(Width * PlacementScale)) / 2), area.Y + Math.Max(0, (area.Height - (int)(Height * PlacementScale)) / 2));
    }

    /// <summary>A size remembered on a bigger (or less scaled) monitor shrinks to the work area of the one it opens on (D13e).</summary>
    private void FitRestoredSize()
    {
        var extra = FrameExtra;
        if (restoringOuterSize)
        {
            restoringOuterSize = false;
            Width = Math.Max(MinWidth, Width - extra.Width);
            Height = Math.Max(MinHeight, Height - extra.Height);
        }
        if (WindowState != WindowState.Normal || (Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen) return;
        double scale = screen.Scaling > 0 ? screen.Scaling : 1;
        double areaWidth = screen.WorkingArea.Width / scale - extra.Width, areaHeight = screen.WorkingArea.Height / scale - extra.Height;
        if (Width > areaWidth) Width = Math.Max(MinWidth, areaWidth);
        if (Height > areaHeight) Height = Math.Max(MinHeight, areaHeight);
    }

    private void RememberWindowPlacement()
    {
        bool maximized = WindowState == WindowState.Maximized;
        var b = CurrentNormalBounds() ?? normalBounds;
        if (b is not { } bounds || !double.IsFinite(bounds.Width) || bounds.Width <= 0) return;
        settings.Window = new() { Left = bounds.X, Top = bounds.Y, Width = bounds.Width, Height = bounds.Height, Maximized = maximized };
    }
}
