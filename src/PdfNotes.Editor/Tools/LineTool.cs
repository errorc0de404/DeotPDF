using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;

namespace PdfNotes.Editor.Tools;

/// <summary>
/// Straight line and curve tools (spec 8). Press = start point A, release = end point B. A curve fits the dragged path
/// with fixed ends: quadratic when its largest error is within 3 screen pixels, else cubic (or the forced count).
/// Ends closer than 4 screen pixels cancel. The new line is selected so its handles and the top bar act on it at once;
/// pressing one of its handles edits it through the selection tool instead of starting a new line. A click (no drag) on an
/// existing unlocked line or shape hands it to <paramref name="select"/> for editing with the selection tool, like the
/// shape tool (spec 5.4, 2026-10-13 user request); a drag that starts on it still draws.
/// Thresholds are judged on screen and the result is stored in page points, so zoom never changes an existing line.
/// </summary>
public sealed class LineTool(ICanvasView view, Func<DocumentSession?> session, SelectTool selection,
    Func<StrokeStyle> style, Func<LineOptions> options, bool curve, Action<Guid> select) : ICanvasTool, ICanvasPreview
{
    public const double CancelDistanceDip = 4, FitToleranceDip = 3;

    private readonly List<Point2> raw = [];
    private int pageIndex = -1;
    private Guid pageId;
    private Point2 end;
    private bool editing;
    private Point2 startDip;
    private double reachDip;
    private Guid? pressed; // existing line or shape under the press
    private StrokeStyle currentStyle = StrokeStyle.DefaultPen;
    private LineOptions currentOptions = LineOptions.Default;

    public bool IsCurve { get; } = curve;
    public ToolCursor? Cursor => ToolCursor.Cross;
    public bool IsActive => pageIndex >= 0 || editing;

    public bool PointerDown(CanvasPointer pointer)
    {
        if (selection.LineHandleAt(pointer.Position) >= 0)
        {
            editing = selection.PointerDown(pointer);
            return editing;
        }
        if (session() is not { } s || view.HitTestPage(pointer.Position) is not { } anchor) return false;
        pressed = selection.Pick(pointer.Position) is { } hit && s.Current.Pages[anchor.PageIndex].Annotations.FirstOrDefault(a => a.Id == hit) is { } under &&
            ShapeTool.IsShapeGroup(under) ? hit : null;
        startDip = pointer.Position; reachDip = 0;
        selection.Select(null);
        pageIndex = anchor.PageIndex;
        pageId = s.Current.Pages[pageIndex].Id;
        currentStyle = style().ClampFor(AnnotationKind.Line);
        currentOptions = options().Validated();
        raw.Clear();
        raw.Add(anchor.PagePoint);
        end = anchor.PagePoint;
        view.SetPreview(pageIndex, this);
        return true;
    }

    public void PointerMove(CanvasPointer pointer)
    {
        if (editing) { selection.PointerMove(pointer); return; }
        if (pageIndex < 0) return;
        reachDip = Math.Max(reachDip, pointer.Position.DistanceTo(startDip));
        var transform = view.PageTransform(pageIndex);
        end = transform.DipToPage.Transform(new(pointer.Position.X, pointer.Position.Y));
        if (!IsCurve && pointer.Modifiers.HasFlag(InputModifiers.Shift)) end = SelectTool.SnapAngle(raw[0], end);
        if (IsCurve && end.DistanceTo(raw[^1]) >= transform.DevicePixelLengthToPoints(StrokeTool.MinStepPixels)) raw.Add(end);
        view.RefreshPreview();
    }

    public void PointerUp(CanvasPointer pointer)
    {
        if (editing) { editing = false; selection.PointerUp(pointer); return; }
        if (pageIndex < 0) return;
        PointerMove(pointer);
        if (reachDip < ShapeTool.MinDragDip && pressed is { } clicked)
        {
            Reset();
            select(clicked); // edit the line or shape that was clicked
            return;
        }
        var transform = view.PageTransform(pageIndex);
        var line = Build(transform);
        Guid target = pageId;
        Reset();
        if (line is null || session() is not { } s || s.Current.IndexOfPage(target) < 0) return;
        if (s.Execute(DocumentEdits.AddAnnotation(s.Current, target, line, label: IsCurve ? "undo.curve" : "undo.line"))) selection.Select(line.Id);
    }

    public void Cancel()
    {
        if (editing) { editing = false; selection.Cancel(); }
        Reset();
    }

    /// <summary>The line the current input makes, or null when it is too short on screen (spec 8.2).</summary>
    private LineAnnotation? Build(ViewTransform transform)
    {
        var a = raw[0];
        if (transform.PageToDip.TransformVector(new(end.X - a.X, end.Y - a.Y)).DistanceTo(default) < CancelDistanceDip) return null;
        var line = new LineAnnotation(Guid.NewGuid(), false, Affine.Identity, LineKind.Straight, a, end, [], currentStyle.WidthPt,
            currentOptions.Dash, currentOptions.StartArrow, currentOptions.EndArrow, currentStyle.Color, currentStyle.Opacity);
        if (!IsCurve) return line;
        var path = raw[^1] == end ? raw : [.. raw, end];
        var fit = LineGeometry.FitCurve(path, transform.DipLengthToPoints(FitToleranceDip),
            currentOptions.CurveControls == 0 ? null : currentOptions.CurveControls);
        return line with { LineKind = fit.Kind, Controls = fit.Controls };
    }

    private void Reset()
    {
        pageIndex = -1;
        pressed = null;
        raw.Clear();
        view.SetPreview(-1, null);
    }

    IEnumerable<CanvasMark> ICanvasPreview.Marks(int index, Page page, ViewTransform transform)
    {
        if (index != pageIndex || raw.Count == 0) yield break;
        if (IsCurve)
        {
            // The dragged path; the fitted curve replaces it on release.
            if (raw.Count > 1)
                yield return new AnnotationMark(new InkStroke(Guid.Empty, false, Affine.Identity, [.. raw], currentStyle.WidthPt,
                    currentStyle.Color, currentStyle.Opacity));
            yield break;
        }
        if (raw[0] == end) yield break;
        yield return new AnnotationMark(new LineAnnotation(Guid.Empty, false, Affine.Identity, LineKind.Straight, raw[0], end, [],
            currentStyle.WidthPt, currentOptions.Dash, currentOptions.StartArrow, currentOptions.EndArrow, currentStyle.Color, currentStyle.Opacity));
    }
}
