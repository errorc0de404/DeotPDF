using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;
using PdfNotes.Core.Marks;

namespace PdfNotes.Editor.Tools;

/// <summary>
/// Pen and free highlighter (spec 7.1–7.2, 9.1–9.2). One mouse press to release is one stroke and one Undo unit.
/// Points are collected in canonical page points of the page where the press started (drawing past the page edge
/// keeps the points; the page clips the display). The preview uses the same smoothing as the committed stroke and
/// never touches the document; Esc, right button or lost capture discards it. A pen that reports pressure draws a pen
/// stroke whose width follows it (spec 7.6) while <paramref name="usePressure"/> says so; the highlighter never does.
/// <para>
/// Held still at the end (spec 7.7, 1.4 plan 1.2): the pen, mouse or finger staying within <see cref="HoldRadiusDip"/>
/// for <see cref="HoldTime"/> before it lifts turns the stroke into the shape <see cref="ShapeRecognizer"/> finds (the
/// highlighter: a line only), shown at once; moving on stretches and turns it, and lifting commits the hand stroke and
/// then the shape in its place as a second Undo unit. <paramref name="recognize"/> is the setting (null or no
/// <paramref name="time"/>: never); <paramref name="post"/> runs the timer's callback on the UI thread (default inline).
/// </para>
/// </summary>
public sealed class StrokeTool(ICanvasView view, Func<DocumentSession?> session, Func<StrokeStyle> style, AnnotationKind kind,
    Func<bool>? usePressure = null, TimeProvider? time = null, Func<bool>? recognize = null, Action<Action>? post = null) : ICanvasTool, ICanvasPreview
{
    /// <summary>How long the pen stays within <see cref="HoldRadiusDip"/> before the stroke becomes a shape (1.4 plan 1.2).</summary>
    public static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(500);

    /// <summary>The hold allows this much movement (screen DIP); farther starts it again from there.</summary>
    public const double HoldRadiusDip = 3;

    /// <summary>A stroke whose bounds are smaller than this on screen (DIP) is never a shape (handwriting).</summary>
    public const double MinShapeDip = 24;

    /// <summary>Recognition tolerance: this share of the longer side of the stroke's bounds (page points).</summary>
    public const double ShapeToleranceFraction = 0.08;

    /// <summary>A timer may fire this much early.</summary>
    private static readonly TimeSpan HoldSlack = TimeSpan.FromMilliseconds(30);

    /// <summary>Input points closer than this (device pixels) to the previous one are skipped.</summary>
    public const double MinStepPixels = 0.75;

    /// <summary>Simplification tolerance: a fraction of a device pixel at the drawing zoom, capped in points.</summary>
    public const double SimplifyPixels = 0.25, MaxSimplifyPt = 0.05;

    private readonly List<Point2> raw = [];
    private readonly List<double> pressures = []; // one per raw point while the stroke has pressure

    /// <summary>Settled points go into a preview piece this many at a time (the preview then costs the same at any length).</summary>
    public const int PreviewPiecePoints = 24;

    private ImmutableArray<StrokeAnnotation> settled = [];
    private int settledEnd; // the raw index the last settled piece ends at (the next one starts there)
    private Guid previewKey;
    private bool pressured;
    private int pageIndex = -1;
    private Guid pageId;
    private StrokeStyle current = StrokeStyle.DefaultPen;
    private ITimer? holdTimer;
    private long holdStart;
    private Point2 holdAnchorDip;
    private RecognizedShape? recognized; // the shape found at the hold, before any drag
    private Point2 grab;                 // where the stroke was held (page points)
    private Annotation? shape;           // what the stroke becomes (preview id Guid.Empty)

    public AnnotationKind Kind { get; } = kind is AnnotationKind.Ink or AnnotationKind.FreeHighlight
        ? kind : throw new ArgumentOutOfRangeException(nameof(kind));

    public ToolCursor? Cursor => ToolCursor.Pen;
    public bool IsActive => pageIndex >= 0;

    /// <summary>Raised after a stroke was added to the document.</summary>
    public event EventHandler<StrokeAnnotation>? Committed;

    public bool PointerDown(CanvasPointer pointer)
    {
        if (session() is not { } s || view.HitTestPage(pointer.Position) is not { } anchor) return false; // strokes start on a page
        pageIndex = anchor.PageIndex;
        pageId = s.Current.Pages[pageIndex].Id;
        current = style().ClampFor(Kind);
        raw.Clear();
        pressures.Clear();
        settled = [];
        settledEnd = 0;
        previewKey = Guid.NewGuid();
        raw.Add(anchor.PagePoint);
        pressured = Kind == AnnotationKind.Ink && pointer is { Device: PointerDevice.Pen, Pressure: { } pressure } && double.IsFinite(pressure) &&
                    usePressure?.Invoke() == true;
        if (pressured) pressures.Add(Math.Clamp(pointer.Pressure!.Value, 0, 1));
        view.SetPreview(pageIndex, this);
        StartHold(pointer.Position);
        return true;
    }

    public void PointerMove(CanvasPointer pointer)
    {
        if (!IsActive) return;
        if (shape is not null) { Drag(pointer.Position); return; }
        if (pointer.Position.DistanceTo(holdAnchorDip) > HoldRadiusDip) StartHold(pointer.Position);
        var transform = view.PageTransform(pageIndex);
        var p = transform.DipToPage.Transform(new(pointer.Position.X, pointer.Position.Y));
        if (p.DistanceTo(raw[^1]) < transform.DevicePixelLengthToPoints(MinStepPixels)) return;
        raw.Add(p);
        AddPressure(pointer);
        // Smoothing looks one point to each side, so every point but the last is final: full pieces of them settle.
        int final = raw.Count - 2;
        if (final - settledEnd >= PreviewPiecePoints)
        {
            settled = settled.Add(Piece(settledEnd, final));
            settledEnd = final;
        }
        view.RefreshPreview();
    }

    /// <summary>The preview piece of raw points <paramref name="from"/>..<paramref name="to"/>, smoothed as the whole stroke will be, opaque.</summary>
    private StrokeAnnotation Piece(int from, int to)
    {
        int lo = Math.Max(0, from - 1), hi = Math.Min(raw.Count - 1, to + 1);
        var window = raw.GetRange(lo, hi - lo + 1);
        if (pressured)
        {
            var (points, smoothed) = StrokeGeometry.Smooth(window, pressures.GetRange(lo, hi - lo + 1));
            return Create(points[(from - lo)..(to - lo + 1)], smoothed[(from - lo)..(to - lo + 1)], opaque: true);
        }
        return Create(StrokeGeometry.Smooth(window)[(from - lo)..(to - lo + 1)], opaque: true);
    }

    public void PointerUp(CanvasPointer pointer)
    {
        if (!IsActive) return;
        if (shape is not null) Drag(pointer.Position);
        var made = shape is null ? null : shape with { Id = Guid.NewGuid() };
        var transform = view.PageTransform(pageIndex);
        var p = transform.DipToPage.Transform(new(pointer.Position.X, pointer.Position.Y));
        // After recognition the pen moved the shape, not the stroke: the hand stroke ends where it was held.
        if (made is null && p.DistanceTo(raw[^1]) >= transform.DevicePixelLengthToPoints(MinStepPixels))
        {
            raw.Add(p);
            AddPressure(pointer with { Pressure = null }); // a pen reports no pressure as it lifts: the end keeps the last one
        }
        double tolerance = Math.Min(transform.DevicePixelLengthToPoints(SimplifyPixels), MaxSimplifyPt);
        StrokeAnnotation stroke;
        if (pressured)
        {
            var (points, finished) = StrokeGeometry.Finish(raw, pressures, tolerance);
            stroke = Create(points, finished);
        }
        else stroke = Create(StrokeGeometry.Finish(raw, tolerance));
        Guid targetPage = pageId;
        Reset();
        if (session() is not { } s || s.Current.IndexOfPage(targetPage) < 0) return;
        if (!s.Execute(DocumentEdits.AddAnnotation(s.Current, targetPage, stroke, label: Kind == AnnotationKind.Ink ? "undo.pen" : "undo.highlighter"))) return;
        Committed?.Invoke(this, stroke);
        // The shape replaces the hand stroke as its own Undo unit: the first Undo gives the stroke back (1.4 plan 1.2).
        if (made is not null && ModelValidator.ValidateAnnotation(made, _ => false) is null)
            s.Execute(DocumentEdits.ReplaceAnnotation(s.Current, stroke.Id, made, "undo.recognizeShape"));
    }

    /// <summary>(Re)starts the hold at <paramref name="dip"/>: the stroke becomes a shape when the pen stays near it.</summary>
    private void StartHold(Point2 dip)
    {
        holdAnchorDip = dip;
        if (time is null || recognize?.Invoke() != true) return;
        holdStart = time.GetTimestamp();
        holdTimer ??= time.CreateTimer(_ => (post ?? (action => action()))(HoldElapsed), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        holdTimer.Change(HoldTime, Timeout.InfiniteTimeSpan);
    }

    private void StopHold() => holdTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    /// <summary>The hold timer (on the UI thread): the stroke so far becomes a shape if it is one; otherwise nothing happens.</summary>
    private void HoldElapsed()
    {
        if (!IsActive || shape is not null || time is null || recognize?.Invoke() != true) return;
        if (time.GetElapsedTime(holdStart) < HoldTime - HoldSlack) return; // a callback from before the hold restarted
        double minX = raw.Min(p => p.X), maxX = raw.Max(p => p.X), minY = raw.Min(p => p.Y), maxY = raw.Max(p => p.Y);
        double tolerance = Math.Max(maxX - minX, maxY - minY) * ShapeToleranceFraction;
        if (!(tolerance > 0)) return;
        var found = ShapeRecognizer.Recognize(raw, view.PageTransform(pageIndex).DipLengthToPoints(MinShapeDip), tolerance);
        if (found is null || Kind == AnnotationKind.FreeHighlight && found.Kind != RecognizedKind.Line) return;
        recognized = found;
        grab = raw[^1];
        shape = ToAnnotation(found);
        view.RefreshPreview();
    }

    /// <summary>After the hold the shape follows the pen: grabbed where it was held, stretched and turned (or the line's end moved).</summary>
    private void Drag(Point2 dip)
    {
        var to = view.PageTransform(pageIndex).DipToPage.Transform(dip);
        shape = ToAnnotation(ShapeRecognizer.Dragged(recognized!, grab, to));
        view.RefreshPreview();
    }

    /// <summary>
    /// The annotation of a recognised shape, as the line and shape tools make it: the pen's color, width and opacity as a
    /// solid line or border, no fill, no pressure. The highlighter's line stays a two-point free highlight.
    /// </summary>
    private Annotation ToAnnotation(RecognizedShape found)
    {
        var p = found.Points;
        if (Kind == AnnotationKind.FreeHighlight)
            return new FreeHighlightStroke(Guid.Empty, false, Affine.Identity, [p[0], p[1]], current.WidthPt, current.Color, current.Opacity);
        var border = new ShapeStyle(current.Color, 0, true, current.Color, current.WidthPt, current.Opacity, DashStyle.Solid);
        switch (found.Kind)
        {
            case RecognizedKind.Line or RecognizedKind.Arrow:
                return new LineAnnotation(Guid.Empty, false, Affine.Identity, LineKind.Straight, p[0], p[1], [], current.WidthPt, DashStyle.Solid,
                    false, found.Kind == RecognizedKind.Arrow, current.Color, current.Opacity);
            case RecognizedKind.Triangle:
                return new ShapeAnnotation(Guid.Empty, false, Affine.Identity, ShapeKind.Polygon, default, [.. p], border);
            default:
                double halfWidth = Math.Abs(p[1].X), halfHeight = Math.Abs(p[1].Y);
                double turn = Math.IEEERemainder(found.RotationRad, 2 * Math.PI);
                return new ShapeAnnotation(Guid.Empty, false, Affine.Rigid(turn, p[0].X, p[0].Y),
                    found.Kind == RecognizedKind.Ellipse ? ShapeKind.Ellipse : ShapeKind.Rectangle,
                    new Rect2(-halfWidth, -halfHeight, 2 * halfWidth, 2 * halfHeight), [], border);
        }
    }

    public void Cancel() => Reset();

    private void Reset()
    {
        pageIndex = -1;
        raw.Clear();
        pressures.Clear();
        settled = [];
        settledEnd = 0;
        pressured = false;
        StopHold();
        recognized = null;
        shape = null;
        view.SetPreview(-1, null);
    }

    /// <summary>
    /// The pressure of a new point. An event without one (some drivers skip it on the last report) repeats the previous
    /// value, so the stroke keeps one pressure per point.
    /// </summary>
    private void AddPressure(CanvasPointer pointer)
    {
        if (!pressured) return;
        pressures.Add(pointer.Pressure is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : pressures[^1]);
    }

    private StrokeAnnotation Create(ImmutableArray<Point2> points, ImmutableArray<double> pressure = default, bool opaque = false)
    {
        double opacity = opaque ? 1 : current.Opacity;
        return Kind == AnnotationKind.Ink
            ? new InkStroke(Guid.NewGuid(), false, Affine.Identity, points, current.WidthPt, current.Color, opacity)
            {
                // Stored with four decimals (schema v8), so the saved file reads back as the same stroke.
                Pressures = pressure.IsDefaultOrEmpty ? default : [.. pressure.Select(p => Math.Round(p, 4))]
            }
            : new FreeHighlightStroke(Guid.NewGuid(), false, Affine.Identity, points, current.WidthPt, current.Color, opacity);
    }

    /// <summary>
    /// The settled pieces as they are and the end from the last of them (roadmap P5d): drawing a long stroke costs the
    /// same per frame as a short one. Together they look like the stroke smoothed whole, which is what it commits as.
    /// </summary>
    IEnumerable<CanvasMark> ICanvasPreview.Marks(int index, Page page, ViewTransform transform)
    {
        if (index != pageIndex || raw.Count == 0) yield break;
        if (shape is not null)
        {
            yield return new AnnotationMark(shape); // drawn as the shape tool draws its preview
            yield break;
        }
        yield return new StrokeProgressMark(previewKey, settled, Piece(settledEnd, raw.Count - 1), current.Opacity);
    }
}
