using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Text;
using SkiaSharp;

namespace PdfNotes.Cli;

/// <summary>The Skia imaging of the CLI built with <c>-p:DeotEngine=Skia</c> (roadmap P2 S7; no WPF, any thread, any OS).</summary>
public static partial class PageRaster
{
    /// <summary>A new image drawn through <see cref="IRasterCanvas"/>.</summary>
    public static RenderedPage Draw(int width, int height, Action<IRasterCanvas> draw)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            draw(new SkiaCanvas(canvas));
        }
        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++) Marshal.Copy(bitmap.GetPixels() + y * bitmap.RowBytes, bgra, y * width * 4, width * 4);
        return new RenderedPage(width, height, width * 4, bgra);
    }

    public static void SavePng(RenderedPage page, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = Image(page);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static SKImage Image(RenderedPage page)
    {
        var info = new SKImageInfo(page.Width, page.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(page.Bgra, GCHandleType.Pinned);
        try
        {
            using var pixels = new SKPixmap(info, handle.AddrOfPinnedObject(), page.Stride);
            return SKImage.FromPixelCopy(pixels);
        }
        finally { handle.Free(); }
    }

    private sealed class SkiaCanvas(SKCanvas canvas) : IRasterCanvas
    {
        public void Fill(int x, int y, int width, int height, RgbColor color)
        {
            using var paint = new SKPaint { Color = new SKColor(color.R, color.G, color.B) };
            canvas.DrawRect(x, y, width, height, paint);
        }

        public void DrawImage(RenderedPage image, int x, int y)
        {
            using var picture = Image(image);
            canvas.DrawImage(picture, x, y);
        }

        public void Polygon(IReadOnlyList<Point2> points, RgbColor color, double opacity)
        {
            using var path = new SKPath();
            path.MoveTo((float)points[0].X, (float)points[0].Y);
            for (int i = 1; i < points.Count; i++) path.LineTo((float)points[i].X, (float)points[i].Y);
            path.Close();
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(color.R, color.G, color.B, (byte)Math.Round(opacity * 255)) };
            canvas.DrawPath(path, paint);
            paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 1; paint.Color = new SKColor(color.R, color.G, color.B);
            canvas.DrawPath(path, paint);
        }

        public void Label(string text, double x, double y, double size)
        {
            var face = SkiaFonts.Face(OperatingSystem.IsWindows() ? "Segoe UI" : SkiaFonts.FallbackFamily, false, false);
            using var font = new SKFont(face.Typeface, (float)size) { Subpixel = true };
            using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Black };
            canvas.DrawText(text, (float)x, (float)(y + face.Ascent * size), font, paint);
        }
    }
}

/// <summary>
/// Image files for <c>add image</c>: pixel size from Skia's decoder and the horizontal DPI from the file header (PNG
/// pHYs, JPEG JFIF density, BMP pixels per meter); 96 when the file does not say, as the WPF decoder reports.
/// </summary>
public static class ImageProbe
{
    public static (int Width, int Height, double DpiX) Read(byte[] bytes)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes)) ?? throw new InvalidDataException("The image cannot be decoded.");
        using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("The image cannot be decoded.");
        return (bitmap.Width, bitmap.Height, Dpi(bytes) ?? 96);
    }

    private static double? Dpi(byte[] b)
    {
        if (b.Length > 8 && b[0] == 137 && b[1] == 'P')
        {
            for (int at = 8; at + 12 <= b.Length;)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(at));
                if (length < 0 || at + 12 + length > b.Length) break;
                if (b[at + 4] == 'p' && b[at + 5] == 'H' && b[at + 6] == 'Y' && b[at + 7] == 's' && length >= 9 && b[at + 16] == 1)
                    return BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at + 8)) * .0254;
                if (b[at + 4] == 'I' && b[at + 5] == 'D' && b[at + 6] == 'A' && b[at + 7] == 'T') break;
                at += 12 + length;
            }
            return null;
        }
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8) return JpegDpi(b);
        if (b.Length > 46 && b[0] == 'B' && b[1] == 'M')
        {
            int perMeter = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(38));
            return perMeter > 0 ? perMeter * .0254 : null;
        }
        return null;
    }

    /// <summary>
    /// A JPEG's horizontal DPI from its JFIF density (APP0) or, when that has none, its Exif XResolution (APP1, IFD0) —
    /// phone and camera files usually carry only Exif. Segments are read up to the start of the image data.
    /// </summary>
    private static double? JpegDpi(byte[] b)
    {
        double? jfif = null, exif = null;
        for (int at = 2; at + 4 <= b.Length && b[at] == 0xFF;)
        {
            byte marker = b[at + 1];
            if (marker is 0xD9 or 0xDA) break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at + 2));
            if (length < 2 || at + 2 + length > b.Length) break;
            var segment = b.AsSpan(at + 4, length - 2);
            if (marker == 0xE0 && segment.Length >= 14 && segment[..5].SequenceEqual("JFIF\0"u8))
            {
                int units = segment[7], x = BinaryPrimitives.ReadUInt16BigEndian(segment[8..]);
                jfif = units switch { 1 => x, 2 => x * 2.54, _ => null };
            }
            else if (marker == 0xE1 && segment.Length >= 14 && segment[..6].SequenceEqual("Exif\0\0"u8)) exif = ExifDpi(segment[6..].ToArray());
            at += 2 + length;
        }
        return jfif is > 0 ? jfif : exif is > 0 ? exif : null;
    }

    private static double? ExifDpi(byte[] tiff)
    {
        if (tiff.Length < 8) return null;
        bool little = tiff[0] == 'I';
        ushort U16(int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(at));
        uint U32(int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(at));
        int ifd = (int)U32(4);
        if (ifd < 8 || ifd + 2 > tiff.Length) return null;
        double? resolution = null;
        int unit = 2; // inches when the file does not say
        int count = U16(ifd);
        for (int i = 0; i < count && ifd + 2 + 12 * (i + 1) <= tiff.Length; i++)
        {
            int entry = ifd + 2 + 12 * i;
            ushort tag = U16(entry);
            if (tag == 0x011A)
            {
                int offset = (int)U32(entry + 8);
                if (offset >= 0 && offset + 8 <= tiff.Length && U32(offset + 4) != 0) resolution = U32(offset) / (double)U32(offset + 4);
            }
            else if (tag == 0x0128) unit = U16(entry + 8);
        }
        return resolution is not { } r ? null : unit switch { 2 => r, 3 => r * 2.54, _ => null };
    }
}
