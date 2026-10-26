using System.Collections.Immutable;
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
public static class EditablePdfWriter
{
    /// <param name="sourcePdf">Bytes of the opened PDF, or null for a new note (all pages generated).</param>
    /// <param name="sourcePages">Geometry measured when <paramref name="sourcePdf"/> was opened.</param>
    /// <param name="imported">
    /// Pages imported from other PDFs (spec 11.9), numbered after the source pages of <paramref name="sourcePdf"/>. They are
    /// appended to the one stored original, so the saved file has a single source like any other (no schema change).
    /// </param>
    public static byte[] Write(byte[]? sourcePdf, IReadOnlyList<PdfPageGeometry>? sourcePages, DocumentModel model,
        IReadOnlyList<ImportedPdf>? imported = null)
    {
        imported ??= [];
        byte[]? original = null;
        if (sourcePdf is not null)
        {
            using var input = PdfReader.Open(new MemoryStream(sourcePdf, writable: false), PdfDocumentOpenMode.Import);
            original = PageContentWriter.Original(input);
            if (original is not null && EditablePdfReader.Read(sourcePdf, PdfPageGeometryReader.Read(sourcePdf)).SourcePdf is null) original = null;
            if (original is not null) { sourcePdf = original; sourcePages = PdfPageGeometryReader.Read(original); }
        }
        model = model with { Pages = model.Pages.Select(p => p with { Annotations = p.Annotations.Where(a => a is not (TextBoxAnnotation { IsBlank: true } or StickyNote { IsBlank: true })).ToImmutableList() }).ToImmutableList() };
        var serialized = SchemaV7.Serialize(model);
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
            if (sourcePdf is not null)
            {
                sourcePages ??= PdfPageGeometryReader.Read(sourcePdf);
                if (original is null)
                {
                    using var stripped = EditablePdfReader.OpenStripped(sourcePdf, sourcePages, out _, saveBackground: false);
                    PageContentWriter.RemoveMetadata(stripped.Document);
                    using var savedSource = new MemoryStream(); stripped.Document.Save(savedSource, false); original = savedSource.ToArray();
                    sourcePdf = original;
                }
            }
            if (imported.Count > 0)
            {
                // Imported pages continue the source numbering: append them to the stored original (spec 11.9).
                var expected = (sourcePages ?? []).Concat(imported.SelectMany(part => part.Pages)).ToList();
                original = sourcePdf = PageContentWriter.Append(original, imported);
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
                    if (annotation is TextBoxAnnotation text) SearchableText.Add(document, pdfPage, geometry.Box, text, saved);
                }
            }
            document.Internals.Catalog.Elements[EditablePdfKeys.Header] = Stream(document, serialized.Header).Reference!;
            using var buffer = new MemoryStream();
            document.Save(buffer, false);
            var bytes = buffer.ToArray();
            // PDFsharp adds standard resource defaults during serialization. Fingerprint that final physical
            // representation with our appearances removed, then attach the fingerprints without changing content.
            using var background = EditablePdfReader.OpenStripped(bytes, PdfPageGeometryReader.Read(bytes), out _, saveBackground: false);
            var hashes = background.Document.Pages.Cast<PdfPage>().Select(p => PageContentWriter.BackgroundHash(p)).ToArray();
            using var final = PdfReader.Open(new MemoryStream(bytes, false), PdfDocumentOpenMode.Modify);
            for (int i = 0; i < hashes.Length; i++) final.Pages[i].Elements.SetString(PageContentWriter.BackgroundHashKey, hashes[i]);
            using var output = new MemoryStream(); final.Save(output, false); return output.ToArray();
        }
        finally
        {
            copies?.Dispose();
            if (opened is not null) opened.Dispose();
            else document.Dispose();
        }
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
        var a = SchemaV7.Serialize(read.Model);
        expected = expected with { Pages = expected.Pages.Select(p => p with { Annotations = p.Annotations.Where(a => a is not (TextBoxAnnotation { IsBlank: true } or StickyNote { IsBlank: true })).ToImmutableList() }).ToImmutableList() };
        var b = SchemaV7.Serialize(expected);
        if (!a.Header.AsSpan().SequenceEqual(b.Header) || a.Annotations.Length != b.Annotations.Length ||
            a.Annotations.Zip(b.Annotations).Any(p => p.First.AnnotationId != p.Second.AnnotationId || !p.First.Envelope.AsSpan().SequenceEqual(p.Second.Envelope)))
            throw new InvalidDataException("The written PDF does not restore the saved document.");
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
