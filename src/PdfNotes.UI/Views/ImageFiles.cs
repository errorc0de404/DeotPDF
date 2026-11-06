using System.Buffers.Binary;
using SkiaSharp;

namespace PdfNotes.UI.Views;

/// <summary>
/// Image files going into a document (spec 10), as the WPF ImageAssets: the whole picture must decode and have at most
/// 64 million pixels, and its stored resolution sets its natural size (WPF reads it from the file through BitmapFrame;
/// Skia does not report it, so it is read from the PNG pHYs, JPEG JFIF and BMP headers here; 96 DPI otherwise).
/// </summary>
public static class ImageFiles
{
    public const long MaxPixels = 64_000_000;

    /// <summary>Pixel size and DPI; throws <see cref="InvalidDataException"/> for a file that is not a usable picture.</summary>
    public static (int Width, int Height, double Dpi) Check(byte[] bytes)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes)) ?? throw new InvalidDataException("Not an image.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels) throw new InvalidDataException("Image dimensions are too large.");
        using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) throw new InvalidDataException("The image does not decode."); // a cut-off file too
        return (info.Width, info.Height, Dpi(bytes));
    }

    /// <summary>The horizontal resolution stored in the file, or 96.</summary>
    public static double Dpi(ReadOnlySpan<byte> b)
    {
        double dpi = 0;
        if (b.Length > 33 && b[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            // PNG chunks until IDAT: pHYs holds pixels per unit (unit 1 = metre).
            for (int at = 8; at + 12 <= b.Length;)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(b[at..]);
                var type = b.Slice(at + 4, 4);
                if (length < 0 || at + 12 + length > b.Length) break;
                if (type.SequenceEqual("pHYs"u8) && length >= 9 && b[at + 16] == 1) { dpi = BinaryPrimitives.ReadUInt32BigEndian(b[(at + 8)..]) * 0.0254; break; }
                if (type.SequenceEqual("IDAT"u8)) break;
                at += 12 + length;
            }
        }
        else if (b.Length > 20 && b[0] == 0xFF && b[1] == 0xD8)
        {
            // JPEG APP0 JFIF: units (1 = per inch, 2 = per cm) and the X density.
            for (int at = 2; at + 4 <= b.Length && b[at] == 0xFF;)
            {
                byte marker = b[at + 1];
                int length = BinaryPrimitives.ReadUInt16BigEndian(b[(at + 2)..]);
                if (marker == 0xE0 && length >= 14 && at + 4 + 10 <= b.Length && b.Slice(at + 4, 5).SequenceEqual("JFIF\0"u8))
                {
                    byte units = b[at + 11];
                    int density = BinaryPrimitives.ReadUInt16BigEndian(b[(at + 12)..]);
                    dpi = units == 1 ? density : units == 2 ? density * 2.54 : 0;
                    break;
                }
                if (marker is 0xDA or 0xD9) break;
                at += 2 + length;
            }
        }
        else if (b.Length > 42 && b[0] == (byte)'B' && b[1] == (byte)'M' && BinaryPrimitives.ReadUInt32LittleEndian(b[14..]) >= 40) // not the OS/2 12-byte header
            dpi = BinaryPrimitives.ReadInt32LittleEndian(b[38..]) * 0.0254; // BITMAPINFOHEADER biXPelsPerMeter
        return double.IsFinite(dpi) && dpi >= 1 ? dpi : 96;
    }
}
