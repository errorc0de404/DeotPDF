using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;
using SkiaSharp;

namespace PdfNotes.Render.Skia;

/// <summary>
/// App annotations on a Skia canvas (roadmap P4 A2), matching their PDF appearance (Pdf/Writing/AnnotationAppearance) and
/// the WPF <c>AnnotationPainter</c>: a stroke is one round-capped path (a pressure stroke or an eraser piece its filled
/// region), a single point is a dot of the stroke width, and opacity is applied once per annotation so a highlighter's
/// self-overlap does not darken while separate strokes do (spec 9.2). The canvas is in viewport DIPs;
/// <see cref="ViewTransform.PageToDip"/> places the page. Safe to call from the render thread: it reads only the
/// immutable model.
/// </summary>
public static class SkiaAnnotationPainter
{
    private static readonly ConditionalWeakTable<StrokeAnnotation, SKPath> Strokes = new();
    private static readonly ConditionalWeakTable<Resource, SKImage> Images = new();

    /// <summary>Paints every annotation of <paramref name="page"/> in stacking order (skipping those <paramref name="skip"/> names).</summary>
    public static void PaintPage(SKCanvas canvas, Page page, ViewTransform transform, Func<Guid, Resource?>? resources = null, Func<Annotation, bool>? skip = null)
    {
        foreach (var annotation in page.Annotations)
            if (skip?.Invoke(annotation) != true) Paint(canvas, annotation, transform, resources);
    }

    public static void Paint(SKCanvas canvas, Annotation annotation, ViewTransform transform, Func<Guid, Resource?>? resources = null, bool preview = false)
    {
        switch (annotation)
        {
            case StrokeAnnotation stroke: PaintStroke(canvas, stroke, transform, preview); break;
            case LineAnnotation line: PaintLine(canvas, line, transform); break;
            case ShapeAnnotation shape: PaintShape(canvas, shape, transform); break;
            case PdfTextHighlight highlight: PaintQuads(canvas, TextSelection.PaintedQuads(highlight), highlight.Color, highlight.Opacity, transform.PageToDip); break;
            case PdfTextMarkup markup: PaintQuads(canvas, TextSelection.PaintedQuads(markup), markup.Color, markup.Opacity, transform.PageToDip); break;
            case TextBoxAnnotation text: PaintText(canvas, text, transform); break;
            case ImageAnnotation image when resources?.Invoke(image.ResourceId) is { } resource: PaintImage(canvas, image, resource, transform); break;
            case StickyNote note: PaintNote(canvas, note, transform); break;
        }
    }

    public static SKMatrix Matrix(Affine m) => new((float)m.M11, (float)m.M21, (float)m.OffsetX, (float)m.M12, (float)m.M22, (float)m.OffsetY, 0, 0, 1);

    public static SKColor Color(RgbColor c, double opacity = 1) => new(c.R, c.G, c.B, (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255));

    private static int Push(SKCanvas canvas, Affine m)
    {
        int saved = canvas.Save();
        var matrix = Matrix(m);
        canvas.Concat(in matrix);
        return saved;
    }

    // ----- strokes -----

    private static void PaintStroke(SKCanvas canvas, StrokeAnnotation stroke, ViewTransform transform, bool preview)
    {
        int saved = Push(canvas, stroke.Transform.Then(transform.PageToDip));
        using var paint = new SKPaint { IsAntialias = true, Color = Color(stroke.Color, stroke.Opacity) };
        bool filled = stroke.IsOutlined || stroke is InkStroke { HasPressure: true };
        if (filled)
        {
            paint.Style = SKPaintStyle.Fill;
            // While drawing, the overlapping pieces as one non-zero figure (no polygon arithmetic per frame); one draw
            // call, so the opacity is applied once.
            if (preview && stroke is InkStroke { HasPressure: true } live) canvas.DrawPath(Rings([.. StrokeRegion.PressurePieces(live)], SKPathFillType.Winding), paint);
            else canvas.DrawPath(Strokes.GetValue(stroke, BuildFilled), paint);
        }
        else if (stroke.Points.Length == 1)
        {
            paint.Style = SKPaintStyle.Fill;
            canvas.DrawCircle((float)stroke.Points[0].X, (float)stroke.Points[0].Y, (float)(stroke.WidthPt / 2), paint);
        }
        else
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (float)stroke.WidthPt;
            paint.StrokeCap = SKStrokeCap.Round;
            paint.StrokeJoin = SKStrokeJoin.Round;
            canvas.DrawPath(preview ? Polyline(stroke.Points) : Strokes.GetValue(stroke, s => Polyline(s.Points)), paint);
        }
        canvas.RestoreToCount(saved);
    }

    private static SKPath BuildFilled(StrokeAnnotation stroke) =>
        Rings(stroke is InkStroke { HasPressure: true } ink ? StrokeRegion.PressureOutline(ink) : stroke.Outline, SKPathFillType.EvenOdd);

    public static SKPath Rings(ImmutableArray<ImmutableArray<Point2>> rings, SKPathFillType fill)
    {
        var path = new SKPath { FillType = fill };
        foreach (var ring in rings)
        {
            if (ring.Length == 0) continue;
            path.MoveTo((float)ring[0].X, (float)ring[0].Y);
            for (int i = 1; i < ring.Length; i++) path.LineTo((float)ring[i].X, (float)ring[i].Y);
            path.Close();
        }
        return path;
    }

    private static SKPath Polyline(IReadOnlyList<Point2> points)
    {
        var path = new SKPath();
        path.MoveTo((float)points[0].X, (float)points[0].Y);
        for (int i = 1; i < points.Count; i++) path.LineTo((float)points[i].X, (float)points[i].Y);
        return path;
    }

    // ----- lines -----

    private static void PaintLine(SKCanvas canvas, LineAnnotation line, ViewTransform transform)
    {
        int saved = Push(canvas, line.Transform.Then(transform.PageToDip));
        // Body and heads under one opacity, like the PDF transparency group (D07).
        bool layered = line.Opacity < 1;
        if (layered)
        {
            using var layer = new SKPaint { Color = new SKColor(0, 0, 0, (byte)Math.Round(line.Opacity * 255)) };
            canvas.SaveLayer(layer);
        }
        if (LineGeometry.Body(line) is { } p)
        {
            using var path = new SKPath();
            path.MoveTo((float)p[0].X, (float)p[0].Y);
            if (p.Length == 2) path.LineTo((float)p[1].X, (float)p[1].Y);
            else if (p.Length == 3) path.QuadTo((float)p[1].X, (float)p[1].Y, (float)p[2].X, (float)p[2].Y);
            else path.CubicTo((float)p[1].X, (float)p[1].Y, (float)p[2].X, (float)p[2].Y, (float)p[3].X, (float)p[3].Y);
            using var pen = Pen(line.Color, line.WidthPt, LineGeometry.DashPattern(line.Dash));
            canvas.DrawPath(path, pen);
        }
        using var heads = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var head in LineGeometry.ArrowHeads(line))
        {
            heads.MoveTo((float)head[0].X, (float)head[0].Y);
            heads.LineTo((float)head[1].X, (float)head[1].Y);
            heads.LineTo((float)head[2].X, (float)head[2].Y);
            heads.Close();
        }
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = Color(line.Color) };
        canvas.DrawPath(heads, fill);
        if (layered) canvas.Restore();
        canvas.RestoreToCount(saved);
    }

    /// <summary>A round-capped, round-joined pen; <paramref name="dashes"/> are in pen widths (as WPF dash styles are).</summary>
    private static SKPaint Pen(RgbColor color, double width, double[] dashes, double opacity = 1)
    {
        var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, Color = Color(color, opacity), StrokeWidth = (float)width,
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
        };
        // A zero-length dash is a round dot in PDF; Skia skips empty dashes, so it gets a hair of length.
        if (dashes.Length > 0) paint.PathEffect = SKPathEffect.CreateDash([.. dashes.Select(d => (float)Math.Max(d * width, 0.01))], 0);
        return paint;
    }

    // ----- shapes -----

    private static void PaintShape(SKCanvas canvas, ShapeAnnotation shape, ViewTransform transform)
    {
        int saved = Push(canvas, shape.Transform.Then(transform.PageToDip));
        using var outline = new SKPath { FillType = SKPathFillType.EvenOdd };
        if (ShapeGeometry.Vertices(shape) is { } vertices)
        {
            outline.MoveTo((float)vertices[0].X, (float)vertices[0].Y);
            foreach (var v in vertices.Skip(1)) outline.LineTo((float)v.X, (float)v.Y);
            if (shape.IsClosed) outline.Close();
        }
        else
        {
            var e = ShapeGeometry.EllipseCubics(shape.Box);
            outline.MoveTo((float)e[0].X, (float)e[0].Y);
            for (int i = 1; i < e.Length; i += 3)
                outline.CubicTo((float)e[i].X, (float)e[i].Y, (float)e[i + 1].X, (float)e[i + 1].Y, (float)e[i + 2].X, (float)e[i + 2].Y);
            outline.Close();
        }
        var style = shape.Style;
        if (ShapeGeometry.HasFill(shape))
        {
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = Color(style.FillColor, style.FillOpacity) };
            canvas.DrawPath(outline, fill);
        }
        if (style.BorderEnabled)
        {
            using var border = Pen(style.BorderColor, style.BorderWidthPt, LineGeometry.DashPattern(style.BorderDash), style.BorderOpacity);
            canvas.DrawPath(outline, border);
        }
        canvas.RestoreToCount(saved);
    }

    // ----- glyph marks -----

    /// <summary>Quads as one non-zero fill under one opacity (PDF glyph highlights, underlines, strikeouts).</summary>
    public static void PaintQuads(SKCanvas canvas, IEnumerable<Quad> quads, RgbColor color, double opacity, Affine toDip)
    {
        int saved = Push(canvas, toDip);
        using var path = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var quad in quads)
        {
            var ring = TextSelection.Ring(quad);
            path.MoveTo((float)ring[0].X, (float)ring[0].Y);
            foreach (var p in ring.Skip(1)) path.LineTo((float)p.X, (float)p.Y);
            path.Close();
        }
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = Color(color, opacity) };
        canvas.DrawPath(path, fill);
        canvas.RestoreToCount(saved);
    }

    // ----- text boxes, images, notes -----

    private static void PaintText(SKCanvas canvas, TextBoxAnnotation text, ViewTransform transform)
    {
        if (TextLayout.Measure(text) is not SkiaTextLayout layout) return; // drawn by the Skia engine only
        int saved = Push(canvas, text.Transform.Then(transform.PageToDip));
        SkiaTextRenderer.Draw(canvas, layout);
        canvas.RestoreToCount(saved);
    }

    private static void PaintImage(SKCanvas canvas, ImageAnnotation image, Resource resource, ViewTransform transform)
    {
        var picture = Images.GetValue(resource, r => SKImage.FromEncodedData(r.Payload.ToArray()));
        if (picture is null) return;
        int saved = Push(canvas, image.Transform.Then(transform.PageToDip));
        var b = image.LocalBounds;
        using var paint = new SKPaint();
        canvas.DrawImage(picture, SKRect.Create((float)b.X, (float)b.Y, (float)b.Width, (float)b.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        canvas.RestoreToCount(saved);
    }

    /// <summary>Sticky note icon (spec 24.9): the same rounded square and lines as its PDF appearance, in page points.</summary>
    private static void PaintNote(SKCanvas canvas, StickyNote note, ViewTransform transform)
    {
        int saved = Push(canvas, note.Transform.Then(transform.PageToDip));
        var edge = new RgbColor((byte)(note.Color.R / 2), (byte)(note.Color.G / 2), (byte)(note.Color.B / 2));
        var rect = new SKRoundRect(SKRect.Create(0, 0, (float)StickyNote.IconPt, (float)StickyNote.IconPt), 3, 3);
        using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = Color(note.Color) }) canvas.DrawRoundRect(rect, fill);
        using (var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Color(edge) }) canvas.DrawRoundRect(rect, border);
        using var lines = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, StrokeCap = SKStrokeCap.Round, Color = Color(edge) };
        canvas.DrawLine(5, 6.5f, 15, 6.5f, lines);
        canvas.DrawLine(5, 10, 15, 10, lines);
        canvas.DrawLine(5, 13.5f, 11, 13.5f, lines);
        canvas.RestoreToCount(saved);
    }
}
