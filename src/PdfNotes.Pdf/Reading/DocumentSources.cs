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
    /// converts. A source PDFsharp cannot rewrite, or whose rewrite PDFium cannot open, has nothing to convert: its
    /// annotations stay read-only. Never throws, so the lazy value that holds it never keeps an exception to rethrow: not
    /// even running out of memory on a large PDF, which only means the annotations stay read-only.
    /// </summary>
    internal static ForeignVariant? Create(byte[] bytes, byte[] rendered, IReadOnlyList<PdfPageGeometry> pages)
    {
        try
        {
            var conversion = ForeignAnnotations.Convert(bytes, pages);
            if (conversion.Converted.IsEmpty) return null;
            var background = ReferenceEquals(rendered, bytes) ? conversion.Pdf : ForeignAnnotations.Remove(rendered, conversion.RemovedKeys);
            return new(conversion.Pdf, new PdfiumDocument(background), conversion.Converted);
        }
        catch (Exception) { return null; }
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
        // A variant leaves out the convertible annotations of every page of its source, so a source converts all its
        // pages or none: one page that cannot take its annotations keeps the whole source read-only (saved as is).
        var planned = new List<(Page Page, ForeignVariant Variant, List<Annotation> Mine)>();
        var failed = new HashSet<ForeignVariant>(ReferenceEqualityComparer.Instance);
        foreach (var page in model.Pages)
        {
            if (page.Setup.Source is not { Kind: PageSourceKind.Pdf, PdfPageIndex: int index, ForeignConverted: false }) continue;
            var (variant, local) = VariantOf(index);
            if (variant is null) continue;
            var mine = variant.Converted.Where(c => c.SourcePageIndex == local).Select(c => c.Annotation with { Id = Guid.NewGuid() }).ToList();
            if (page.Setup.Content is { } content && !content.Transform.IsIdentity)
                mine = mine.Select(a => PageTransforms.Transform(a, content.Transform, content.Transform.M11)).ToList();
            if (mine.Any(a => ModelValidator.ValidateAnnotation(a, _ => false) is not null)) failed.Add(variant);
            planned.Add((page, variant, mine));
        }
        var operations = new List<EditOperation>();
        foreach (var (page, variant, mine) in planned)
        {
            if (failed.Contains(variant)) continue;
            operations.Add(new ReplacePageSetupOperation(page.Id, page.Setup, page.Setup with { Source = page.Setup.Source with { ForeignConverted = true } }));
            for (int i = 0; i < mine.Count; i++) operations.Add(new InsertAnnotationOperation(page.Id, i, mine[i]));
        }
        return operations.Count == 0 ? null : new EditCommand("undo.convertForeign", operations);
    }

    private enum SourceUse { Original, Variant, Mixed }

    /// <summary>
    /// How a save writes the source holding source pages <paramref name="start"/> to <paramref name="start"/> +
    /// <paramref name="count"/>: as its converted variant when some page shows its converted annotations and no page still
    /// shows foreign annotations the variant leaves out; as its original plus the converted pages (appended, see
    /// <see cref="ForSave"/>) when pages of both kinds use it (a copy pasted across the conversion or its Undo, which
    /// <see cref="ForeignConversion"/> alone does not make); otherwise as its original.
    /// </summary>
    /// <remarks>
    /// The variant is asked for only when a page of the range shows it converted: building it can take long or fail (a
    /// large PDF), and a save of a document nobody converted must neither wait for it nor fail with it.
    /// </remarks>
    private static SourceUse Use(Func<ForeignVariant?> variantOf, int start, int count, DocumentModel model)
    {
        bool InRange(Page page, out int index)
        {
            index = page.Setup.Source.PdfPageIndex ?? -1;
            return page.Setup.Source.Kind == PageSourceKind.Pdf && index >= start && index < start + count;
        }
        if (!model.Pages.Any(p => p.Setup.Source.ForeignConverted && InRange(p, out _))) return SourceUse.Original;
        if (variantOf() is not { } variant) return SourceUse.Original;
        var removed = variant.Converted.Select(c => c.SourcePageIndex).ToHashSet();
        bool plain = model.Pages.Any(p => !p.Setup.Source.ForeignConverted && InRange(p, out int index) && removed.Contains(index - start));
        return plain ? SourceUse.Mixed : SourceUse.Variant;
    }

    /// <summary>
    /// What a save writes: the opened PDF's bytes (null for a new note), the model with imported source indices renumbered
    /// so imported PDFs no page uses are left out, and those used PDFs in order. A source whose pages show its converted
    /// foreign annotations (14.6) is written as its variant, without them. A source with pages of both kinds is written as
    /// its original, and the converted pages it shows are appended from the variant after the other sources, so every page
    /// is saved as it shows. Without imports or conversions the model is returned unchanged.
    /// </summary>
    public (byte[]? Main, DocumentModel Model, IReadOnlyList<ImportedPdf> Imported) ForSave(DocumentModel model)
    {
        int main = MainCount;
        var used = model.Pages.Select(p => p.Setup.Source.PdfPageIndex).OfType<int>().ToHashSet();
        var kept = new List<ImportedPdf>();
        var mixed = new List<(ForeignVariant Variant, IReadOnlyList<PdfPageGeometry> Pages, int Start, int Count)>();
        byte[]? mainBytes = null;
        if (Main is { } m)
        {
            var use = Use(() => m.Foreign, 0, main, model);
            mainBytes = use == SourceUse.Variant ? m.Foreign!.Bytes : m.Bytes;
            if (use == SourceUse.Mixed) mixed.Add((m.Foreign!, m.Pages, 0, main));
        }
        var shift = new Dictionary<int, int>(); // imported source index -> index in the written source
        int start = main, written = main;
        foreach (var part in imported)
        {
            if (Enumerable.Range(start, part.Pages.Count).Any(used.Contains))
            {
                var use = Use(() => part.Foreign, start, part.Pages.Count, model);
                kept.Add(use == SourceUse.Variant ? part.Converted() : part);
                if (use == SourceUse.Mixed) mixed.Add((part.Foreign!, part.Pages, start, part.Pages.Count));
                for (int i = 0; i < part.Pages.Count; i++) shift[start + i] = written + i;
                written += part.Pages.Count;
            }
            start += part.Pages.Count;
        }
        var appended = new Dictionary<int, int>(); // converted page of a mixed source -> its page among the appended ones
        foreach (var (variant, pages, from, count) in mixed)
        {
            var indices = model.Pages.Where(p => p.Setup.Source.ForeignConverted).Select(p => p.Setup.Source.PdfPageIndex).OfType<int>()
                .Where(i => i >= from && i < from + count).Distinct().Order().ToList();
            foreach (int index in indices) appended[index] = written++;
            kept.Add(VariantPages(variant, pages, [.. indices.Select(i => i - from)]));
        }
        bool renumbered = shift.Any(s => s.Key != s.Value) || appended.Count > 0;
        if (!renumbered && !model.Pages.Any(p => p.Setup.Source.ForeignConverted)) return (mainBytes, model, kept);
        // The written sources are the variants already: the model goes to the writer without the flags.
        var saved = model with
        {
            Pages = model.Pages.Select(p => p.Setup.Source is { Kind: PageSourceKind.Pdf, PdfPageIndex: int index } source
                ? p with { Setup = p.Setup with { Source = source with
                    {
                        PdfPageIndex = source.ForeignConverted && appended.TryGetValue(index, out int added) ? added : index >= main ? shift[index] : index,
                        ForeignConverted = false
                    } } }
                : p).ToImmutableList()
        };
        return (mainBytes, saved, kept);
    }

    /// <summary>
    /// Pages <paramref name="locals"/> of a variant as an import of their own, stripped of our app annotations and page
    /// keys like any imported page (spec 11.9). Only the save of a mixed source uses it, so it is built for that save.
    /// </summary>
    private static ImportedPdf VariantPages(ForeignVariant variant, IReadOnlyList<PdfPageGeometry> pages, IReadOnlyList<int> locals)
    {
        byte[] bytes;
        using (EditablePdfReader.OpenStripped(variant.Bytes, pages, out var stripped, saveBackground: true))
        using (var import = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(stripped.BackgroundPdf, writable: false), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        using (var subset = new PdfSharp.Pdf.PdfDocument())
        {
            foreach (int local in locals)
            {
                var page = Writing.PageContentWriter.AddImported(subset, import.Pages[local]);
                foreach (var key in page.Elements.Keys.Where(k => k.StartsWith("/PdfNotes", StringComparison.Ordinal)).ToArray())
                    page.Elements.Remove(key);
            }
            using var output = new MemoryStream();
            subset.Save(output, false);
            bytes = output.ToArray();
        }
        return new ImportedPdf(bytes, PdfPageGeometryReader.Read(bytes), variant.Engine, ownsEngine: false);
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
        // A source page shown both converted and not (a copy pasted across the conversion) is two exported pages.
        var sources = chosen.Where(p => p.Setup.Source.PdfPageIndex is not null)
            .Select(p => (Index: p.Setup.Source.PdfPageIndex!.Value, p.Setup.Source.ForeignConverted)).Distinct().ToList();
        var renumber = new Dictionary<(int, bool), int>();
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
                    foreach (var (index, converted) in sources)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var (_, sourceBytes, sourcePages, local) = Locate(index, converted);
                        if (!opened.TryGetValue(sourceBytes, out var import))
                        {
                            using (EditablePdfReader.OpenStripped(sourceBytes, sourcePages, out var stripped, saveBackground: true))
                                import = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(stripped.BackgroundPdf, writable: false), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                            opened[sourceBytes] = import;
                        }
                        var page = Writing.PageContentWriter.AddImported(subset, import.Pages[local]);
                        foreach (var key in page.Elements.Keys.Where(k => k.StartsWith("/PdfNotes", StringComparison.Ordinal)).ToArray())
                            page.Elements.Remove(key);
                        renumber[(index, converted)] = renumber.Count;
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
        var pages = chosen.Select(p => p.Setup.Source.PdfPageIndex is int index
            ? p with { Setup = p.Setup with { Source = PageSource.Pdf(renumber[(index, p.Setup.Source.ForeignConverted)]) } } : p).ToImmutableList();
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
