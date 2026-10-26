using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using PdfNotes.Editor.Platform;

namespace PdfNotes.App.Services;

/// <summary>The Windows clipboard for the editor (D14d). A busy clipboard is reported as <see cref="ClipboardUnavailableException"/>.</summary>
public sealed class WpfClipboard : IEditorClipboard
{
    public bool ContainsData(string format) => Use(() => Clipboard.ContainsData(format));
    public string? GetData(string format) => Use(() => Clipboard.GetData(format) as string);
    public void SetData(string format, string data) => Use(() => { Clipboard.SetData(format, data); return true; });
    public bool ContainsText() => Use(Clipboard.ContainsText);
    public string GetText() => Use(Clipboard.GetText);
    public void SetText(string text) => Use(() => { Clipboard.SetText(text); return true; });
    public bool ContainsImage() => Use(Clipboard.ContainsImage);

    public byte[]? GetImagePng()
    {
        if (Use(Clipboard.GetImage) is not { } bitmap) return null;
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 64_000_000) throw new InvalidDataException();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }

    private static T Use<T>(Func<T> action)
    {
        try { return action(); }
        catch (ExternalException e) { throw new ClipboardUnavailableException(e); }
    }
}
