using PdfNotes.Core.Geometry;
using PdfNotes.Core.Marks;
using SkiaSharp;

namespace PdfNotes.Render.Skia;

/// <summary>
/// The settled part of a stroke being drawn, kept in pixels between frames (roadmap P5d). Each frame adds only the
/// pieces that settled since the last one and puts the picture down under the moving end, so a long stroke draws as fast
/// as a short one. Starts over for another stroke or when the view moved. Use it from one drawing thread.
/// </summary>
public sealed class StrokeLayer
{
    private SKSurface? surface;
    private Guid key;
    private SKMatrix matrix;
    private SKRectI area;
    private int drawn;

    /// <summary>Draws <paramref name="mark"/> with the kept picture; false when it cannot be kept here (the caller draws it all).</summary>
    public bool Draw(SKCanvas canvas, StrokeProgressMark mark, ViewTransform transform)
    {
        var m = canvas.TotalMatrix;
        if (m.SkewX != 0 || m.SkewY != 0 || m.Persp0 != 0 || m.Persp1 != 0) return false;
        var clip = canvas.DeviceClipBounds;
        if (clip.Width <= 0 || clip.Height <= 0) return false;
        if (surface is null || key != mark.Key || !m.Equals(matrix) || clip != area || drawn > mark.Settled.Count)
        {
            surface?.Dispose();
            var info = new SKImageInfo(clip.Width, clip.Height);
            surface = (canvas.Context is { } gpu ? SKSurface.Create(gpu, false, info) : null) ?? SKSurface.Create(info);
            if (surface is null) return false;
            (key, matrix, area, drawn) = (mark.Key, m, clip, 0);
            surface.Canvas.Clear(SKColors.Transparent);
        }
        if (drawn < mark.Settled.Count)
        {
            var target = surface.Canvas;
            target.ResetMatrix();
            target.Translate(-area.Left, -area.Top);
            target.Concat(in m);
            for (; drawn < mark.Settled.Count; drawn++) SkiaAnnotationPainter.Paint(target, mark.Settled[drawn], transform, preview: true);
        }
        int saved = Group(canvas, mark.Opacity);
        canvas.Save();
        canvas.ResetMatrix();
        using (var picture = surface.Snapshot()) canvas.DrawImage(picture, area.Left, area.Top);
        canvas.Restore();
        if (mark.Tail is { } tail) SkiaAnnotationPainter.Paint(canvas, tail, transform, preview: true);
        canvas.RestoreToCount(saved);
        return true;
    }

    /// <summary>The whole mark without a kept picture.</summary>
    public static void DrawAll(SKCanvas canvas, StrokeProgressMark mark, ViewTransform transform)
    {
        int saved = Group(canvas, mark.Opacity);
        foreach (var piece in mark.Settled) SkiaAnnotationPainter.Paint(canvas, piece, transform, preview: true);
        if (mark.Tail is { } tail) SkiaAnnotationPainter.Paint(canvas, tail, transform, preview: true);
        canvas.RestoreToCount(saved);
    }

    /// <summary>The opaque pieces are shown at the stroke's opacity as one, so their overlaps do not darken.</summary>
    private static int Group(SKCanvas canvas, double opacity)
    {
        int saved = canvas.Save();
        if (opacity < 1)
        {
            using var paint = new SKPaint { Color = new SKColor(0, 0, 0, (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255)) };
            canvas.SaveLayer(paint);
        }
        return saved;
    }
}
