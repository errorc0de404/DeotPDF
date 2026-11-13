using System.Collections.Immutable;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Rendering;
using PdfNotes.Pdf.Writing;

namespace PdfNotes.Cli;

/// <summary>
/// Page pixels for <c>render</c> (cli_plan 4.2). The paper without annotations comes from the app's
/// <see cref="PageRenderer"/>; annotations are drawn by rendering the page's Flatten PDF (the same appearance the app
/// exports and external viewers show; PDFium ignores annotation /CA, so the editable PDF itself would lose opacity).
/// Composing images (overlays, grids) and writing PNG files is the platform part (<c>Platform/Wpf</c> for the shipped
/// Windows build, <c>Platform/Skia</c> for <c>-p:DeotEngine=Skia</c>, roadmap P2 S7).
/// </summary>
public static partial class PageRaster
{
    public static (int Width, int Height) PixelSize(PageSetup setup, double pixelsPerPoint) => PageRenderer.PixelSize(setup, pixelsPerPoint);

    /// <summary>Canonical page points to pixels of the whole displayed page at <paramref name="pixelsPerPoint"/>.</summary>
    public static Affine PageToFullPixel(PageSetup setup, double pixelsPerPoint) =>
        setup.Frame.CanonicalToDisplay.Then(Affine.Scale(pixelsPerPoint));

    /// <summary>Renders <paramref name="region"/> (full page when null) with the annotations <paramref name="include"/> accepts.</summary>
    public static RenderedPage Render(CliDocument doc, Page page, double pixelsPerPoint, PixelRect? region, Func<Annotation, bool> include, out int drawn)
    {
        var annotations = page.Annotations.Where(include).ToImmutableList();
        drawn = annotations.Count;
        if (annotations.Count == 0) return doc.Document.Renderer.Render(page.Setup, pixelsPerPoint, region, CancellationToken.None);
        var model = doc.Model with { Pages = doc.Model.Pages.Select(p => p.Id == page.Id ? p with { Annotations = annotations } : p).ToImmutableList() };
        var (subset, pdf) = doc.Document.Sources.Subset(model, [page.Id]);
        try
        {
            var flat = FlattenWriter.Write(null, null, subset, pdf is null ? [] : [pdf]);
            using var engine = new PdfiumDocument(flat.Pdf);
            var (width, height) = PixelSize(page.Setup, pixelsPerPoint);
            return engine.RenderRegion(0, width, height, 0, region ?? new PixelRect(0, 0, width, height), true, CancellationToken.None);
        }
        finally { pdf?.Dispose(); }
    }
}

/// <summary>What <c>render</c> draws on a composed image (overlays, grid cells and labels), in pixels.</summary>
public interface IRasterCanvas
{
    void Fill(int x, int y, int width, int height, RgbColor color);
    void DrawImage(RenderedPage image, int x, int y);

    /// <summary>A closed polygon filled with <paramref name="color"/> at <paramref name="opacity"/> and outlined 1 px opaque.</summary>
    void Polygon(IReadOnlyList<Point2> points, RgbColor color, double opacity);

    /// <summary>A short black label with its top left at (x, y).</summary>
    void Label(string text, double x, double y, double size);
}
