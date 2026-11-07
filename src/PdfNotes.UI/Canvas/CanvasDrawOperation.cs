using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace PdfNotes.UI.Canvas;

/// <summary>
/// Draws one frame of the document canvas through Avalonia's Skia lease (render thread). While a tool shows a preview
/// (a stroke being drawn, a shape being dragged) the rest of the frame does not change from one pointer move to the next:
/// it is drawn once into <see cref="FrameCache"/> and each frame puts that picture down and draws the preview over it,
/// instead of the page pictures and all notes again (roadmap P5d: writing stuttered on the tablet).
/// <paramref name="baseVersion"/> changes whenever anything but the preview changed.
/// </summary>
internal sealed class CanvasDrawOperation(Rect bounds, IReadOnlyList<DocumentCanvas.PageShot> shots, DocumentCanvas.CanvasLook look,
    FrameCache? cache = null, int baseVersion = 0) : ICustomDrawOperation
{
    public Rect Bounds => bounds;
    public bool HitTest(Point p) => false;
    public bool Equals(ICustomDrawOperation? other) => false;
    public void Dispose() { }

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;
        canvas.Save();
        canvas.ClipRect(SKRect.Create(0, 0, (float)bounds.Width, (float)bounds.Height));
        if (cache is null || !shots.Any(s => s.Preview is not null || s.MarkPreview is not null || s.Live is not null) || !DrawCached(canvas, cache, lease.GrContext))
        {
            if (cache is not null) cache.Version = -1;
            PageDrawing.Draw(canvas, shots, look);
        }
        canvas.Restore();
    }

    /// <summary>The frame from the cached picture of everything but the previews; false where none can be made.</summary>
    private bool DrawCached(SKCanvas canvas, FrameCache cache, GRContext? gpu)
    {
        var matrix = canvas.TotalMatrix;
        float scale = matrix.ScaleX;
        if (matrix.SkewX != 0 || matrix.SkewY != 0 || scale <= 0 || Math.Abs(matrix.ScaleY - scale) > 1e-4) return false;
        int width = (int)Math.Ceiling(bounds.Width * scale), height = (int)Math.Ceiling(bounds.Height * scale);
        if (width <= 0 || height <= 0) return false;
        if (cache.Surface is null || cache.Width != width || cache.Height != height || cache.Scale != scale || !ReferenceEquals(cache.Gpu, gpu))
        {
            cache.Surface?.Dispose();
            var info = new SKImageInfo(width, height);
            cache.Surface = gpu is not null ? SKSurface.Create(gpu, false, info) : SKSurface.Create(info);
            (cache.Width, cache.Height, cache.Scale, cache.Gpu, cache.Version) = (width, height, scale, gpu, -1);
            if (cache.Surface is null) return false;
        }
        if (cache.Version != baseVersion)
        {
            var target = cache.Surface.Canvas;
            target.ResetMatrix();
            target.Clear(SKColors.Transparent);
            target.Scale(scale);
            PageDrawing.Draw(target, shots, look, previews: false);
            target.Flush();
            cache.Version = baseVersion;
        }
        using (var picture = cache.Surface.Snapshot())
            canvas.DrawImage(picture, SKRect.Create(0, 0, width / scale, height / scale), new SKSamplingOptions(SKFilterMode.Nearest), null);
        PageDrawing.DrawPreviews(canvas, shots, look);
        return true;
    }
}

/// <summary>The canvas's picture of everything but the tool previews (render thread only; see <see cref="CanvasDrawOperation"/>).</summary>
internal sealed class FrameCache
{
    public SKSurface? Surface;
    public int Width, Height, Version = -1;
    public float Scale;
    public GRContext? Gpu;
}

/// <summary>
/// The pages of one frame on a Skia canvas in viewport DIPs. Shared by the control and the tests, which draw it into an
/// offscreen surface and compare it with PDFium's own rendering (roadmap P4 A1, A2).
/// </summary>
public static class PageDrawing
{
    /// <summary>The reading dark page (spec 24.12): colors inverted, alpha kept.</summary>
    public static readonly SKColorFilter Invert = SKColorFilter.CreateColorMatrix(
    [
        -1, 0, 0, 0, 1,
        0, -1, 0, 0, 1,
        0, 0, -1, 0, 1,
        0, 0, 0, 1, 0
    ]);

    internal static void Draw(SKCanvas canvas, IReadOnlyList<DocumentCanvas.PageShot> shots, DocumentCanvas.CanvasLook look, bool previews = true)
    {
        using var fill = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        using var image = new SKPaint();
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        foreach (var shot in shots)
        {
            var t = shot.Transform;
            var o = t.PageOriginDip;
            var rect = SKRect.Create((float)o.X, (float)o.Y, (float)t.PageWidthDip, (float)t.PageHeightDip);
            double dpi = t.DpiScale;

            // Shadow below and to the right, then the content layer (inverted on dark pages, spec 24.12).
            fill.Color = look.Shadow;
            canvas.DrawRect(SKRect.Create(rect.Left + 2, rect.Top + 2, rect.Width, rect.Height), fill);
            if (look.DarkPages)
            {
                using var layer = new SKPaint { ColorFilter = Invert };
                canvas.SaveLayer(layer);
            }
            else canvas.Save();
            canvas.ClipRect(rect);
            fill.Color = look.Paper;
            canvas.DrawRect(rect, fill);
            // Pictures go where their own pixels per point put them (the WPF PixelRectToLocal): 1:1 at the zoom they were made
            // for, stretched in place while a new one renders after a zoom.
            if (shot.Base is { } b)
            {
                double perPixel = shot.BaseKey is { } bk ? t.DipsPerPagePoint / bk.PixelsPerPoint : 1 / dpi;
                canvas.DrawImage(b, SKRect.Create(rect.Left, rect.Top, (float)(b.Width * perPixel), (float)(b.Height * perPixel)), sampling, image);
            }
            // Grid tiles are made at the screen's pixels and drawn 1:1, so neighbors meet without a seam.
            foreach (var (tileKey, tile) in shot.Tiles)
            {
                if (tileKey.Region is not { } region) continue;
                double perPixel = t.DipsPerPagePoint / tileKey.PixelsPerPoint;
                canvas.DrawImage(tile, SKRect.Create((float)(o.X + region.X * perPixel), (float)(o.Y + region.Y * perPixel), (float)(region.Width * perPixel), (float)(region.Height * perPixel)), new SKSamplingOptions(SKFilterMode.Nearest), image);
            }
            if (shot.Failed && look.FailedText.Length > 0)
            {
                using var font = new SKFont(SKTypeface.Default, 14);
                using var ink = new SKPaint { Color = look.PaperText, IsAntialias = true };
                float w = font.MeasureText(look.FailedText);
                canvas.DrawText(look.FailedText, rect.Left + Math.Max(4, (rect.Width - w) / 2), rect.Top + Math.Max(18, rect.Height / 2), SKTextAlign.Left, font, ink);
            }
            if (look.PageNumberSize > 0) DrawPageNumber(canvas, shot.Index + 1, rect, look);
            shot.Overlay?.Invoke(canvas, shot.Index, shot.Page, t);
            if (previews)
            {
                shot.Live?.Invoke(canvas, shot.Index, shot.Page, t);
                shot.Preview?.Invoke(canvas, shot.Index, shot.Page, t);
            }
            canvas.Restore();

            using (var edge = new SKPaint { Color = look.Edge, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(1 / dpi), IsAntialias = false })
                canvas.DrawRect(rect, edge);
            // Screen marks are never inverted and not clipped to the page (handles may stick out). Over a kept frame they
            // are drawn each time (a moved selection's handles move with it).
            if (previews) shot.Marks?.Invoke(canvas, shot.Index, shot.Page, t);
            if (previews && shot.MarkPreview is { } preview)
            {
                // Tool marks (eraser circle, lasso) stay inside the page like the WPF view's.
                canvas.Save();
                canvas.ClipRect(rect);
                preview(canvas, shot.Index, shot.Page, t);
                canvas.Restore();
            }
        }
    }

    /// <summary>Only the tool previews of a frame, placed and clipped as <see cref="Draw"/> places them (over a cached frame).</summary>
    internal static void DrawPreviews(SKCanvas canvas, IReadOnlyList<DocumentCanvas.PageShot> shots, DocumentCanvas.CanvasLook look)
    {
        foreach (var shot in shots)
        {
            var t = shot.Transform;
            var o = t.PageOriginDip;
            var rect = SKRect.Create((float)o.X, (float)o.Y, (float)t.PageWidthDip, (float)t.PageHeightDip);
            if (shot.Live is not null || shot.Preview is not null)
            {
                int saved = canvas.Save();
                canvas.ClipRect(rect);
                if (look.DarkPages)
                {
                    using var layer = new SKPaint { ColorFilter = Invert };
                    canvas.SaveLayer(rect, layer);
                }
                shot.Live?.Invoke(canvas, shot.Index, shot.Page, t);
                shot.Preview?.Invoke(canvas, shot.Index, shot.Page, t);
                canvas.RestoreToCount(saved);
            }
            shot.Marks?.Invoke(canvas, shot.Index, shot.Page, t);
            if (shot.MarkPreview is { } mark)
            {
                canvas.Save();
                canvas.ClipRect(rect);
                mark(canvas, shot.Index, shot.Page, t);
                canvas.Restore();
            }
        }
    }

    private static void DrawPageNumber(SKCanvas canvas, int number, SKRect page, DocumentCanvas.CanvasLook look)
    {
        using var font = new SKFont(SKTypeface.Default, (float)look.PageNumberSize);
        using var paint = new SKPaint { Color = look.PageNumber, IsAntialias = true };
        string text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        float width = font.MeasureText(text);
        const float Space = 8; // Space.PageNumber
        canvas.DrawText(text, page.Right - Space - width, page.Bottom - Space, SKTextAlign.Left, font, paint);
    }
}
