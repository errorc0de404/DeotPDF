using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using SkiaSharp;

namespace PdfNotes.Render.Skia;

/// <summary>
/// The AI tab's 강조 (MCP_LIVE_PLAN 6): a halo in one fixed color around AI writing, nothing dimmed. Pen and highlighter
/// strokes, lines and shapes get their own shape drawn wider underneath; text marks get their quads outlined; text
/// boxes, images and notes their bounds. Drawn in viewport DIPs, never saved.
/// </summary>
public static class SkiaAiHalo
{
    /// <summary>The AI color: a violet none of the accent colors uses.</summary>
    public static readonly SKColor Color = new(0xA0, 0x3C, 0xE8);

    /// <summary>True when the halo is the annotation's shape drawn wider underneath it (otherwise an outline on top).</summary>
    public static bool Under(Annotation annotation) => annotation is StrokeAnnotation or LineAnnotation or ShapeAnnotation;

    public static void Paint(SKCanvas canvas, Annotation annotation, ViewTransform transform, Func<TextBoxAnnotation, Rect2>? textBounds = null)
    {
        var map = transform.PageToDip;
        using var path = new SKPath();
        bool under = Under(annotation);
        if ((under || annotation is PdfTextHighlight or PdfTextMarkup) && Lasso.PaintedRegion(annotation, textBounds) is { Count: > 0 } region)
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
            if (bounds.Width <= 0 && bounds.Height <= 0) return;
            path.AddPoly([.. AnnotationGeometry.Corners(bounds, map).Select(p => new SKPoint((float)p.X, (float)p.Y))], close: true);
        }
        if (path.IsEmpty) return;
        if (under)
        {
            using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = 6f, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round, Color = Color.WithAlpha(0x70) };
            canvas.DrawPath(path, halo);
        }
        else
        {
            using var white = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f, StrokeJoin = SKStrokeJoin.Round, Color = SKColors.White.WithAlpha(0xC0) };
            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeJoin = SKStrokeJoin.Round, Color = Color };
            canvas.DrawPath(path, white);
            canvas.DrawPath(path, line);
        }
    }
}
