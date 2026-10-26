using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
/// </summary>
public static class PageRaster
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

    public static BitmapSource ToBitmap(RenderedPage page)
    {
        var bitmap = BitmapSource.Create(page.Width, page.Height, 96, 96, PixelFormats.Bgra32, null, page.Bgra, page.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A new image drawn by <paramref name="draw"/> at 1 DIP = 1 pixel.</summary>
    public static BitmapSource Compose(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) draw(context);
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    public static void SavePng(BitmapSource image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static void DrawLabel(DrawingContext context, string text, double x, double y, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.Black, 1.0);
        context.DrawText(formatted, new System.Windows.Point(x, y));
    }

    public static StreamGeometry Polygon(IReadOnlyList<Point2> points)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new System.Windows.Point(points[0].X, points[0].Y), true, true);
            for (int i = 1; i < points.Count; i++) g.LineTo(new System.Windows.Point(points[i].X, points[i].Y), true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
