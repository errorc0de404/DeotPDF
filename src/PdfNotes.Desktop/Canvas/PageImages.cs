using PdfNotes.Pdf.Engine;
using SkiaSharp;

namespace PdfNotes.Desktop.Canvas;

/// <summary>PDFium pixels (premultiplied BGRA) as an immutable Skia image, made on the render thread.</summary>
public static class PageImages
{
    public static SKImage FromPdfium(RenderedPage page)
    {
        var info = new SKImageInfo(page.Width, page.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var data = SKData.CreateCopy(page.Bgra);
        return SKImage.FromPixels(info, data, page.Stride) ?? throw new InvalidOperationException("Page image could not be made.");
    }
}
