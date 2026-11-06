using System.Runtime.CompilerServices;
using PdfNotes.Pdf.Engine;
using SkiaSharp;

namespace PdfNotes.UI.Canvas;

/// <summary>PDFium pixels (premultiplied BGRA) as an immutable Skia image, made on the render thread.</summary>
public static class PageImages
{
    // The pixels are native memory the GC does not see. A dropped page image is never disposed (a frame on the render
    // thread may still draw it), so it is freed by its finalizer; the pressure makes the GC run often enough for that.
    private static readonly ConditionalWeakTable<SKImage, Pressure> Pressures = [];

    public static SKImage FromPdfium(RenderedPage page)
    {
        var info = new SKImageInfo(page.Width, page.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var data = SKData.CreateCopy(page.Bgra);
        var image = SKImage.FromPixels(info, data, page.Stride) ?? throw new InvalidOperationException("Page image could not be made.");
        Pressures.Add(image, new Pressure((long)page.Stride * page.Height));
        return image;
    }

    /// <summary>Lives as long as its image (the weak table's value); its finalizer takes the pressure back.</summary>
    private sealed class Pressure
    {
        private readonly long bytes;
        public Pressure(long bytes) { this.bytes = Math.Max(1, bytes); GC.AddMemoryPressure(this.bytes); }
        ~Pressure() => GC.RemoveMemoryPressure(bytes);
    }
}
