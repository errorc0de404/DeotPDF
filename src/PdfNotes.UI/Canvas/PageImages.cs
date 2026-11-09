using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PdfNotes.Pdf.Engine;
using SkiaSharp;

namespace PdfNotes.UI.Canvas;

/// <summary>PDFium pixels (premultiplied BGRA) as an immutable Skia image (render thread) or a bitmap for an Image control.</summary>
public static class PageImages
{
    /// <summary>
    /// The image uses PDFium's own pixel array in place (roadmap P5d A5: no copy of the whole picture). The array stays
    /// pinned while the image lives and is a normal managed array, so the GC sees its size without extra pressure; a
    /// dropped page image is never disposed (a frame on the render thread may still draw it) and goes with its finalizer.
    /// </summary>
    public static SKImage FromPdfium(RenderedPage page)
    {
        var info = new SKImageInfo(page.Width, page.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pin = GCHandle.Alloc(page.Bgra, GCHandleType.Pinned);
        var image = SKImage.FromPixels(new SKPixmap(info, pin.AddrOfPinnedObject(), page.Stride), (_, context) => ((GCHandle)context!).Free(), pin);
        if (image is null)
        {
            pin.Free();
            throw new InvalidOperationException("Page image could not be made.");
        }
        return image;
    }

    /// <summary>
    /// Premultiplied BGRA rows as a bitmap for an Image control, at 96 DPI so its size is its pixel size (the control
    /// scales it to the DIPs it is given; see <see cref="PageSnapshots"/>).
    /// </summary>
    public static WriteableBitmap ToBitmap(byte[] bgra, int width, int height, int stride)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var locked = bitmap.Lock())
            for (int y = 0; y < height; y++)
                Marshal.Copy(bgra, y * stride, locked.Address + y * locked.RowBytes, Math.Min(stride, locked.RowBytes));
        return bitmap;
    }
}
