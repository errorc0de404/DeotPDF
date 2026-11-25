using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Reading;
using PdfSharp.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Pdf.Writing;

/// <summary>
/// Writes the editable PDF (spec 14.1–14.2): the source PDF without our previously restored annotations, pages in
/// model order (generated pages drawn as page content), one /Stamp annotation with a standard appearance per app
/// annotation, and the schema v1 metadata. The whole file is rewritten every time, so repeated saves never
/// accumulate annotations or resources (spec 14.4).
/// </summary>
/// <summary>A written editable PDF with the data it stores (for <see cref="EditablePdfWriter.VerifyQuick"/>).</summary>
/// <param name="ReusedBackgrounds">The page fingerprints came from an earlier save of the same pages.</param>
public sealed record WrittenPdf(byte[] Bytes, SerializedDocument Data, int PageCount, bool ReusedBackgrounds);

/// <summary>
/// The check a save hands to the file writers: what is read back must be the bytes written, and those
/// bytes are checked once (the file saver and an Android document each read back; an Android save also checks before).
/// </summary>
public sealed class WrittenCheck(byte[] content, Action<byte[]> check)
{
    private bool done;

    public void Verify(byte[] bytes)
    {
        if (!ReferenceEquals(bytes, content) && !bytes.AsSpan().SequenceEqual(content))
            throw new InvalidDataException("What was read back differs from what was written.");
        if (done) return;
        check(content);
        done = true;
    }
}

public static class EditablePdfWriter
{
    /// <summary>What a save stores as the original of a source, and its geometry (<see cref="Prepare"/>).</summary>
    private sealed record Prepared(byte[] Original, IReadOnlyList<PdfPageGeometry> Pages, IReadOnlyList<PdfPageGeometry>? Given);

    private static readonly ConditionalWeakTable<byte[], Prepared> PreparedSources = new();

    /// <summary>
    /// The background fingerprints of the last save from one original (save plan 2026-11-12): what the pages were and
    /// which had a text box layer. A save with the same pages writes these fingerprints at once instead of saving, reading
    /// the file back to compute them and saving again. The fingerprint is of the page without our annotations, so new or
    /// changed strokes do not change it; a page's text layer can (its content becomes an array), so that is part of the key.
    /// </summary>
    private sealed record BackgroundMemo(PageSetup[] Setups, bool[] TextLayers, string[] Hashes);

    private static readonly ConditionalWeakTable<object, BackgroundMemo> Backgrounds = new();

    /// <summary>The key of a document without a source PDF (a new note): its pages are generated only.</summary>
    private static readonly object NoSource = new();

    /// <summary>The last stored original with imported pages appended, per original (the append is a full PDF rewrite).</summary>
    private sealed record Appended(ImportedPdf[] Parts, byte[] Bytes);

    private static readonly ConditionalWeakTable<object, Appended> AppendedSources = new();

    /// <summary>Forgets the remembered background fingerprints (a written file did not verify with them).</summary>
    public static void ForgetBackgrounds() => Backgrounds.Clear();

    /// <summary>
    /// The original a save stores for <paramref name="sourcePdf"/>: the archived original of a file this app saved, or else
    /// the source without our app annotations and metadata. Prepared once per source array (plan C1): source bytes are
    /// never modified, so every save of an open document reuses it until a save gives the document new source bytes.
    /// </summary>
    private static Prepared Prepare(byte[] sourcePdf, IReadOnlyList<PdfPageGeometry>? sourcePages)
    {
        if (PreparedSources.TryGetValue(sourcePdf, out var cached) && ReferenceEquals(cached.Given, sourcePages)) return cached;
        byte[]? original;
        using (var input = PdfReader.Open(new MemoryStream(sourcePdf, writable: false), PdfDocumentOpenMode.Import))
            original = PageContentWriter.Original(input);
        if (original is not null && EditablePdfReader.Read(sourcePdf, PdfPageGeometryReader.Read(sourcePdf)).SourcePdf is null) original = null;
        Prepared result;
        if (original is not null) result = new(original, PdfPageGeometryReader.Read(original), sourcePages);
        else
        {
            var pages = sourcePages ?? PdfPageGeometryReader.Read(sourcePdf);
            using var stripped = EditablePdfReader.OpenStripped(sourcePdf, pages, out _, saveBackground: false);
            PageContentWriter.RemoveMetadata(stripped.Document);
            using var saved = new MemoryStream();
            stripped.Document.Save(saved, false);
            result = new(saved.ToArray(), pages, sourcePages);
        }
        PreparedSources.AddOrUpdate(sourcePdf, result);
        return result;
    }

    /// <param name="sourcePdf">Bytes of the opened PDF, or null for a new note (all pages generated).</param>
    /// <param name="sourcePages">Geometry measured when <paramref name="sourcePdf"/> was opened.</param>
    /// <param name="imported">
    /// Pages imported from other PDFs (spec 11.9), numbered after the source pages of <paramref name="sourcePdf"/>. They are
    /// appended to the one stored original, so the saved file has a single source like any other (no schema change).
    /// </param>
    public static byte[] Write(byte[]? sourcePdf, IReadOnlyList<PdfPageGeometry>? sourcePages, DocumentModel model,
        IReadOnlyList<ImportedPdf>? imported = null) => WritePdf(sourcePdf, sourcePages, model, imported).Bytes;

    /// <inheritdoc cref="Write(byte[], IReadOnlyList{PdfPageGeometry}, DocumentModel, IReadOnlyList{ImportedPdf})"/>
    public static WrittenPdf WritePdf(byte[]? sourcePdf, IReadOnlyList<PdfPageGeometry>? sourcePages, DocumentModel model,
        IReadOnlyList<ImportedPdf>? imported = null) => Build(sourcePdf, sourcePages, model, imported, null);

    /// <param name="knownHashes">The page fingerprints just computed for these pages (the second build of a first save).</param>
    private static WrittenPdf Build(byte[]? sourcePdf, IReadOnlyList<PdfPageGeometry>? sourcePages, DocumentModel model,
        IReadOnlyList<ImportedPdf>? imported, string[]? knownHashes)
    {
        bool reusedBackgrounds = false;
        imported ??= [];
        var given = (Source: sourcePdf, Pages: sourcePages, Imported: imported);
        byte[]? original = null;
        if (sourcePdf is not null)
        {
            var prepared = Prepare(sourcePdf, sourcePages);
            original = sourcePdf = prepared.Original;
            sourcePages = prepared.Pages;
        }
        model = model with { Pages = model.Pages.Select(p => p with { Annotations = p.Annotations.Where(a => a is not (TextBoxAnnotation { IsBlank: true } or StickyNote { IsBlank: true })).ToImmutableList() }).ToImmutableList() };
        var serialized = SchemaV9.Serialize(model);
        if (model.AllAnnotations.FirstOrDefault(a => !AnnotationAppearance.IsSupported(a.Kind)) is { } unsupported)
            throw new NotSupportedException($"{unsupported.Kind} annotations cannot be saved yet.");
        var envelopes = serialized.Annotations.ToDictionary(a => a.AnnotationId, a => a.Envelope);

        EditablePdfReader.OpenedDocument? opened = null;
        PdfDocument? copies = null;
        PdfDocument document;
        if (sourcePdf is null && imported.Count == 0)
        {
            if (model.Pages.Any(p => p.Setup.Source.Kind == PageSourceKind.Pdf)) throw new InvalidDataException("Pages refer to a source PDF that was not supplied.");
            document = new PdfDocument();
        }
        else
        {
            if (imported.Count > 0)
            {
                // Imported pages continue the source numbering: append them to the stored original (spec 11.9).
                var expected = (sourcePages ?? []).Concat(imported.SelectMany(part => part.Pages)).ToList();
                object appendKey = (object?)original ?? NoSource;
                if (AppendedSources.TryGetValue(appendKey, out var appended) && appended.Parts.SequenceEqual(imported, ReferenceEqualityComparer.Instance))
                    original = appended.Bytes;
                else
                {
                    original = PageContentWriter.Append(original, imported);
                    AppendedSources.AddOrUpdate(appendKey, new([.. imported], original));
                }
                sourcePdf = original;
                sourcePages = PdfPageGeometryReader.Read(original);
                if (sourcePages.Count != expected.Count || sourcePages.Zip(expected).Any(p => !SameGeometry(p.First, p.Second)))
                    throw new InvalidDataException("Imported pages changed while they were added to the source.");
            }
            opened = EditablePdfReader.OpenStripped(sourcePdf!, sourcePages!, out _, saveBackground: false);
            document = opened.Document;
            // A source page used more than once (page copy, spec 11.3) is imported again from the stripped source: the
            // copy carries the original content and foreign annotations, never our old app annotations.
            bool repeated = model.Pages.Where(p => p.Setup.Source.Kind == PageSourceKind.Pdf).GroupBy(p => p.Setup.Source.PdfPageIndex).Any(g => g.Count() > 1);
            if (repeated)
            {
                EditablePdfReader.OpenStripped(sourcePdf!, sourcePages!, out var stripped, saveBackground: true).Dispose();
                copies = PdfReader.Open(new MemoryStream(stripped.BackgroundPdf, writable: false), PdfDocumentOpenMode.Import);
            }
        }
        try
        {
            document.Options.CompressContentStreams = true;
            ArrangePages(document, model, copies);
            PdfNavigation.Prune(document); // outline items, links and named destinations of deleted pages (D13h)
            if (original is not null && model.Pages.Any(p => p.Setup.Source.Kind == PageSourceKind.Pdf)) PageContentWriter.StoreOriginal(document, original);
            var images = new Dictionary<Guid, PdfDictionary>();
            var resourceStreams = new PdfDictionary(document);
            foreach (var resource in serialized.Resources)
            {
                resourceStreams.Elements["/R" + resource.Id.ToString("N")] = Stream(document, resource.Payload.ToArray()).Reference!;
                using var imageBytes = new MemoryStream(resource.Payload.ToArray());
                using var raster = XImage.FromStream(imageBytes);
                // PDFsharp creates the standard image/soft-mask objects without adding temporary pages.
                var pdfImage = new PdfImage(document, raster);
                document.Internals.AddObject(pdfImage);
                images[resource.Id] = pdfImage;
            }
            document.Internals.Catalog.Elements["/PdfNotesResources"] = resourceStreams;
            for (int i = 0; i < model.Pages.Count; i++)
            {
                var page = model.Pages[i];
                var pdfPage = document.Pages[i];
                if (page.Setup.Source.PdfPageIndex is { } sourceIndex)
                    PageContentWriter.Apply(pdfPage, page.Setup, sourcePages![sourceIndex]);
                if (page.Setup.Rotation != PdfPageGeometryReader.Read(pdfPage, i).Rotation)
                    pdfPage.Elements.SetInteger("/Rotate", page.Setup.Rotation);
                var geometry = PdfPageGeometryReader.Read(pdfPage, i);
                if (Math.Abs(geometry.Box.WidthPt - page.Setup.WidthPt) > SchemaV1.PageSizeTolerancePt ||
                    Math.Abs(geometry.Box.HeightPt - page.Setup.HeightPt) > SchemaV1.PageSizeTolerancePt)
                    throw new InvalidDataException($"Page {i + 1} size differs from the PDF page.");
                if (page.Annotations.Count == 0) continue;
                var annots = pdfPage.Elements.GetArray("/Annots");
                if (annots is null)
                {
                    annots = new PdfArray(document);
                    pdfPage.Elements["/Annots"] = annots;
                }
                foreach (var annotation in page.Annotations)
                {
                    var saved = CreateAnnotation(document, geometry, annotation, images, envelopes[annotation.Id]);
                    annots.Elements.Add(saved.Reference!);
                    if (saved.Elements.GetDictionary("/Popup") is { } popup) annots.Elements.Add(popup.Reference!); // a note's popup (24.9)
                    if (annotation is TextBoxAnnotation text) SearchableText.Add(document, pdfPage, geometry.Box, [text], text.Id, saved);
                    else if (annotation is TableAnnotation table) SearchableText.Add(document, pdfPage, geometry.Box, SearchableText.Cells(table), table.Id, saved);
                }
            }
            document.Internals.Catalog.Elements[EditablePdfKeys.Header] = Stream(document, serialized.Header).Reference!;
            object backgroundKey = (object?)original ?? NoSource;
            var setups = model.Pages.Select(p => p.Setup).ToArray();
            var textLayers = model.Pages.Select(p => p.Annotations.Any(a => a is TextBoxAnnotation or TableAnnotation)).ToArray();
            string[]? known = knownHashes;
            if (known is null && Backgrounds.TryGetValue(backgroundKey, out var memo) && memo.Setups.SequenceEqual(setups) && memo.TextLayers.SequenceEqual(textLayers))
            {
                known = memo.Hashes;
                reusedBackgrounds = true;
            }
            if (known is not null)
            {
                // The same pages as the last save: their fingerprints are known, one save is enough.
                for (int i = 0; i < known.Length; i++) document.Pages[i].Elements.SetString(PageContentWriter.BackgroundHashKey, known[i]);
                using var once = new MemoryStream();
                document.Save(once, false);
                return new(once.ToArray(), serialized, model.Pages.Count, reusedBackgrounds);
            }
            using var buffer = new MemoryStream();
            document.Save(buffer, false);
            var bytes = buffer.ToArray();
            // PDFsharp adds standard resource defaults during serialization. Fingerprint that final physical
            // representation with our appearances removed, then attach the fingerprints without changing content.
            string[] hashes;
            using (var background = EditablePdfReader.OpenStripped(bytes, PdfPageGeometryReader.Read(bytes), out _, saveBackground: false))
            {
                var streams = SearchableText.NewStreamHashes(); // fonts and images the pages share: hashed once
                hashes = background.Document.Pages.Cast<PdfPage>().Select(p => PageContentWriter.BackgroundHash(p, streams: streams)).ToArray();
            }
            Backgrounds.AddOrUpdate(backgroundKey, new(setups, textLayers, hashes));
            // Built again with the fingerprints now known: the same bytes every later save of these pages writes (a document
            // is saved only once, so this one cannot just take them).
            return Build(given.Source, given.Pages, model, given.Imported, hashes);
        }
        finally
        {
            copies?.Dispose();
            if (opened is not null) opened.Dispose();
            else document.Dispose();
        }
    }

    /// <summary>Where two envelopes part, as a short JSON excerpt of each (for the verification error).</summary>
    private static string FirstDifference(byte[] read, byte[] wanted)
    {
        string a = System.Text.Encoding.UTF8.GetString(read), b = System.Text.Encoding.UTF8.GetString(wanted);
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        int from = Math.Max(0, i - 40);
        string Cut(string s) => s.Substring(Math.Min(from, s.Length), Math.Min(120, Math.Max(0, s.Length - from)));
        return $"read …{Cut(a)}… expected …{Cut(b)}…";
    }

    /// <summary>
    /// Re-reads a written file and requires it to restore exactly <paramref name="expected"/>: the renderer opens it,
    /// the header is valid, every app annotation is restored and the metadata is byte-identical.
    /// </summary>
    public static void Verify(byte[] written, DocumentModel expected)
    {
        using (var engine = new PdfiumDocument(written))
            if (engine.PageCount != expected.Pages.Count) throw new InvalidDataException("The written PDF has a different page count.");
        var read = EditablePdfReader.Read(written, PdfPageGeometryReader.Read(written));
        if (read.HeaderStatus != HeaderStatus.Valid)
            throw new InvalidDataException("The written PDF does not restore all annotations.");
        var a = SchemaV9.Serialize(read.Model);
        if (read.ReadOnlyCount != 0)
            throw new InvalidDataException($"The written PDF does not restore the saved document ({read.ReadOnlyCount} read-only).");
        expected = expected with { Pages = expected.Pages.Select(p => p with { Annotations = p.Annotations.Where(a => a is not (TextBoxAnnotation { IsBlank: true } or StickyNote { IsBlank: true })).ToImmutableList() }).ToImmutableList() };
        var b = SchemaV9.Serialize(expected);
        if (!a.Header.AsSpan().SequenceEqual(b.Header))
            throw new InvalidDataException($"The written PDF does not restore the saved document (header: {FirstDifference(a.Header, b.Header)}).");
        if (a.Annotations.Length != b.Annotations.Length)
            throw new InvalidDataException($"The written PDF does not restore the saved document ({a.Annotations.Length} of {b.Annotations.Length} annotations).");
        foreach (var (got, wanted) in a.Annotations.Zip(b.Annotations))
            if (got.AnnotationId != wanted.AnnotationId || !got.Envelope.AsSpan().SequenceEqual(wanted.Envelope))
                throw new InvalidDataException($"The written PDF does not restore the saved document (annotation {wanted.AnnotationId}: {FirstDifference(got.Envelope, wanted.Envelope)}).");
    }

    /// <summary>
    /// The quick check of a written file (save plan 2026-11-12), for automatic backups and saves: the renderer opens it
    /// with the right page count, the stored header and every annotation's data are the bytes that were written, nothing
    /// of ours is missing or extra, and each page's background fingerprint holds once our annotations are taken off (so
    /// the next open keeps the stored original). It does not rebuild the model from the file or recompute every
    /// appearance fingerprint as <see cref="Verify"/> does; a manual save still runs that.
    /// </summary>
    public static void VerifyQuick(byte[] written, WrittenPdf expected)
    {
        using (var engine = new PdfiumDocument(written))
            if (engine.PageCount != expected.PageCount) throw new InvalidDataException("The written PDF has a different page count.");
        using var stream = new MemoryStream(written, writable: false);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
        if (document.PageCount != expected.PageCount) throw new InvalidDataException("The written PDF has a different page count.");
        var header = document.Internals.Catalog.Elements.GetDictionary(EditablePdfKeys.Header)?.Stream?.UnfilteredValue;
        if (header is null || !header.AsSpan().SequenceEqual(expected.Data.Header)) throw new InvalidDataException("The written PDF does not hold the saved header.");
        var wanted = expected.Data.Annotations.ToDictionary(a => a.AnnotationId, a => a.Envelope);
        int found = 0;
        for (int p = 0; p < document.PageCount; p++)
        {
            var page = document.Pages[p];
            if (page.Elements.GetArray("/Annots") is not { } annots) continue;
            var removed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            for (int i = annots.Elements.Count - 1; i >= 0; i--)
            {
                if (EditablePdfReader.Resolve(annots.Elements[i]) is not { } annot) continue;
                string name = annot.Elements.GetString("/NM") ?? "";
                if (!name.StartsWith(EditablePdfKeys.NamePrefix, StringComparison.Ordinal)) continue;
                if (!Guid.TryParseExact(name.AsSpan(EditablePdfKeys.NamePrefix.Length), "D", out var id) || !wanted.TryGetValue(id, out var envelope) ||
                    annot.Elements.GetDictionary(EditablePdfKeys.Envelope)?.Stream?.UnfilteredValue is not { } data || !data.AsSpan().SequenceEqual(envelope))
                    throw new InvalidDataException($"The written PDF does not hold annotation {name} as saved.");
                found++;
                SearchableText.Remove(page, annot);
                removed.Add(annot);
                annots.Elements.RemoveAt(i);
            }
            for (int i = annots.Elements.Count - 1; i >= 0; i--)
                if (EditablePdfReader.Resolve(annots.Elements[i]) is { } a && a.Elements.GetName("/Subtype") == "/Popup" &&
                    a.Elements.GetDictionary("/Parent") is { } parent && removed.Contains(parent))
                    annots.Elements.RemoveAt(i);
        }
        if (found != wanted.Count) throw new InvalidDataException($"The written PDF holds {found} of {wanted.Count} annotations.");
        if (!PageContentWriter.BackgroundVerified(document)) throw new InvalidDataException("The written PDF's page fingerprints do not match.");
    }

    /// <summary>
    /// Puts the document's pages in model order: the first use of a source page moves the original, later uses insert
    /// an imported copy from <paramref name="copies"/> (the stripped source opened for import), generated pages are
    /// inserted where the model has them and unused source pages are removed.
    /// </summary>
    private static void ArrangePages(PdfDocument document, DocumentModel model, PdfDocument? copies)
    {
        var originals = Enumerable.Range(0, document.PageCount).Select(i => document.Pages[i]).ToList();
        var used = new HashSet<int>();
        foreach (var page in model.Pages)
            if (page.Setup.Source is { Kind: PageSourceKind.Pdf, PdfPageIndex: int index })
            {
                if (index >= originals.Count) throw new InvalidDataException("Source page index is out of range.");
                if (!used.Add(index) && copies is null) throw new InvalidDataException("A repeated source page needs an import copy.");
            }
        for (int i = originals.Count - 1; i >= 0; i--)
            if (!used.Contains(i)) document.Pages.RemoveAt(i);
        var placed = new HashSet<int>();
        for (int target = 0; target < model.Pages.Count; target++)
        {
            var setup = model.Pages[target].Setup;
            if (setup.Source.Kind == PageSourceKind.Generated)
            {
                GeneratedPageWriter.InsertPage(document, target, setup);
                continue;
            }
            int index = setup.Source.PdfPageIndex!.Value;
            if (!placed.Add(index))
            {
                PageContentWriter.AddImported(document, copies!.Pages[index], target);
                continue;
            }
            var wanted = originals[index];
            int current = IndexOf(document, wanted);
            if (current != target) document.Pages.MovePage(current, target);
        }
        if (document.PageCount != model.Pages.Count) throw new InvalidDataException("Page arrangement failed.");
    }

    private static bool SameGeometry(PdfPageGeometry a, PdfPageGeometry b) =>
        a.Rotation == b.Rotation && Math.Abs(a.Box.Left - b.Box.Left) <= 1e-6 && Math.Abs(a.Box.Bottom - b.Box.Bottom) <= 1e-6 &&
        Math.Abs(a.Box.WidthPt - b.Box.WidthPt) <= 1e-6 && Math.Abs(a.Box.HeightPt - b.Box.HeightPt) <= 1e-6;

    private static int IndexOf(PdfDocument document, PdfPage page)
    {
        for (int i = 0; i < document.PageCount; i++)
            if (ReferenceEquals(document.Pages[i], page) || document.Pages[i].Reference == page.Reference && page.Reference is not null) return i;
        throw new InvalidDataException("Source page is missing.");
    }

    private static PdfDictionary CreateAnnotation(PdfDocument document, PdfPageGeometry geometry, Annotation annotation, IReadOnlyDictionary<Guid, PdfDictionary> images, byte[] envelope)
    {
        var form = AnnotationAppearance.Create(document, geometry.Box, annotation, images, out var rect, out string hash);
        var appearance = new PdfDictionary(document);
        appearance.Elements["/N"] = form.Reference!;
        var annot = new PdfDictionary(document);
        annot.Elements.SetName("/Type", "/Annot");
        annot.Elements.SetName("/Subtype", AnnotationAppearance.Subtype(annotation));
        annot.Elements.SetInteger("/F", EditablePdfKeys.PdfFlagPrint); // printed; no NoZoom/NoRotate: scales and turns with the page
        annot.Elements.SetString("/NM", EditablePdfKeys.NamePrefix + annotation.Id.ToString("D"));
        annot.Elements["/Rect"] = AnnotationAppearance.Array(document, rect);
        annot.Elements["/AP"] = appearance;
        annot.Elements[EditablePdfKeys.Envelope] = Stream(document, envelope).Reference!;
        annot.Elements.SetString(EditablePdfKeys.AppearanceHash, hash);
        document.Internals.AddObject(annot);
        StandardFields(document, geometry, annotation, annot, rect);
        return annot;
    }

    /// <summary>
    /// What other viewers read from the standard types (spec 24.8–24.9): /QuadPoints, /C and /CA of a markup; /Contents, /C,
    /// /Name and a closed /Popup of a note. Our appearance stays the one drawn.
    /// </summary>
    private static void StandardFields(PdfDocument document, PdfPageGeometry geometry, Annotation annotation, PdfDictionary annot, double[] rect)
    {
        static PdfArray Rgb(PdfDocument d, RgbColor c) => new(d, AnnotationAppearance.Real(c.R / 255d), AnnotationAppearance.Real(c.G / 255d), AnnotationAppearance.Real(c.B / 255d));
        switch (annotation)
        {
            case PdfTextMarkup markup:
            {
                var toUser = geometry.Box.CanonicalToPdfUser;
                var points = new PdfArray(document);
                foreach (var q in markup.Quads)
                    foreach (var p in new[] { q.UpperLeft, q.UpperRight, q.LowerLeft, q.LowerRight }.Select(toUser.Transform))
                    { points.Elements.Add(AnnotationAppearance.Real(p.X)); points.Elements.Add(AnnotationAppearance.Real(p.Y)); }
                annot.Elements["/QuadPoints"] = points;
                annot.Elements["/C"] = Rgb(document, markup.Color);
                annot.Elements["/CA"] = AnnotationAppearance.Real(markup.Opacity);
                if (markup.SourceTextHint is { Length: > 0 } hint) annot.Elements["/Contents"] = new PdfString(hint, PdfStringEncoding.Unicode);
                break;
            }
            case StickyNote note:
            {
                annot.Elements["/Contents"] = new PdfString(note.Text, PdfStringEncoding.Unicode); // UTF-16: Korean and every other script
                annot.Elements["/C"] = Rgb(document, note.Color);
                annot.Elements.SetName("/Name", "/Comment");
                annot.Elements.SetBoolean("/Open", false);
                var popup = new PdfDictionary(document);
                popup.Elements.SetName("/Type", "/Annot");
                popup.Elements.SetName("/Subtype", "/Popup");
                popup.Elements["/Rect"] = AnnotationAppearance.Array(document, [rect[2], rect[1] - 120, rect[2] + 220, rect[1]]);
                popup.Elements.SetBoolean("/Open", false);
                document.Internals.AddObject(popup);
                popup.Elements["/Parent"] = annot.Reference!;
                annot.Elements["/Popup"] = popup.Reference!;
                break;
            }
        }
    }

    /// <summary>Compressed app data stream. Data only: never content, scripts or actions (spec 14.4).</summary>
    private static PdfDictionary Stream(PdfDocument document, byte[] data)
    {
        var dictionary = new PdfDictionary(document);
        dictionary.CreateStream(data);
        dictionary.Stream.Zip();
        document.Internals.AddObject(dictionary);
        return dictionary;
    }
}
