using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using SkiaSharp;

namespace PdfNotes.Render.Skia;

/// <summary>
/// The AI tab's 강조 (MCP_LIVE_PLAN 6): a soft glow in the user's halo color around AI writing, nothing dimmed. The glow
/// goes under the annotations: pen and highlighter strokes, lines and shapes glow around their own shape, text marks
/// around their quads, text boxes, images and notes around their bounds. Those with a frame-like outline (everything but
/// strokes, lines and shapes) also get a thin edge on top, so a box with a filled background still shows it. Drawn in
/// viewport DIPs, never saved.
/// </summary>
public static class SkiaAiHalo
{
    /// <summary>The default halo color: a warm highlighter yellow.</summary>
    public static readonly SKColor DefaultColor = new(0xFF, 0xC4, 0x00);

    private const float GlowWidth = 9f, GlowBlur = 4.5f, EdgeWidth = 1.5f;

    private static bool OwnShape(Annotation annotation) => annotation is StrokeAnnotation or LineAnnotation or ShapeAnnotation;

    private static SKPath? Outline(Annotation annotation, ViewTransform transform, Func<TextBoxAnnotation, Rect2>? textBounds)
    {
        var map = transform.PageToDip;
        var path = new SKPath();
        if ((OwnShape(annotation) || annotation is PdfTextHighlight or PdfTextMarkup) && Lasso.PaintedRegion(annotation, textBounds) is { Count: > 0 } region)
        {
            foreach (var polygon in region)
            {
                if (polygon.Count < 2) continue;
                path.AddPoly([.. polygon.Select(p => map.Transform(new Point2(p.x, p.y))).Select(p => new SKPoint((float)p.X, (float)p.Y))], close: true);
            }
        }
        else
        {
            var bounds = AnnotationGeometry.Bounds([annotation], textBounds);
            if (bounds.Width > 0 || bounds.Height > 0)
                path.AddPoly([.. AnnotationGeometry.Corners(bounds, map).Select(p => new SKPoint((float)p.X, (float)p.Y))], close: true);
        }
        if (!path.IsEmpty) return path;
        path.Dispose();
        return null;
    }

    /// <summary>The glow, to draw before the annotations of the page.</summary>
    public static void PaintGlow(SKCanvas canvas, Annotation annotation, ViewTransform transform, SKColor color, Func<TextBoxAnnotation, Rect2>? textBounds = null)
    {
        using var path = Outline(annotation, transform, textBounds);
        if (path is null) return;
        using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, GlowBlur);
        using var glow = new SKPaint
        {
            IsAntialias = true,
            // A stroke's glow fills its shape too (a thin pen line has almost no outline to glow from).
            Style = OwnShape(annotation) ? SKPaintStyle.StrokeAndFill : SKPaintStyle.Stroke,
            StrokeWidth = GlowWidth, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round,
            Color = color.WithAlpha(0xC0), MaskFilter = blur,
        };
        canvas.DrawPath(path, glow);
    }

    /// <summary>The thin edge, to draw after the annotations: for the kinds whose halo is a frame.</summary>
    public static void PaintEdge(SKCanvas canvas, Annotation annotation, ViewTransform transform, SKColor color, Func<TextBoxAnnotation, Rect2>? textBounds = null)
    {
        if (OwnShape(annotation)) return;
        using var path = Outline(annotation, transform, textBounds);
        if (path is null) return;
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = EdgeWidth, StrokeJoin = SKStrokeJoin.Round, Color = color.WithAlpha(0xE6) };
        canvas.DrawPath(path, edge);
    }
}
