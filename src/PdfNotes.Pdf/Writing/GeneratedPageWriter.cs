using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfNotes.Pdf.Writing;

/// <summary>
/// Writes generated note backgrounds (spec 4.1) as real PDF page content. The viewer renders the same PDF bytes,
/// so the screen matches what a save will write and what other viewers and printers show.
/// </summary>
public static class GeneratedPageWriter
{
    /// <summary>Line width of grid/ruled backgrounds in points.</summary>
    public const double LineWidthPt = 0.5;

    /// <summary>Draws the background in canonical page points (origin top-left, before rotation).</summary>
    public static void DrawBackground(XGraphics graphics, PageSetup setup)
    {
        var background = setup.Background;
        if (background.Kind == PageBackgroundKind.Blank) return; // white paper: the page itself
        var color = background.LineColor;
        var pen = new XPen(XColor.FromArgb(color.R, color.G, color.B), LineWidthPt);
        double step = Units.MillimetersToPoints(background.SpacingMm);
        foreach (double y in Positions(setup.HeightPt, step)) graphics.DrawLine(pen, 0, y, setup.WidthPt, y);
        if (background.Kind == PageBackgroundKind.Grid)
            foreach (double x in Positions(setup.WidthPt, step)) graphics.DrawLine(pen, x, 0, x, setup.HeightPt);
    }

    /// <summary>Line offsets: every <paramref name="step"/> from the top/left edge, excluding the edges themselves.</summary>
    public static IEnumerable<double> Positions(double length, double step)
    {
        if (!(step > 0)) yield break;
        for (int i = 1; i * step < length - 1e-9; i++) yield return i * step;
    }

    /// <summary>Adds one generated page (size, background and /Rotate) to a PDFsharp document.</summary>
    public static PdfPage AddPage(PdfDocument document, PageSetup setup) => InsertPage(document, document.PageCount, setup);

    /// <summary>Inserts one generated page at <paramref name="index"/>.</summary>
    public static PdfPage InsertPage(PdfDocument document, int index, PageSetup setup)
    {
        if (setup.Source.Kind != PageSourceKind.Generated) throw new ArgumentException("Only generated pages can be written here.", nameof(setup));
        var page = document.InsertPage(index);
        page.Width = XUnit.FromPoint(setup.WidthPt);
        page.Height = XUnit.FromPoint(setup.HeightPt);
        using (var graphics = XGraphics.FromPdfPage(page)) DrawBackground(graphics, setup);
        // Set after drawing so PDFsharp does not re-orient the drawing surface.
        if (setup.Rotation != 0) page.Elements.SetInteger("/Rotate", setup.Rotation);
        return page;
    }

    /// <summary>A standalone one-page PDF of a generated page, used by the renderer.</summary>
    public static byte[] CreateSinglePagePdf(PageSetup setup)
    {
        using var document = new PdfDocument();
        AddPage(document, setup);
        using var buffer = new MemoryStream();
        document.Save(buffer, false);
        return buffer.ToArray();
    }
}
