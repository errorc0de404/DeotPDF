using PdfNotes.Core.Geometry;
using PdfNotes.Pdf.Engine;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Pdf.Reading;

/// <summary>Page geometry of one PDF page in Core terms.</summary>
public sealed record PdfPageGeometry(int PageIndex, PdfPageBox Box, int Rotation)
{
    public PageFrame Frame => Box.Frame(Rotation);
}

/// <summary>
/// Reads the visible page box, /Rotate and /UserUnit with double precision (PDFsharp) using the same rules as the
/// PDFium renderer: CropBox ∩ MediaBox, rotation truncated to quarter turns. PDFium reports boxes as float and
/// ignores /UserUnit, so the values here are the reference for stored geometry.
/// </summary>
public static class PdfPageGeometryReader
{
    public static PdfPageGeometry[] Read(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return Read(document);
    }

    public static PdfPageGeometry[] Read(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf, writable: false);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        return Read(document);
    }

    internal static PdfPageGeometry[] Read(PdfDocument document)
    {
        var result = new PdfPageGeometry[document.PageCount];
        for (int i = 0; i < result.Length; i++) result[i] = Read(document.Pages[i], i);
        return result;
    }

    internal static PdfPageGeometry Read(PdfPage page, int index)
    {
        var media = Normalize(page.MediaBox);
        var visible = media;
        if (page.Elements.ContainsKey("/CropBox"))
        {
            var crop = Normalize(page.CropBox);
            var intersection = (Left: Math.Max(media.Left, crop.Left), Bottom: Math.Max(media.Bottom, crop.Bottom),
                Right: Math.Min(media.Right, crop.Right), Top: Math.Min(media.Top, crop.Top));
            if (intersection.Right > intersection.Left && intersection.Top > intersection.Bottom) visible = intersection;
        }
        double userUnit = page.Elements.ContainsKey("/UserUnit") ? page.Elements.GetReal("/UserUnit") : 1;
        if (!double.IsFinite(userUnit) || userUnit <= 0) throw new InvalidDataException($"Page {index + 1} has an invalid /UserUnit.");
        int rotation = PageRotation.FromPdfValue(page.Elements.GetInteger("/Rotate"));
        return new(index, new PdfPageBox(visible.Left, visible.Bottom, visible.Right, visible.Top, userUnit), rotation);
    }

    /// <summary>Cross-check against the renderer. Returns the largest coordinate difference in user units, or throws on rotation mismatch.</summary>
    public static double CompareWithRenderer(PdfiumDocument renderer, IReadOnlyList<PdfPageGeometry> pages)
    {
        if (renderer.PageCount != pages.Count) throw new InvalidDataException("Page count differs between PDF readers.");
        double maximum = 0;
        foreach (var page in pages)
        {
            var native = renderer.GetPageBox(page.PageIndex);
            if (native.Rotation != page.Rotation) throw new InvalidDataException($"Page {page.PageIndex + 1} rotation differs between PDF readers.");
            maximum = new[]
            {
                Math.Abs(native.Left - page.Box.Left), Math.Abs(native.Bottom - page.Box.Bottom),
                Math.Abs(native.Right - page.Box.Right), Math.Abs(native.Top - page.Box.Top), maximum
            }.Max();
        }
        return maximum;
    }

    private static (double Left, double Bottom, double Right, double Top) Normalize(PdfRectangle r)
    {
        if (!double.IsFinite(r.X1) || !double.IsFinite(r.Y1) || !double.IsFinite(r.X2) || !double.IsFinite(r.Y2))
            throw new InvalidDataException("Page box is not finite.");
        return (Math.Min(r.X1, r.X2), Math.Min(r.Y1, r.Y2), Math.Max(r.X1, r.X2), Math.Max(r.Y1, r.Y2));
    }
}
