using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using PdfNotes.Core.Geometry;

namespace PdfNotes.UI.Canvas;

/// <summary>
/// A finger or pen on the scroll bars (2026-11-12 user report: the page shook). The bar is thin and a long document makes
/// one of its pixels many DIPs of pages, so the small wobble of a finger moved the page by large steps, and a touch just
/// beside the thumb hit the bar's repeating page buttons. Here a touch anywhere on a bar drags from where it landed: the
/// finger's position is smoothed, a wobble under <see cref="TouchBarDeadbandDip"/> is ignored, and the page follows at
/// <see cref="TouchBarSpeed"/> of the thumb's rate. The mouse keeps the bar's own behavior.
/// </summary>
public sealed partial class DocumentCanvas
{
    /// <summary>The page moves this fraction of what dragging the thumb by the same distance would move it.</summary>
    public const double TouchBarSpeed = 0.5;

    /// <summary>Movements of the finger smaller than this (DIPs) since the last step are ignored.</summary>
    public const double TouchBarDeadbandDip = 1.5;

    /// <summary>How much of each new finger position the smoothed position takes (exponential smoothing).</summary>
    private const double TouchBarSmoothing = 0.35;

    private sealed record BarDrag(ScrollBar Bar, IPointer Pointer, double Start, double StartOffset)
    {
        public double Smoothed { get; set; } = Start;
    }

    private BarDrag? barDrag;

    private void InitializeTouchBars()
    {
        foreach (var bar in new[] { vertical, horizontal })
        {
            bar.AddHandler(PointerPressedEvent, (_, e) => TouchBarPressed(bar, e), RoutingStrategies.Tunnel);
            bar.AddHandler(PointerMovedEvent, (_, e) => TouchBarMoved(bar, e), RoutingStrategies.Tunnel);
            bar.AddHandler(PointerReleasedEvent, (_, e) => TouchBarReleased(bar, e), RoutingStrategies.Tunnel);
            bar.AddHandler(PointerCaptureLostEvent, (_, e) => { if (ReferenceEquals(barDrag?.Pointer, e.Pointer)) barDrag = null; });
        }
    }

    private static double Along(ScrollBar bar, PointerEventArgs e)
    {
        var p = e.GetPosition(bar);
        return bar.Orientation == Avalonia.Layout.Orientation.Vertical ? p.Y : p.X;
    }

    private void TouchBarPressed(ScrollBar bar, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse) return;
        e.Handled = true; // not the thumb nor the repeating page buttons
        StopFling();
        bool isVertical = ReferenceEquals(bar, vertical);
        barDrag = new(bar, e.Pointer, Along(bar, e), isVertical ? offset.Y : offset.X);
        e.Pointer.Capture(bar);
    }

    private void TouchBarMoved(ScrollBar bar, PointerEventArgs e)
    {
        if (barDrag is not { } drag || !ReferenceEquals(drag.Bar, bar) || !ReferenceEquals(drag.Pointer, e.Pointer)) return;
        e.Handled = true;
        double at = Along(bar, e);
        if (Math.Abs(at - drag.Smoothed) < TouchBarDeadbandDip) return;
        drag.Smoothed += TouchBarSmoothing * (at - drag.Smoothed);
        SetOffsetAlong(bar, drag.StartOffset + (drag.Smoothed - drag.Start) * TouchBarRate(bar));
    }

    private void TouchBarReleased(ScrollBar bar, PointerReleasedEventArgs e)
    {
        if (barDrag is not { } drag || !ReferenceEquals(drag.Bar, bar) || !ReferenceEquals(drag.Pointer, e.Pointer)) return;
        e.Handled = true;
        barDrag = null;
        e.Pointer.Capture(null);
    }

    /// <summary>DIPs of pages per DIP of finger: the thumb's own rate (scroll range over the track's free length) times <see cref="TouchBarSpeed"/>.</summary>
    internal double TouchBarRate(ScrollBar bar)
    {
        bool isVertical = ReferenceEquals(bar, vertical);
        double length = isVertical ? bar.Bounds.Height : bar.Bounds.Width;
        double viewport = isVertical ? viewportHeight : viewportWidth;
        double range = Math.Max(0, (isVertical ? ExtentHeight : ExtentWidth) - viewport);
        double thumb = range + viewport > 0 ? length * viewport / (range + viewport) : length;
        return range / Math.Max(1, length - thumb) * TouchBarSpeed;
    }

    private void SetOffsetAlong(ScrollBar bar, double value) =>
        SetOffset(ReferenceEquals(bar, vertical) ? new Point2(offset.X, value) : new Point2(value, offset.Y));

    /// <summary>The vertical bar (tests).</summary>
    internal ScrollBar VerticalBar => vertical;
}
