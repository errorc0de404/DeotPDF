using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using PdfNotes.Core.Models;

namespace PdfNotes.App.Services;

/// <summary>Detached image decoding and a bounded display resolution; the original bytes remain in the model.</summary>
public static class ImageAssets
{
    private static readonly ConditionalWeakTable<Resource, BitmapSource> Cache = new();
    public static BitmapSource Display(Resource resource) => Cache.GetValue(resource, r => Decode(r.Payload.ToArray(), 2048));
    public static BitmapSource Decode(byte[] bytes, int maxSide = 0)
    {
        using var stream = new MemoryStream(bytes);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > 64_000_000)
            throw new InvalidDataException("Image dimensions are too large.");
        if (maxSide > 0 && Math.Max(frame.PixelWidth, frame.PixelHeight) > maxSide)
        {
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream;
            if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = maxSide; else image.DecodePixelHeight = maxSide;
            image.EndInit(); image.Freeze(); return image;
        }
        frame.Freeze(); return frame;
    }
    public static Resource Read(byte[] bytes)
    {
        var resource = ImageResources.Create(bytes);
        Decode(bytes);
        return resource;
    }
}
