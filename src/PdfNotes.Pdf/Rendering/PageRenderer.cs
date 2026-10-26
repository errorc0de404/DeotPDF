using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;

namespace PdfNotes.Pdf.Rendering;

/// <summary>
/// Renders model pages: source-PDF pages from the opened file and generated pages from their background data.
/// Thread-safe (PDFium calls are serialized inside <see cref="PdfiumDocument"/>). Does not own the source document.
/// </summary>
public sealed class PageRenderer : IDisposable
{
    private const int MaxGeneratedDocuments = 16;
    private readonly DocumentSources? source;
    private readonly object gate = new();
    private readonly LinkedList<(PageSetup Setup, PdfiumDocument Document)> generated = new();
    private bool disposed;

    public PageRenderer(DocumentSources source) => this.source = source;

    public PageRenderer(PdfSourceDocument? source) => this.source = source is null ? null : new DocumentSources(source, ownsMain: false);

    /// <summary>
    /// Device-pixel size of the whole displayed (rotated) page. App layout and rendering both use this so the
    /// bitmap always maps 1:1 onto the page rectangle.
    /// </summary>
    public static (int Width, int Height) PixelSize(PageSetup setup, double pixelsPerPoint)
    {
        var frame = setup.Frame;
        return (Math.Max(1, (int)Math.Ceiling(frame.DisplayWidthPt * pixelsPerPoint - 1e-6)),
                Math.Max(1, (int)Math.Ceiling(frame.DisplayHeightPt * pixelsPerPoint - 1e-6)));
    }

    /// <summary>Renders <paramref name="region"/> (full page when null) of the page at <paramref name="pixelsPerPoint"/>.</summary>
    public RenderedPage Render(PageSetup setup, double pixelsPerPoint, PixelRect? region, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(pixelsPerPoint) || pixelsPerPoint <= 0) throw new ArgumentOutOfRangeException(nameof(pixelsPerPoint));
        var (width, height) = PixelSize(setup, pixelsPerPoint);
        var area = region ?? new PixelRect(0, 0, width, height);
        cancellationToken.ThrowIfCancellationRequested();
        if (setup.Source.Kind == PageSourceKind.Pdf)
        {
            if (source is null) throw new InvalidOperationException("The page refers to a source PDF that is not open.");
            int index = setup.Source.PdfPageIndex!.Value;
            if (index >= source.Pages.Count) throw new InvalidDataException("Source page index is out of range.");
            if (setup.Content is not null)
                lock (gate) return Generated(setup).RenderRegion(0, width, height, 0, area, true, cancellationToken);
            int extra = (setup.Rotation - source.Pages[index].Rotation + 360) % 360 / 90;
            var (engine, _, _, local) = source.Locate(index, setup.Source.ForeignConverted);
            return engine.RenderRegion(local, width, height, extra, area, true, cancellationToken);
        }
        // Generated pages carry their rotation in the generated PDF. Render under the cache lock so eviction
        // cannot dispose the document mid-render.
        lock (gate) return Generated(setup).RenderRegion(0, width, height, 0, area, true, cancellationToken);
    }

    /// <summary>Most-recently-used cache of one-page PDFs per distinct generated setup. Call under <see cref="gate"/>.</summary>
    private PdfiumDocument Generated(PageSetup setup)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        for (var node = generated.First; node is not null; node = node.Next)
        {
            if (!node.Value.Setup.Equals(setup)) continue;
            generated.Remove(node);
            generated.AddFirst(node);
            return node.Value.Document;
        }
        byte[] bytes;
        if (setup.Source.Kind == PageSourceKind.Pdf)
        {
            // The same vector writer is used for screen, thumbnails and saved pages; no background rasterization.
            bytes = PageContentWriter.SingleBackground(source!, setup);
        }
        else bytes = GeneratedPageWriter.CreateSinglePagePdf(setup);
        var document = new PdfiumDocument(bytes);
        generated.AddFirst((setup, document));
        while (generated.Count > MaxGeneratedDocuments)
        {
            generated.Last!.Value.Document.Dispose();
            generated.RemoveLast();
        }
        return document;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var (_, document) in generated) document.Dispose();
            generated.Clear();
        }
    }
}
