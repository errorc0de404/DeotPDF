using System.IO;
using PdfNotes.Core.Models;

namespace PdfNotes.Editor.Services;

/// <summary>Image bytes as a model resource: size limit, media type from the file signature and model validation.</summary>
public static class ImageResources
{
    /// <summary>The resource for <paramref name="bytes"/>; the platform still checks that it decodes.</summary>
    public static Resource Create(byte[] bytes)
    {
        if (bytes.Length > ModelLimits.MaxResourceBytes) throw new InvalidDataException("Image file is too large.");
        string media = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71 }) ? "image/png" :
            bytes.AsSpan().StartsWith(new byte[] { 255, 216 }) ? "image/jpeg" : "image/bmp";
        var resource = Resource.Create(Guid.NewGuid(), media, bytes);
        if (ModelValidator.ValidateResource(resource, true) is { } error) throw new InvalidDataException(error);
        return resource;
    }
}
