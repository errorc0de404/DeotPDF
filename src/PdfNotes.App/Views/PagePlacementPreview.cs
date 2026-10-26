using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.App.Themes;

namespace PdfNotes.App.Views;

/// <summary>Transient displayed-page placement. Position is a fraction of the target paper; content keeps its aspect ratio.</summary>
public sealed class PagePlacementPreview : FrameworkElement
{
    private readonly double sourceWidth, sourceHeight;
    private readonly ImageSource? image;
    private readonly bool resize;
    private double paperWidth, paperHeight, scale = .8, left, top, factor;
    private Point origin, start;
    private double startLeft, startTop, startScale;
    private int drag = -1;
    private bool free;
    private Rect contentRect;
    public event EventHandler? PlacementChanged;
    public double ScalePercent => scale * 100;
    public Rect ContentBounds => contentRect;
    public Point2 FreePosition => new(left / paperWidth, top / paperHeight);

    public PagePlacementPreview(PageSetup source, ImageSource? image, bool resize)
    {
        sourceWidth = source.Frame.DisplayWidthPt; sourceHeight = source.Frame.DisplayHeightPt;
        paperWidth = sourceWidth; paperHeight = sourceHeight; this.image = image; this.resize = resize;
        Focusable = true; ClipToBounds = true;
    }
    public void BeginFree() => free = true;
    public void Update(double? width, double? height, double percent, bool fit, int alignment, bool isFree)
    {
        // Retain normalized placement when the target paper dimensions change.
        var position = FreePosition;
        paperWidth = width ?? sourceWidth; paperHeight = height ?? sourceHeight;
        scale = resize ? fit ? Math.Min(1, Math.Min(paperWidth / sourceWidth, paperHeight / sourceHeight)) : 1 : percent;
        if (isFree) { left = position.X * paperWidth; top = position.Y * paperHeight; }
        else { left = alignment % 3 / 2.0 * (paperWidth - sourceWidth * scale); top = alignment / 3 / 2.0 * (paperHeight - sourceHeight * scale); }
        free = isFree; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Theme.Brush("Canvas.Background"), null, new Rect(RenderSize));
        factor = Math.Max(.0001, Math.Min(Math.Max(1, ActualWidth - 100) / Math.Max(paperWidth, sourceWidth), Math.Max(1, ActualHeight - 100) / Math.Max(paperHeight, sourceHeight)));
        origin = new((ActualWidth - paperWidth * factor) / 2, (ActualHeight - paperHeight * factor) / 2);
        var paper = new Rect(origin.X, origin.Y, paperWidth * factor, paperHeight * factor);
        contentRect = new(origin.X + left * factor, origin.Y + top * factor, sourceWidth * scale * factor, sourceHeight * scale * factor);
        dc.DrawRectangle(Theme.Brush("Paper"), new Pen(Theme.Brush("Paper.Edge"), 1), paper);
        dc.PushClip(new RectangleGeometry(paper));
        if (image != null) dc.DrawImage(image, contentRect);
        dc.Pop();
        dc.DrawRectangle(null, new Pen(Theme.Brush(free ? "Accent" : "Overlay.Neutral"), 1.5), contentRect);
        if (free && !resize)
            foreach (var point in Corners(contentRect)) dc.DrawRectangle(Theme.Brush("Overlay.Halo"), new Pen(Theme.Brush("Accent"), 1.5), new Rect(point.X - 5, point.Y - 5, 10, 10));
    }
    private static Point[] Corners(Rect r) => [r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft];
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!free) return;
        var p = e.GetPosition(this); var corners = Corners(contentRect);
        drag = !resize ? Array.FindIndex(corners, c => (c - p).Length <= 12) : -1;
        if (drag < 0 && !contentRect.Contains(p)) return;
        if (drag < 0) drag = 4;
        start = p; startLeft = left; startTop = top; startScale = scale;
        Focus(); CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (!IsMouseCaptured) { Cursor = free ? !resize && Corners(contentRect).Any(c => (c - p).Length <= 12) ? Cursors.SizeNWSE : contentRect.Contains(p) ? Cursors.SizeAll : Cursors.Arrow : Cursors.Arrow; return; }
        var delta = (p - start) / factor;
        if (drag == 4) { left = startLeft + delta.X; top = startTop + delta.Y; }
        else
        {
            double sx = drag is 0 or 3 ? -1 : 1, sy = drag is 0 or 1 ? -1 : 1;
            scale = Math.Clamp(startScale + (delta.X * sx * sourceWidth + delta.Y * sy * sourceHeight) / (sourceWidth * sourceWidth + sourceHeight * sourceHeight), .001, 1);
            left = startLeft + (sx < 0 ? sourceWidth * (startScale - scale) : 0);
            top = startTop + (sy < 0 ? sourceHeight * (startScale - scale) : 0);
        }
        InvalidateVisual(); PlacementChanged?.Invoke(this, EventArgs.Empty); e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return; ReleaseMouseCapture(); drag = -1; e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!free || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        left += e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
        top += e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
        InvalidateVisual(); PlacementChanged?.Invoke(this, EventArgs.Empty); e.Handled = true;
    }
}
