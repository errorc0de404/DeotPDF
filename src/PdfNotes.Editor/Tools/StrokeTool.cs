using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;

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
        view.RefreshPreview();
    }

    public void PointerUp(CanvasPointer pointer)
    {
        if (!IsActive) return;
        var transform = view.PageTransform(pageIndex);
        var p = transform.DipToPage.Transform(new(pointer.Position.X, pointer.Position.Y));
        if (p.DistanceTo(raw[^1]) >= transform.DevicePixelLengthToPoints(MinStepPixels))
        {
            raw.Add(p);
            AddPressure(pointer);
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

    private StrokeAnnotation Create(ImmutableArray<Point2> points, ImmutableArray<double> pressure = default) => Kind == AnnotationKind.Ink
        ? new InkStroke(Guid.NewGuid(), false, Affine.Identity, points, current.WidthPt, current.Color, current.Opacity)
        {
            // Stored with four decimals (schema v8), so the saved file reads back as the same stroke.
            Pressures = pressure.IsDefaultOrEmpty ? default : [.. pressure.Select(p => Math.Round(p, 4))]
        }
        : new FreeHighlightStroke(Guid.NewGuid(), false, Affine.Identity, points, current.WidthPt, current.Color, current.Opacity);

    IEnumerable<CanvasMark> ICanvasPreview.Marks(int index, Page page, ViewTransform transform)
    {
        if (index != pageIndex || raw.Count == 0) yield break;
        if (pressured)
        {
            var (points, smoothed) = StrokeGeometry.Smooth(raw, pressures);
            yield return new AnnotationMark(Create(points, smoothed));
        }
        else yield return new AnnotationMark(Create(StrokeGeometry.Smooth(raw)));
    }
}
