using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;
using SkiaSharp;
using PdfNotes.Core.Marks;

namespace PdfNotes.Render.Skia;

/// <summary>
/// The colors of the marks drawn on the paper (Canvas.xaml / Colors.axaml, the same in every theme). The defaults are
/// the token values; the app passes what its resources say.
/// </summary>
public sealed record MarkColors
{
    public SKColor Selection { get; init; } = new(0x1E, 0x6F, 0xD9);
    public SKColor Halo { get; init; } = SKColors.White;
    public SKColor TextSelection { get; init; } = new(0x1E, 0x6F, 0xD9, 0x55);
    public SKColor LassoFill { get; init; } = new(0x1E, 0x6F, 0xD9, 0x14);
    public SKColor Eraser { get; init; } = new(0x50, 0x50, 0x50);
    public SKColor EraserHalo { get; init; } = new(0xFF, 0xFF, 0xFF, 0xC0);
    public SKColor EraserFill { get; init; } = new(0x50, 0x50, 0x50, 0x18);
    public SKColor Search { get; init; } = new(0xFF, 0x9A, 0x00, 0x55);
    public SKColor SearchCurrent { get; init; } = new(0xFF, 0x6A, 0x00, 0x80);
    public SKColor SearchCurrentEdge { get; init; } = new(0xE0, 0x50, 0x00);

    public static MarkColors Default { get; } = new();
}

/// <summary>
/// Tool previews (<see cref="CanvasMark"/>) and selection marks on a Skia canvas in viewport DIPs — the WPF
/// <c>ToolMarkPainter</c> and the selection parts of <c>AnnotationPainter</c> (roadmap P4 A2).
/// </summary>
public static class SkiaMarkPainter
{
    public static void Paint(SKCanvas canvas, IEnumerable<CanvasMark> marks, ViewTransform transform, MarkColors? colors = null)
    {
        colors ??= MarkColors.Default;
        foreach (var mark in marks)
        {
            switch (mark)
            {
                case AnnotationMark { Annotation: var annotation }: SkiaAnnotationPainter.Paint(canvas, annotation, transform, preview: true); break;
                case EraserCircleMark circle: PaintCircle(canvas, circle, transform, colors); break;
                case LassoMark lasso: PaintLasso(canvas, lasso, transform, colors); break;
                case RubberBandMark band: PaintRubberBand(canvas, band, transform, colors); break;
                case TextBoxHighlightMark highlight: PaintBoxHighlight(canvas, highlight, transform); break;
            }
        }
    }

    private static SKPaint Stroke(SKColor color, float width, bool dashed = false)
    {
        var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color, StrokeWidth = width, StrokeJoin = SKStrokeJoin.Round };
        if (dashed) paint.PathEffect = SKPathEffect.CreateDash([4 * width, 3 * width], 0);
        return paint;
    }

    private static SKPaint Fill(SKColor color) => new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };

    private static SKPoint P(Point2 p) => new((float)p.X, (float)p.Y);

    private static void PaintCircle(SKCanvas canvas, EraserCircleMark circle, ViewTransform transform, MarkColors colors)
    {
        var c = P(transform.PageToDip.Transform(circle.Center));
        float r = (float)(transform.DipsPerPagePoint * circle.DiameterPt / 2);
        using (var fill = Fill(colors.EraserFill)) canvas.DrawCircle(c, r, fill);
        using (var halo = Stroke(colors.EraserHalo, 3)) canvas.DrawCircle(c, r, halo);
        using var edge = Stroke(colors.Eraser, 1);
        canvas.DrawCircle(c, r, edge);
    }

    private static void PaintLasso(SKCanvas canvas, LassoMark lasso, ViewTransform transform, MarkColors colors)
    {
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.AddPoly([.. lasso.Path.Select(p => P(transform.PageToDip.Transform(p)))], close: true);
        using (var fill = Fill(colors.LassoFill)) canvas.DrawPath(path, fill);
        using (var halo = Stroke(colors.Halo, 3)) canvas.DrawPath(path, halo);
        using var line = Stroke(colors.Selection, 1.2f, dashed: true);
        canvas.DrawPath(path, line);
    }

    private static void PaintRubberBand(SKCanvas canvas, RubberBandMark band, ViewTransform transform, MarkColors colors)
    {
        var toDip = band.ToPage.Then(transform.PageToDip);
        var last = P(toDip.Transform(band.Last)); var now = P(toDip.Transform(band.Pointer)); var first = P(toDip.Transform(band.First));
        using var halo = Stroke(colors.Halo, 3); halo.StrokeCap = SKStrokeCap.Round;
        using var line = Stroke(colors.Selection, 1, dashed: true);
        canvas.DrawLine(last, now, halo);
        canvas.DrawLine(last, now, line);
        canvas.DrawCircle(first, 4, halo);
        using (var fill = Fill(colors.Halo)) canvas.DrawCircle(first, 4, fill);
        canvas.DrawCircle(first, 4, line);
    }

    private static void PaintBoxHighlight(SKCanvas canvas, TextBoxHighlightMark mark, ViewTransform transform)
    {
        var m = SkiaAnnotationPainter.Matrix(mark.Box.Transform.Then(transform.PageToDip));
        int saved = canvas.Save();
        canvas.Concat(in m);
        foreach (var (h, lines) in TextLayout.HighlightLines(mark.Preview, TextLayout.Measure(mark.Box).Characters))
        {
            using var path = new SKPath { FillType = SKPathFillType.Winding };
            foreach (var r in lines) path.AddRect(SKRect.Create((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height));
            using var fill = Fill(SkiaAnnotationPainter.Color(h.Color, h.Opacity));
            canvas.DrawPath(path, fill);
        }
        canvas.RestoreToCount(saved);
    }

    // ----- selection (viewport DIPs, computed by the selection tool) -----

    /// <summary>Selection frame with its four corner handles and the rotation handle (handles[4]).</summary>
    public static void PaintSelectionFrame(SKCanvas canvas, Point2[] handles, Point2[]? frameCorners = null, MarkColors? colors = null)
    {
        colors ??= MarkColors.Default;
        if (handles.Length != 5) return;
        var corners = frameCorners ?? handles.Take(4).ToArray();
        Outline(canvas, corners, colors);
        using var inner = Stroke(colors.Selection, 1.2f, dashed: true);
        var top = new SKPoint((float)(corners[0].X + corners[1].X) / 2, (float)(corners[0].Y + corners[1].Y) / 2);
        canvas.DrawLine(top, P(handles[4]), inner);
        using var fill = Fill(colors.Halo);
        using var edge = Stroke(colors.Selection, 1.2f, dashed: true);
        foreach (var p in handles.Take(4).Where(p => p.X != double.MaxValue))
        {
            var r = SKRect.Create((float)p.X - 4, (float)p.Y - 4, 8, 8);
            canvas.DrawRect(r, fill);
            canvas.DrawRect(r, edge);
        }
        canvas.DrawCircle(P(handles[4]), 5, fill);
        canvas.DrawCircle(P(handles[4]), 5, edge);
    }

    /// <summary>A frame without handles (glyph highlights cannot be moved).</summary>
    public static void PaintSelectionOutline(SKCanvas canvas, Point2[] corners, MarkColors? colors = null)
    {
        if (corners.Length == 4) Outline(canvas, corners, colors ?? MarkColors.Default);
    }

    private static void Outline(SKCanvas canvas, Point2[] corners, MarkColors colors)
    {
        using var path = new SKPath();
        path.AddPoly([.. corners.Select(P)], close: true);
        using (var outer = Stroke(colors.Halo, 3)) canvas.DrawPath(path, outer);
        using var inner = Stroke(colors.Selection, 1.2f, dashed: true);
        canvas.DrawPath(path, inner);
    }

    /// <summary>Handles of a single selected line: squares on the ends, circles on the control points, guides between.</summary>
    public static void PaintLineHandles(SKCanvas canvas, Point2[] handles, MarkColors? colors = null)
    {
        colors ??= MarkColors.Default;
        if (handles.Length < 2) return;
        using var outer = Stroke(colors.Halo, 3);
        using var dashed = Stroke(colors.Selection, 1.2f, dashed: true);
        using var solid = Stroke(colors.Selection, 1.2f);
        using var fill = Fill(colors.Halo);
        void Guide(Point2 a, Point2 b) { canvas.DrawLine(P(a), P(b), outer); canvas.DrawLine(P(a), P(b), dashed); }
        if (handles.Length == 3) { Guide(handles[0], handles[2]); Guide(handles[2], handles[1]); }
        else if (handles.Length == 4) { Guide(handles[0], handles[2]); Guide(handles[3], handles[1]); }
        foreach (var p in handles.Skip(2)) { canvas.DrawCircle(P(p), 4.5f, fill); canvas.DrawCircle(P(p), 4.5f, solid); }
        foreach (var p in handles.Take(2))
        {
            var r = SKRect.Create((float)p.X - 4, (float)p.Y - 4, 8, 8);
            canvas.DrawRect(r, fill);
            canvas.DrawRect(r, solid);
        }
    }

    /// <summary>Selected PDF text: translucent line quads in viewport DIPs.</summary>
    public static void PaintTextSelection(SKCanvas canvas, IEnumerable<Quad> quads, ViewTransform transform, MarkColors? colors = null)
    {
        using var path = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var quad in quads) path.AddPoly([.. TextSelection.Ring(quad).Select(transform.PageToDip.Transform).Select(P)], close: true);
        using var fill = Fill((colors ?? MarkColors.Default).TextSelection);
        canvas.DrawPath(path, fill);
    }

    /// <summary>Search results (spec 12): all matches, the current one stronger with an edge.</summary>
    public static void PaintSearch(SKCanvas canvas, IEnumerable<Quad> matches, IEnumerable<Quad> current, ViewTransform transform, MarkColors? colors = null)
    {
        colors ??= MarkColors.Default;
        SKPath Path(IEnumerable<Quad> quads)
        {
            var path = new SKPath { FillType = SKPathFillType.Winding };
            foreach (var quad in quads) path.AddPoly([.. TextSelection.Ring(quad).Select(transform.PageToDip.Transform).Select(P)], close: true);
            return path;
        }
        using (var all = Path(matches)) using (var fill = Fill(colors.Search)) canvas.DrawPath(all, fill);
        using var now = Path(current);
        using (var fill = Fill(colors.SearchCurrent)) canvas.DrawPath(now, fill);
        using var edge = Stroke(colors.SearchCurrentEdge, 1);
        canvas.DrawPath(now, edge);
    }
}
