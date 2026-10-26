using System.Collections.Immutable;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Writing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Pdf.Reading;

/// <summary>
/// Result of reading app data from a PDF. <see cref="Model"/> is always usable: the restored document when the
/// header is valid, otherwise a fresh model over the PDF pages. <see cref="ReadOnlyCount"/> counts app-marked
/// annotations that could not be verified; their appearance stays in <see cref="BackgroundPdf"/> (spec 14.3).
/// </summary>
public sealed record EditablePdfReadResult(HeaderStatus HeaderStatus, DocumentModel Model, int RestoredCount, int ReadOnlyCount,
    ImmutableArray<string> Warnings, byte[] BackgroundPdf)
{
    /// <summary>Untransformed stripped source retained by v4. Null for ordinary/legacy PDFs and fallback.</summary>
    public byte[]? SourcePdf { get; init; }
    /// <summary>True when the PDF had no usable app data and the model was built fresh (new page IDs on every open).</summary>
    public bool FreshModel { get; init; }
    /// <summary>True when the user should be told that some app annotations are shown read-only.</summary>
    public bool HasReadOnlyAppData => ReadOnlyCount > 0;
}

/// <summary>
/// Reads schema v1 app data stored by <see cref="EditablePdfWriter"/>. Only annotations whose envelope, name,
/// physical page, kind and appearance fingerprint all check out are restored and removed from the background;
/// everything else keeps its appearance so nothing disappears (spec 14.3).
/// </summary>
public static class EditablePdfReader
{
    private sealed record Candidate(int PageIndex, int AnnotIndex, Guid? NameId, byte[]? Envelope, bool Verified);

    /// <summary>A PDFsharp document opened for modification together with the stream it reads from.</summary>
    internal sealed class OpenedDocument(PdfDocument document, Stream stream) : IDisposable
    {
        public PdfDocument Document { get; } = document;
        public void Dispose() { Document.Dispose(); stream.Dispose(); }
    }

    public static EditablePdfReadResult Read(byte[] pdf, IReadOnlyList<PdfPageGeometry> pages)
    {
        using var opened = OpenStripped(pdf, pages, out var result, saveBackground: true);
        return result;
    }

    /// <summary>
    /// Opens <paramref name="pdf"/> for modification with the restored (and blank) own annotations already removed from
    /// /Annots. With <paramref name="saveBackground"/> the stripped bytes are produced for rendering.
    /// </summary>
    internal static OpenedDocument OpenStripped(byte[] pdf, IReadOnlyList<PdfPageGeometry> pages, out EditablePdfReadResult result,
        bool saveBackground)
    {
        var stream = new MemoryStream(pdf, writable: false);
        PdfDocument document;
        try { document = PdfReader.Open(stream, PdfDocumentOpenMode.Modify); }
        catch { stream.Dispose(); throw; }
        var opened = new OpenedDocument(document, stream);
        try
        {
            if (document.PageCount != pages.Count) throw new InvalidDataException("Page count differs from the measured geometry.");
            var candidates = FindCandidates(document);
            byte[]? header = ReadStream(document.Internals.Catalog.Elements.GetDictionary(EditablePdfKeys.Header), SchemaV1.MaxHeaderBytes);
            var frames = pages.Select(p => p.Frame).ToArray();
            var included = candidates.Where(c => c.Verified).ToList();
            DocumentReadResult read;
            var fontWarnings = new HashSet<string>();
            while (true)
            {
                read = SchemaV7.Read(header, included.Select(c => c.Envelope!).ToList(), id =>
                    ReadImageResource(document.Internals.Catalog.Elements.GetDictionary("/PdfNotesResources")?.Elements.GetDictionary("/R" + id.ToString("N"))), frames);
                if (read.Model is null) break;
                var mismatched = included.Where((c, k) =>
                {
                    if (read.Annotations[k].AnnotationId is { } id && read.Model.FindAnnotation(id)?.Annotation is TextBoxAnnotation t &&
                        t.Runs.Any(r => !PdfNotes.Text.TextLayout.FontAvailable(r.FontFamily)))
                    { fontWarnings.Add("Missing font: original text appearance is preserved read-only; editing requires the original font."); return true; }
                    return !Consistent(c, read.Annotations[k], read.Model);
                }).ToList();
                if (mismatched.Count == 0) break;
                // Physical placement or kind does not match the data: keep those appearances visible and read again.
                included.RemoveAll(mismatched.Contains);
            }

            byte[]? original = null;
            bool v4 = false; // v4 and later keep the original source archive and page placement
            try { v4 = header is not null && System.Text.Json.Nodes.JsonNode.Parse(header)?["schemaVersion"]?.GetValue<int>() is SchemaV4.Version or SchemaV5.Version or SchemaV6.Version or SchemaV7.Version; }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
            if (v4 && read.Model is { } restoredModel)
            {
                original = PageContentWriter.Original(document);
                bool needsSource = restoredModel.Pages.Any(p => p.Setup.Source.Kind == PageSourceKind.Pdf);
                bool usable = !needsSource || original is not null;
                if (usable && original is not null)
                {
                    var sourceFrames = PdfPageGeometryReader.Read(original);
                    usable = restoredModel.Pages.All(p => p.Setup.Source.Kind == PageSourceKind.Generated ||
                        p.Setup.Source.PdfPageIndex is { } n && n < sourceFrames.Length &&
                        Math.Abs((p.Setup.Content?.WidthPt ?? p.Setup.WidthPt) - sourceFrames[n].Box.WidthPt) < .001 &&
                        Math.Abs((p.Setup.Content?.HeightPt ?? p.Setup.HeightPt) - sourceFrames[n].Box.HeightPt) < .001);
                }
                // Partially restored v4 files cannot use the archived source: it would hide the rejected appearances.
                if (!usable)
                { read = read with { HeaderStatus = HeaderStatus.Invalid, Model = null, Warnings = read.Warnings.Add("Source placement cannot be verified; the PDF stays read-only.") }; original = null; }
                else if (included.Count != candidates.Count || read.Annotations.Any(a => a.Status == AnnotationReadStatus.Rejected))
                {
                    // Use the physical PDF as background so rejected/foreign appearances survive. Its placement is already baked in.
                    read = read with { Model = restoredModel with { Pages = restoredModel.Pages.Select((p, i) => p with
                        { Setup = p.Setup with { Source = PageSource.Pdf(i), Content = null } }).ToImmutableList() } };
                    original = null;
                }
            }
            var hidden = read.Model is null ? [] : included.Where((_, k) => read.Annotations[k].Status != AnnotationReadStatus.Rejected).ToList();
            foreach (var group in hidden.GroupBy(c => c.PageIndex))
            {
                var annots = document.Pages[group.Key].Elements.GetArray("/Annots")!;
                var removed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                foreach (var c in group.OrderByDescending(c => c.AnnotIndex))
                {
                    var own = Resolve(annots.Elements[c.AnnotIndex])!;
                    SearchableText.Remove(document.Pages[group.Key], own);
                    removed.Add(own);
                    annots.Elements.RemoveAt(c.AnnotIndex);
                }
                // A note's popup goes with it (spec 24.9); otherwise every save would add another one.
                for (int i = annots.Elements.Count - 1; i >= 0; i--)
                    if (Resolve(annots.Elements[i]) is { } a && a.Elements.GetName("/Subtype") == "/Popup" && a.Elements.GetDictionary("/Parent") is { } parent && removed.Contains(parent))
                        annots.Elements.RemoveAt(i);
            }
            if (v4 && read.Model is { } verifiedModel && original is not null && !PageContentWriter.BackgroundVerified(document))
            {
                // An external editor changed the visible background. Preserve its physical data instead of replacing it with the archived source.
                read = read with { Model = verifiedModel with { Pages = verifiedModel.Pages.Select((p, i) => p with
                    { Setup = p.Setup with { Source = PageSource.Pdf(i), Content = null } }).ToImmutableList() },
                    Warnings = read.Warnings.Add("The PDF background changed externally; the visible PDF is retained as the new source.") };
                original = null;
            }
            var model = read.Model ?? DocumentModel.Create(pages.Select(p =>
                new PageSetup(p.Box.WidthPt, p.Box.HeightPt, p.Rotation, PageSource.Pdf(p.PageIndex), PageBackground.Blank)));
            byte[] background = pdf;
            if (saveBackground && hidden.Count > 0)
            {
                using var buffer = new MemoryStream();
                document.Save(buffer, false);
                background = buffer.ToArray();
            }
            int restored = read.Annotations.Count(a => a.Status == AnnotationReadStatus.Restored);
            result = new(read.HeaderStatus, model, read.Model is null ? 0 : restored, candidates.Count - hidden.Count, read.Warnings.AddRange(fontWarnings), background) { SourcePdf = original, FreshModel = read.Model is null };
            return opened;
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    private static bool Consistent(Candidate candidate, AnnotationReadResult status, DocumentModel model)
    {
        if (status.Status == AnnotationReadStatus.Rejected) return true;
        if (status.AnnotationId != candidate.NameId) return false;
        if (status.Status == AnnotationReadStatus.DiscardedEmpty) return true;
        var location = model.FindAnnotation(status.AnnotationId!.Value);
        return location is not null && location.PageIndex == candidate.PageIndex && AnnotationAppearance.IsSupported(location.Annotation.Kind);
    }

    /// <summary>Every annotation that carries our name prefix or envelope key, with the PDF-level checks done.</summary>
    private static List<Candidate> FindCandidates(PdfDocument document)
    {
        var result = new List<Candidate>();
        for (int p = 0; p < document.PageCount; p++)
        {
            var annots = document.Pages[p].Elements.GetArray("/Annots");
            if (annots is null) continue;
            for (int i = 0; i < annots.Elements.Count; i++)
            {
                if (Resolve(annots.Elements[i]) is not { } annot) continue;
                string name = annot.Elements.GetString("/NM") ?? "";
                bool marked = name.StartsWith(EditablePdfKeys.NamePrefix, StringComparison.Ordinal) || annot.Elements.ContainsKey(EditablePdfKeys.Envelope);
                if (!marked) continue;
                Guid? nameId = name.StartsWith(EditablePdfKeys.NamePrefix, StringComparison.Ordinal) &&
                    Guid.TryParseExact(name.AsSpan(EditablePdfKeys.NamePrefix.Length), "D", out var id) ? id : null;
                byte[]? envelope = ReadStream(annot.Elements.GetDictionary(EditablePdfKeys.Envelope), SchemaV1.MaxEnvelopeBytes);
                var form = annot.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
                bool verified = false;
                try
                {
                    verified = nameId is not null && envelope is not null && AnnotationAppearance.IsAppSubtype(annot.Elements.GetName("/Subtype")) &&
                        form?.Stream is not null && annot.Elements.GetString(EditablePdfKeys.AppearanceHash) == AnnotationAppearance.Hash(form) &&
                        SearchableText.Verified(document.Pages[p], annot, nameId.Value);
                }
                catch (Exception e) when (e is InvalidDataException or InvalidOperationException or InvalidCastException or ArgumentException or NotSupportedException) { }
                result.Add(new(p, i, nameId, envelope, verified));
            }
        }
        return result;
    }

    private static byte[]? ReadStream(PdfDictionary? dictionary, int limit)
    {
        try
        {
            if (dictionary?.Stream is null || dictionary.Stream.Length > limit) return null;
            var bytes = dictionary.Stream.UnfilteredValue;
            return bytes.Length <= limit ? bytes : null;
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static byte[]? ReadImageResource(PdfDictionary? dictionary)
    {
        var bytes = ReadStream(dictionary, (int)ModelLimits.MaxResourceBytes);
        if (bytes is null) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            using var image = PdfSharp.Drawing.XImage.FromStream(stream);
            return image.PixelWidth > 0 && image.PixelHeight > 0 && (long)image.PixelWidth * image.PixelHeight <= 64_000_000 ? bytes : null;
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or ArgumentException or NotSupportedException or IndexOutOfRangeException)
        { return null; }
    }

    internal static PdfDictionary? Resolve(PdfItem item) => item is PdfReference r ? r.Value as PdfDictionary : item as PdfDictionary;
}
