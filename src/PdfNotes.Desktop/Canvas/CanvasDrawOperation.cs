using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace PdfNotes.Desktop.Canvas;

/// <summary>Draws one frame of the document canvas through Avalonia's Skia lease (render thread).</summary>
internal sealed class CanvasDrawOperation(Rect bounds, IReadOnlyList<DocumentCanvas.PageShot> shots, DocumentCanvas.CanvasLook look) : ICustomDrawOperation
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
        PageDrawing.Draw(canvas, shots, look);
        canvas.Restore();
    }
}

/// <summary>
/// The pages of one frame on a Skia canvas in viewport DIPs. Shared by the control and the tests, which draw it into an
/// offscreen surface and compare it with PDFium's own rendering (roadmap P4 A1, A2).
/// </summary>
public static class PageDrawing
{
    private static readonly SKColorFilter Invert = SKColorFilter.CreateColorMatrix(
    [
        -1, 0, 0, 0, 1,
        0, -1, 0, 0, 1,
        0, 0, -1, 0, 1,
        0, 0, 0, 1, 0
    ]);

    internal static void Draw(SKCanvas canvas, IReadOnlyList<DocumentCanvas.PageShot> shots, DocumentCanvas.CanvasLook look)
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
            if (shot.Base is { } b) canvas.DrawImage(b, rect, sampling, image);
            if (shot.Detail is { } d && shot.DetailKey?.Region is { } region)
                canvas.DrawImage(d, SKRect.Create((float)(o.X + region.X / dpi), (float)(o.Y + region.Y / dpi), (float)(region.Width / dpi), (float)(region.Height / dpi)), sampling, image);
            if (look.PageNumberSize > 0) DrawPageNumber(canvas, shot.Index + 1, rect, look);
            look.Overlay?.Invoke(canvas, shot.Index, shot.Page, t);
            if (shot.Index == look.PreviewPage) look.Preview?.Invoke(canvas, shot.Index, shot.Page, t);
            canvas.Restore();

            using (var edge = new SKPaint { Color = look.Edge, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(1 / dpi), IsAntialias = false })
                canvas.DrawRect(rect, edge);
            // Screen marks are never inverted and not clipped to the page (handles may stick out).
            look.Marks?.Invoke(canvas, shot.Index, shot.Page, t);
            if (shot.Index == look.PreviewPage) look.MarkPreview?.Invoke(canvas, shot.Index, shot.Page, t);
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
