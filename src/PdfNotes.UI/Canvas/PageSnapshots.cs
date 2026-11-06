using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Services;
using PdfNotes.Render.Skia;
using SkiaSharp;
using Page = PdfNotes.Core.Models.Page;

namespace PdfNotes.UI.Canvas;

/// <summary>
/// A page drawn whole, off screen: the PDF background (PDFium) with the app's notes on top (Skia painters). The page
/// list's thumbnails and the page size window's preview use it (the WPF RenderTargetBitmap pictures).
/// </summary>
public static class PageSnapshots
{
    /// <summary>
    /// Page <paramref name="page"/> at <paramref name="zoom"/> (DIPs per DIP of the page at 100%) on a screen of
    /// <paramref name="scale"/> pixels per DIP; an unrenderable PDF page is white. Null when cancelled.
    /// </summary>
    public static async Task<WriteableBitmap?> RenderAsync(OpenDocument doc, DocumentModel model, Page page, double zoom, double scale, bool notesHidden, bool darkPage, CancellationToken cancel)
    {
        var frame = page.Setup.Frame;
        double ppp = zoom * Units.DipsPerPoint * scale;
        Pdf.Engine.RenderedPage? rendered = null;
        try { rendered = await Task.Run(() => doc.Renderer.Render(page.Setup, ppp, null, cancel), cancel); }
        catch (OperationCanceledException) { return null; }
        catch (Exception) when (!cancel.IsCancellationRequested) { } // a page that cannot be drawn keeps a white picture
        if (cancel.IsCancellationRequested) return null;
        return Compose(rendered, model, page, new ViewTransform(frame, zoom, scale, new(0, 0)), notesHidden, darkPage);
    }

    /// <summary>The same, now (the page size window opens with its preview).</summary>
    public static WriteableBitmap Render(OpenDocument doc, DocumentModel model, Page page, double zoom, double scale)
    {
        Pdf.Engine.RenderedPage? rendered = null;
        try { rendered = doc.Renderer.Render(page.Setup, zoom * Units.DipsPerPoint * scale, null, CancellationToken.None); }
        catch (Exception) { }
        return Compose(rendered, model, page, new ViewTransform(page.Setup.Frame, zoom, scale, new(0, 0)), notesHidden: false, darkPage: false);
    }

    private static WriteableBitmap Compose(Pdf.Engine.RenderedPage? background, DocumentModel model, Page page, ViewTransform t, bool notesHidden, bool darkPage)
    {
        int width = Math.Max(1, (int)Math.Ceiling(t.PageWidthDip * t.DpiScale)), height = Math.Max(1, (int)Math.Ceiling(t.PageHeightDip * t.DpiScale));
        // 96 DPI: the size is the pixel size and the Image control scales it to the DIPs it is given (with a higher DPI
        // Avalonia's Image takes its source rectangle in pixels and shows only the top-left part on a scaled screen).
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var locked = bitmap.Lock();
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var surface = SKSurface.Create(info, locked.Address, locked.RowBytes))
        {
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            canvas.Scale((float)t.DpiScale);
            if (darkPage) { using var invert = new SKPaint { ColorFilter = PageDrawing.Invert }; canvas.SaveLayer(invert); }
            else canvas.Save();
            var area = SKRect.Create(0, 0, (float)t.PageWidthDip, (float)t.PageHeightDip);
            using (var paper = new SKPaint { Color = SKColors.White }) canvas.DrawRect(area, paper);
            if (background is { } b)
            {
                using var image = PageImages.FromPdfium(b);
                canvas.DrawImage(image, area, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
            canvas.ClipRect(area);
            if (!notesHidden) SkiaAnnotationPainter.PaintPage(canvas, page, t, id => model.Resources.GetValueOrDefault(id));
            canvas.Restore();
        }
        return bitmap;
    }
}
