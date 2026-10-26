using System.Collections.Immutable;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Pdf.Reading;

/// <summary>
/// Pages chosen from another PDF, ready to insert (spec 11.9): the source pages they show (null when every chosen page is
/// a generated note page), model pages with new IDs whose source indices continue after the document's own, and the
/// image resources their annotations use.
/// </summary>
public sealed record PageImport(ImportedPdf? Pdf, ImmutableArray<Page> Pages, ImmutableArray<Resource> Resources);

public static class PdfPageImport
{
    /// <summary>
    /// Takes the pages at <paramref name="pageIndices"/> (zero-based, in the order the file shows them) from a PDF opened
    /// with <see cref="PdfSourceDocument.Open"/>, so it passed the same checks as opening. App annotations restored from
    /// a file this app saved come along as editable annotations with new IDs, with their page placement; other content
    /// and foreign annotations stay part of the page. Only the needed source pages are kept. Nothing outside the result
    /// is changed; any failure throws before the document is touched.
    /// </summary>
    public static PageImport Build(PdfSourceDocument file, IReadOnlyList<int> pageIndices, int firstSourceIndex, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstSourceIndex);
        var model = file.CreateModel();
        if (pageIndices.Count == 0) throw new ArgumentException("No page was chosen.", nameof(pageIndices));
        if (pageIndices.Any(i => i < 0 || i >= model.Pages.Count)) throw new ArgumentOutOfRangeException(nameof(pageIndices));
        var chosen = pageIndices.Select(i => model.Pages[i]).ToList();
        var sources = chosen.Select(p => p.Setup.Source.PdfPageIndex).OfType<int>().Distinct().ToList();
        var renumber = new Dictionary<int, int>();
        ImportedPdf? part = null;
        if (sources.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            using (EditablePdfReader.OpenStripped(file.Bytes, file.Pages, out var stripped, saveBackground: true))
            using (var import = PdfReader.Open(new MemoryStream(stripped.BackgroundPdf, writable: false), PdfDocumentOpenMode.Import))
            using (var subset = new PdfDocument())
            {
                foreach (int index in sources)
                {
                    var page = Writing.PageContentWriter.AddImported(subset, import.Pages[index]);
                    // Page-level app keys (background fingerprint) belong to the file it came from.
                    foreach (var key in page.Elements.Keys.Where(k => k.StartsWith("/PdfNotes", StringComparison.Ordinal)).ToArray())
                        page.Elements.Remove(key);
                    renumber[index] = firstSourceIndex + renumber.Count;
                }
                using var output = new MemoryStream();
                subset.Save(output, false);
                bytes = output.ToArray();
            }
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = PdfPageGeometryReader.Read(bytes);
            if (geometry.Length != sources.Count || sources.Select((index, i) => !Same(file.Pages[index], geometry[i])).Any(changed => changed))
                throw new InvalidDataException("Imported pages changed their geometry.");
            var engine = new PdfiumDocument(bytes);
            try
            {
                if (engine.PageCount != geometry.Length || PdfPageGeometryReader.CompareWithRenderer(engine, geometry) > PdfSourceDocument.MaxReaderBoxDifference)
                    throw new InvalidDataException("PDF readers disagree on the imported pages.");
            }
            catch { engine.Dispose(); throw; }
            part = new ImportedPdf(bytes, geometry, engine);
        }
        var pages = chosen.Select(page => page with
        {
            Id = Guid.NewGuid(),
            Setup = page.Setup.Source.PdfPageIndex is int index ? page.Setup with { Source = PageSource.Pdf(renumber[index]) } : page.Setup,
            Annotations = page.Annotations.Select(a => a with { Id = Guid.NewGuid() }).ToImmutableList()
        }).ToImmutableArray();
        var resources = pages.SelectMany(p => p.Annotations).OfType<ImageAnnotation>().Select(i => i.ResourceId).Distinct()
            .Select(id => model.Resources[id]).ToImmutableArray();
        return new(part, pages, resources);
    }

    private static bool Same(PdfPageGeometry a, PdfPageGeometry b) =>
        a.Rotation == b.Rotation && Math.Abs(a.Box.Left - b.Box.Left) <= 1e-6 && Math.Abs(a.Box.Bottom - b.Box.Bottom) <= 1e-6 &&
        Math.Abs(a.Box.WidthPt - b.Box.WidthPt) <= 1e-6 && Math.Abs(a.Box.HeightPt - b.Box.HeightPt) <= 1e-6;
}
