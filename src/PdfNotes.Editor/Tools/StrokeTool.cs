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
/// </summary>
public sealed class StrokeTool(ICanvasView view, Func<DocumentSession?> session, Func<StrokeStyle> style, AnnotationKind kind,
    Func<bool>? usePressure = null) : ICanvasTool, ICanvasPreview
{
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
        return true;
    }

    public void PointerMove(CanvasPointer pointer)
    {
        if (!IsActive) return;
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
        var transform = view.PageTransform(pageIndex);
        var p = transform.DipToPage.Transform(new(pointer.Position.X, pointer.Position.Y));
        if (p.DistanceTo(raw[^1]) >= transform.DevicePixelLengthToPoints(MinStepPixels))
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
        if (s.Execute(DocumentEdits.AddAnnotation(s.Current, targetPage, stroke, label: Kind == AnnotationKind.Ink ? "undo.pen" : "undo.highlighter")))
            Committed?.Invoke(this, stroke);
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
        yield return new StrokeProgressMark(previewKey, settled, Piece(settledEnd, raw.Count - 1), current.Opacity);
    }
}
