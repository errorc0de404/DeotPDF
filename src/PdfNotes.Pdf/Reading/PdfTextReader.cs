using PdfNotes.Core.Geometry;
using PdfNotes.Pdf.Engine;

namespace PdfNotes.Pdf.Reading;

/// <summary>One extracted glyph. Index is PDFium's reading-order character index; Quad is in canonical page points.</summary>
public sealed record PdfTextCharacter(int Index, string Text, Quad Quad);

public static class PdfTextReader
{
    /// <summary>
    /// Glyphs of a source page in reading order for selection and highlights (spec 9.3), in canonical page points.
    /// PDFium reports the rotation clockwise in PDF user space (a line turned 30° counterclockwise reads 330°), so the
    /// user-space direction is (cos a, −sin a); it becomes the canonical reading direction.
    /// </summary>
    public static TextGlyph[] Glyphs(PdfiumDocument document, PdfPageGeometry page)
    {
        var toCanonical = page.Box.PdfUserToCanonical;
        return document.ExtractText(page.PageIndex).Select(c =>
        {
            var quad = new Quad(new(c.Quad[0].X, c.Quad[0].Y), new(c.Quad[1].X, c.Quad[1].Y),
                new(c.Quad[2].X, c.Quad[2].Y), new(c.Quad[3].X, c.Quad[3].Y)).Transform(toCanonical);
            var d = toCanonical.TransformVector(new(Math.Cos(c.Angle), -Math.Sin(c.Angle)));
            double length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            return new TextGlyph(c.Text, quad, length > 1e-12 ? new(d.X / length, d.Y / length) : new(1, 0));
        }).ToArray();
    }

    /// <summary>Extracts glyph quads (no OCR) and converts them from PDF user space to canonical page points.</summary>
    public static PdfTextCharacter[] ExtractCanonical(PdfiumDocument document, PdfPageGeometry page)
    {
        var toCanonical = page.Box.PdfUserToCanonical;
        return document.ExtractText(page.PageIndex).Select(c => new PdfTextCharacter(c.Index, c.Text,
            new Quad(new(c.Quad[0].X, c.Quad[0].Y), new(c.Quad[1].X, c.Quad[1].Y),
                new(c.Quad[2].X, c.Quad[2].Y), new(c.Quad[3].X, c.Quad[3].Y)).Transform(toCanonical))).ToArray();
    }
}
