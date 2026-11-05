using System.IO;

namespace PdfNotes.Tests.Cli;

/// <summary>
/// PNG files for the CLI tests: WPF imaging in the Windows test project, Skia in the test project of the WPF-free CLI
/// build (<c>tests/PdfNotes.Cli.Tests</c>, roadmap P2 S7).
/// </summary>
internal static class TestPng
{
    /// <summary>Writes straight BGRA pixels as a PNG.</summary>
    public static void Save(string path, int width, int height, byte[] bgra)
    {
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    /// <summary>A PNG's size and straight BGRA pixels.</summary>
    public static (int Width, int Height, byte[] Bgra) Load(string path)
    {
        using var decoded = SkiaSharp.SKBitmap.Decode(path);
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(decoded.Width, decoded.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul));
        decoded.CopyTo(bitmap, SkiaSharp.SKColorType.Bgra8888);
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        for (int y = 0; y < bitmap.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels() + y * bitmap.RowBytes, pixels, y * bitmap.Width * 4, bitmap.Width * 4);
        return (bitmap.Width, bitmap.Height, pixels);
    }
}
