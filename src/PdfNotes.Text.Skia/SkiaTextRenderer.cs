using PdfNotes.Core.Models;
using SkiaSharp;

namespace PdfNotes.Text;

/// <summary>
/// Draws a Skia layout on a Skia canvas in box-local points (roadmap P2 S2; the Avalonia editor in S6 draws with it).
/// Same scene and order as the PDF outlines: fill, border, highlights, then glyphs as positioned text blobs and bars.
/// </summary>
public static class SkiaTextRenderer
{
    public static void Draw(SKCanvas canvas, SkiaTextLayout layout)
    {
        var scene = SkiaTextScene.Of(layout.Unscaled);
        int saved = canvas.Save();
        if (layout.Scale != 1) canvas.Scale((float)layout.Scale);
        canvas.ClipRect(new SKRect(0, 0, (float)scene.Width, (float)scene.Height), antialias: true);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        var style = scene.Style;
        if (style.BackgroundOpacity > 0)
        {
            fill.Color = Color(style.BackgroundColor, style.BackgroundOpacity);
            canvas.DrawRect(0, 0, (float)scene.Width, (float)scene.Height, fill);
        }
        if (style.BorderEnabled)
        {
            float b = (float)scene.BorderWidth;
            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = b, Color = Color(style.BorderColor, style.BorderOpacity) };
            canvas.DrawRect(b / 2, b / 2, Math.Max(0, (float)scene.Width - b), Math.Max(0, (float)scene.Height - b), stroke);
        }
        foreach (var (h, lines) in scene.Highlights)
        {
            // One fill per highlight: overlapping line rectangles do not darken twice.
            using var path = new SKPath { FillType = SKPathFillType.Winding };
            foreach (var r in lines) path.AddRect(new SKRect((float)r.X, (float)r.Y, (float)(r.X + r.Width), (float)(r.Y + r.Height)));
            fill.Color = Color(h.Color, h.Opacity);
            canvas.DrawPath(path, fill);
        }
        foreach (var item in scene.Content)
        {
            if (item is SceneGlyphs run)
            {
                using var font = SkiaTextScene.Font(run);
                using var builder = new SKTextBlobBuilder();
                var buffer = builder.AllocatePositionedRun(font, run.Glyphs.Length);
                buffer.SetGlyphs(run.Glyphs);
                buffer.SetPositions(run.Positions);
                using var blob = builder.Build();
                fill.Color = Color(run.Color, 1);
                if (blob is not null) canvas.DrawText(blob, 0, 0, fill);
            }
            else if (item is SceneBar bar)
            {
                fill.Color = Color(bar.Color, 1);
                canvas.DrawRect((float)bar.Rect.X, (float)bar.Rect.Y, (float)bar.Rect.Width, (float)bar.Rect.Height, fill);
            }
        }
        canvas.RestoreToCount(saved);
    }

    private static SKColor Color(RgbColor c, double alpha) => new(c.R, c.G, c.B, (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255));
}
