using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;

namespace PdfNotes.Pdf.Reading;

/// <summary>
/// A source PDF without the other apps' annotations that can become app annotations (spec 14.6, D14g), and those
/// annotations. Same pages and geometry as its source; pages whose <see cref="PageSource.ForeignConverted"/> is set render
/// from <see cref="Engine"/> and are saved from <see cref="Bytes"/>.
/// </summary>
public sealed class ForeignVariant : IDisposable
{
    private ForeignVariant(byte[] bytes, PdfiumDocument engine, ImmutableArray<ConvertedAnnotation> converted)
    {
        Bytes = bytes;
        Engine = engine;
        Converted = converted;
    }

    /// <summary>The source with the converted annotations (and their popups) removed: what a save writes.</summary>
    public byte[] Bytes { get; }

    /// <summary>Renders the background without them (and without the restored app annotations, like the source's own engine).</summary>
    internal PdfiumDocument Engine { get; }

    public ImmutableArray<ConvertedAnnotation> Converted { get; }

    /// <summary>
    /// The variant of a source (<paramref name="bytes"/>, rendered from <paramref name="rendered"/>), or null when nothing
    /// converts. A source PDFsharp cannot rewrite has nothing to convert: its annotations stay read-only.
    /// </summary>
    internal static ForeignVariant? Create(byte[] bytes, byte[] rendered, IReadOnlyList<PdfPageGeometry> pages)
    {
        ForeignConversion conversion;
        try { conversion = ForeignAnnotations.Convert(bytes, pages); }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        if (conversion.Converted.IsEmpty) return null;
        var background = ReferenceEquals(rendered, bytes) ? conversion.Pdf : ForeignAnnotations.Remove(rendered, conversion.RemovedKeys);
        return new(conversion.Pdf, new PdfiumDocument(background), conversion.Converted);
    }

    public void Dispose() => Engine.Dispose();
}

/// <summary>
/// Pages taken from another PDF (spec 11.9, D13g): only the chosen source pages, already stripped of our own app
/// annotations and app metadata. Kept in memory until a save appends them to the document's one source PDF.
/// </summary>
public sealed class ImportedPdf : IDisposable
{
    private readonly bool ownsEngine;
    private readonly Lazy<ForeignVariant?> foreign;

    internal ImportedPdf(byte[] bytes, PdfPageGeometry[] pages, PdfiumDocument engine, bool ownsEngine = true)
    {
        Bytes = bytes;
        Pages = pages;
        Engine = engine;
        this.ownsEngine = ownsEngine;
        foreign = new(() => ownsEngine ? ForeignVariant.Create(bytes, bytes, pages) : null, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public byte[] Bytes { get; }
    public IReadOnlyList<PdfPageGeometry> Pages { get; }
    internal PdfiumDocument Engine { get; }

    /// <summary>These pages without the other apps' annotations that convert (computed on first use; may take a while).</summary>
    public ForeignVariant? Foreign => foreign.Value;

    /// <summary>The converted pages as an import of their own (what a save appends), sharing the variant's engine.</summary>
    internal ImportedPdf Converted() => Foreign is { } v ? new(v.Bytes, [.. Pages], v.Engine, ownsEngine: false) : this;

    public void Dispose()
    {
        if (!ownsEngine) return;
        Engine.Dispose();
        if (foreign.IsValueCreated) foreign.Value?.Dispose();
    }
}

/// <summary>
/// The source pages of one open document: the opened PDF (absent for a new note) followed by imported pages in the order
/// they were imported. A model page's <see cref="PageSource.PdfPageIndex"/> counts through all of them, so imported pages
/// continue the numbering of the opened PDF. Only appended to, never shrunk: an undone import keeps its pages for redo,
/// and the next save leaves out what no page uses. Thread-safe for readers (renderer, glyph extraction).
/// </summary>
public sealed class DocumentSources : IDisposable
{
    private readonly bool ownsMain;
    private readonly object gate = new();
    private ImmutableList<ImportedPdf> imported = [];
    private ImmutableList<PdfPageGeometry> pages;

    public DocumentSources(PdfSourceDocument? main, bool ownsMain = true)
    {
        Main = main;
        this.ownsMain = ownsMain;
        pages = main is null ? [] : [.. main.Pages];
    }

    /// <summary>The opened PDF, or null for a new note.</summary>
    public PdfSourceDocument? Main { get; }

    /// <summary>Number of source pages of <see cref="Main"/>; imported pages start at this index.</summary>
    public int MainCount => Main?.Pages.Count ?? 0;

    /// <summary>Geometry of every source page, in source-index order.</summary>
    public IReadOnlyList<PdfPageGeometry> Pages => pages;

    public IReadOnlyList<ImportedPdf> Imported => imported;

    /// <summary>Adds imported pages and returns the source index of their first page.</summary>
    public int Append(ImportedPdf part)
    {
        lock (gate)
        {
            int first = pages.Count;
            imported = imported.Add(part);
            pages = pages.AddRange(part.Pages);
            return first;
        }
    }

    /// <summary>The source index the next <see cref="Append"/> will start at.</summary>
    public int NextIndex => pages.Count;

    /// <summary>Selectable glyphs of source page <paramref name="index"/>, in its unplaced canonical points.</summary>
    public PdfNotes.Core.Geometry.TextGlyph[] Glyphs(int index)
    {
        var (engine, _, pages, local) = Locate(index);
        return PdfTextReader.Glyphs(engine, pages[local]);
    }

    /// <summary>Links of source page <paramref name="index"/> (spec 24.3), targets in source numbering, unplaced canonical points.</summary>
    public PdfLink[] Links(int index)
    {
        var (engine, _, pages, local) = Locate(index);
        return PdfLinkReader.Links(engine, pages, local, index - local);
    }

    /// <summary>The outline of the opened PDF (spec 24.4); none for a new note. Imported PDFs bring no outline (11.9).</summary>
    public PdfOutlineItem[] Outline() => Main is { } main ? PdfLinkReader.Outline(main.Engine, main.Pages) : [];

    /// <summary>
    /// Engine, bytes and geometry of the PDF holding source page <paramref name="index"/>, with its page index there.
    /// <paramref name="converted"/>: the PDF without its converted foreign annotations (14.6) when there is such a variant.
    /// </summary>
    internal (PdfiumDocument Engine, byte[] Bytes, IReadOnlyList<PdfPageGeometry> Pages, int Local) Locate(int index, bool converted = false)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        if (Main is { } main && index < main.Pages.Count)
            return converted && main.Foreign is { } mv ? (mv.Engine, mv.Bytes, main.Pages, index) : (main.Engine, main.Bytes, main.Pages, index);
        int local = index - MainCount;
        foreach (var part in imported)
        {
            if (local < part.Pages.Count)
                return converted && part.Foreign is { } pv ? (pv.Engine, pv.Bytes, part.Pages, local) : (part.Engine, part.Bytes, part.Pages, local);
            local -= part.Pages.Count;
        }
        throw new InvalidDataException("Source page index is out of range.");
    }

    /// <summary>The foreign-annotation variant of the PDF holding source page <paramref name="index"/> and the page's index there.</summary>
    private (ForeignVariant? Variant, int Local) VariantOf(int index)
    {
        if (Main is { } main && index < main.Pages.Count) return (main.Foreign, index);
        int local = index - MainCount;
        foreach (var part in imported)
        {
            if (local < part.Pages.Count) return (part.Foreign, local);
            local -= part.Pages.Count;
        }
        return (null, -1);
    }

    /// <summary>
    /// How many annotations of other apps the 다른 프로그램 주석 편집 command can convert in the source PDFs (spec 14.6,
    /// D14g). Converts each source once on first use: call it off the UI thread.
    /// </summary>
    public int ForeignCount() => (Main?.Foreign?.Converted.Length ?? 0) + imported.Sum(p => p.Foreign?.Converted.Length ?? 0);

    /// <summary>
    /// The 다른 프로그램 주석 편집 command (spec 14.6, D14g) as one Undo unit, or null when there is nothing to convert: every
    /// page showing a source PDF with convertible foreign annotations switches to its converted variant and gets those
    /// annotations (new IDs, through its content placement, under its own app annotations). Call <see cref="ForeignCount"/>
    /// first off the UI thread so the variants exist.
    /// </summary>
    public EditCommand? ForeignConversion(DocumentModel model)
    {
        var operations = new List<EditOperation>();
        foreach (var page in model.Pages)
        {
            if (page.Setup.Source is not { Kind: PageSourceKind.Pdf, PdfPageIndex: int index, ForeignConverted: false }) continue;
            var (variant, local) = VariantOf(index);
            if (variant is null) continue;
            var mine = variant.Converted.Where(c => c.SourcePageIndex == local).Select(c => c.Annotation with { Id = Guid.NewGuid() }).ToList();
            if (page.Setup.Content is { } content && !content.Transform.IsIdentity)
                mine = mine.Select(a => PageTransforms.Transform(a, content.Transform, content.Transform.M11)).ToList();
            if (mine.Any(a => ModelValidator.ValidateAnnotation(a, _ => false) is not null)) continue; // stays read-only on this page
            operations.Add(new ReplacePageSetupOperation(page.Id, page.Setup, page.Setup with { Source = page.Setup.Source with { ForeignConverted = true } }));
            for (int i = 0; i < mine.Count; i++) operations.Add(new InsertAnnotationOperation(page.Id, i, mine[i]));
        }
        return operations.Count == 0 ? null : new EditCommand("undo.convertForeign", operations);
    }

    /// <summary>
    /// What a save writes: the opened PDF's bytes (null for a new note), the model with imported source indices renumbered
    /// so imported PDFs no page uses are left out, and those used PDFs in order. A source whose pages show its converted
    /// foreign annotations (14.6) is written as its variant, without them. Without imports the model is returned unchanged.
    /// </summary>
    public (byte[]? Main, DocumentModel Model, IReadOnlyList<ImportedPdf> Imported) ForSave(DocumentModel model)
    {
        int main = MainCount;
        var converted = model.Pages.Where(p => p.Setup.Source.ForeignConverted).Select(p => p.Setup.Source.PdfPageIndex).OfType<int>().ToHashSet();
        byte[]? mainBytes = Main is { } m ? converted.Any(i => i < main) && m.Foreign is { } variant ? variant.Bytes : m.Bytes : null;
        // The written sources are the variants already: the model goes to the writer without the flags.
        if (converted.Count > 0)
            model = model with { Pages = model.Pages.Select(p => p.Setup.Source.ForeignConverted
                ? p with { Setup = p.Setup with { Source = p.Setup.Source with { ForeignConverted = false } } } : p).ToImmutableList() };
        var parts = imported;
        if (parts.IsEmpty) return (mainBytes, model, []);
        var used = model.Pages.Select(p => p.Setup.Source.PdfPageIndex).OfType<int>().ToHashSet();
        var kept = new List<ImportedPdf>();
        var shift = new Dictionary<int, int>();
        int start = main, removed = 0;
        foreach (var part in parts)
        {
            bool any = Enumerable.Range(start, part.Pages.Count).Any(used.Contains);
            if (any)
            {
                kept.Add(Enumerable.Range(start, part.Pages.Count).Any(converted.Contains) ? part.Converted() : part);
                for (int i = 0; i < part.Pages.Count; i++) shift[start + i] = start + i - removed;
            }
            else removed += part.Pages.Count;
            start += part.Pages.Count;
        }
        if (removed == 0) return (mainBytes, model, kept);
        var renumbered = model with
        {
            Pages = model.Pages.Select(p => p.Setup.Source is { Kind: PageSourceKind.Pdf, PdfPageIndex: int index } && index >= main
                ? p with { Setup = p.Setup with { Source = p.Setup.Source with { PdfPageIndex = shift[index] } } }
                : p).ToImmutableList()
        };
        return (mainBytes, renumbered, kept);
    }

    /// <summary>
    /// Some pages as a document of their own (page export, spec 11.10, D13j): the pages in the given order with their
    /// annotations and placement, a new document ID, only the image resources they use, and one new source PDF holding only
    /// the source pages they show (numbered from 0), so exporting 3 pages of a long PDF does not carry the whole original.
    /// Links and outline items to pages left out are dropped from that source. Runs off the UI thread; the caller owns the
    /// returned source and writes it like an import into a new note (<c>EditablePdfWriter.Write(null, null, model, [pdf])</c>).
    /// </summary>
    public (DocumentModel Model, ImportedPdf? Pdf) Subset(DocumentModel model, IReadOnlyList<Guid> pageIds, CancellationToken cancellationToken = default)
    {
        if (pageIds.Count == 0) throw new ArgumentException("No page was chosen.", nameof(pageIds));
        var chosen = pageIds.Distinct().Select(model.GetPage).ToList(); // a page is in a file once
        var sources = chosen.Select(p => p.Setup.Source.PdfPageIndex).OfType<int>().Distinct().ToList();
        var converted = chosen.Where(p => p.Setup.Source.ForeignConverted).Select(p => p.Setup.Source.PdfPageIndex).OfType<int>().ToHashSet();
        var renumber = new Dictionary<int, int>();
        ImportedPdf? part = null;
        if (sources.Count > 0)
        {
            var expected = new List<PdfPageGeometry>();
            byte[] bytes;
            using (var subset = new PdfSharp.Pdf.PdfDocument())
            {
                // One stripped import document per source PDF (the opened one and each imported part).
                var opened = new Dictionary<byte[], PdfSharp.Pdf.PdfDocument>(ReferenceEqualityComparer.Instance);
                try
                {
                    foreach (int index in sources)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var (_, sourceBytes, sourcePages, local) = Locate(index, converted.Contains(index));
                        if (!opened.TryGetValue(sourceBytes, out var import))
                        {
                            using (EditablePdfReader.OpenStripped(sourceBytes, sourcePages, out var stripped, saveBackground: true))
                                import = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(stripped.BackgroundPdf, writable: false), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                            opened[sourceBytes] = import;
                        }
                        var page = Writing.PageContentWriter.AddImported(subset, import.Pages[local]);
                        foreach (var key in page.Elements.Keys.Where(k => k.StartsWith("/PdfNotes", StringComparison.Ordinal)).ToArray())
                            page.Elements.Remove(key);
                        renumber[index] = renumber.Count;
                        expected.Add(sourcePages[local]);
                    }
                }
                finally { foreach (var doc in opened.Values) doc.Dispose(); }
                Writing.PdfNavigation.Prune(subset); // links to pages that were not exported
                using var output = new MemoryStream();
                subset.Save(output, false);
                bytes = output.ToArray();
            }
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = PdfPageGeometryReader.Read(bytes);
            if (geometry.Length != expected.Count || expected.Zip(geometry).Any(p => !SameBox(p.First, p.Second)))
                throw new InvalidDataException("Exported pages changed their geometry.");
            var engine = new PdfiumDocument(bytes);
            try
            {
                if (engine.PageCount != geometry.Length || PdfPageGeometryReader.CompareWithRenderer(engine, geometry) > PdfSourceDocument.MaxReaderBoxDifference)
                    throw new InvalidDataException("PDF readers disagree on the exported pages.");
            }
            catch { engine.Dispose(); throw; }
            part = new ImportedPdf(bytes, geometry, engine);
        }
        var pages = chosen.Select(p => p.Setup.Source.PdfPageIndex is int index ? p with { Setup = p.Setup with { Source = PageSource.Pdf(renumber[index]) } } : p).ToImmutableList();
        var used = pages.SelectMany(p => p.Annotations).OfType<ImageAnnotation>().Select(i => i.ResourceId).ToHashSet();
        var resources = model.Resources.Where(r => used.Contains(r.Key)).ToImmutableDictionary();
        return (new DocumentModel(Guid.NewGuid(), pages, resources) { Tags = model.Tags }, part);
    }

    private static bool SameBox(PdfPageGeometry a, PdfPageGeometry b) =>
        a.Rotation == b.Rotation && Math.Abs(a.Box.Left - b.Box.Left) <= 1e-6 && Math.Abs(a.Box.Bottom - b.Box.Bottom) <= 1e-6 &&
        Math.Abs(a.Box.WidthPt - b.Box.WidthPt) <= 1e-6 && Math.Abs(a.Box.HeightPt - b.Box.HeightPt) <= 1e-6;

    public void Dispose()
    {
        foreach (var part in imported) part.Dispose();
        if (ownsMain) Main?.Dispose();
    }
}
