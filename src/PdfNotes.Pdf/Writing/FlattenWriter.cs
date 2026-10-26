using System.Globalization;
using System.Text;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Reading;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Pdf.Writing;

/// <summary>Result of a Flatten export. <see cref="KeptForeign"/> visible annotations of other apps stay annotations.</summary>
public sealed record FlattenResult(byte[] Pdf, int MergedApp, int MergedForeign, int KeptForeign);

/// <summary>
/// Flatten PDF export (spec 14.5). The editable PDF of the snapshot is written first (the same physical pages, page
/// placement and appearances as a normal save), then every app annotation and every visible annotation of another app
/// whose normal appearance can be placed is drawn into the page content in /Annots order and removed. All app data is
/// removed: header, envelopes, image originals, the retained source, fingerprints and markers. Text box Unicode layers
/// stay in the content so the text remains selectable and searchable. Source text and vectors are kept as they are.
/// </summary>
public static class FlattenWriter
{
    private const int Hidden = 2, NoZoom = 8, NoRotate = 16, NoView = 32;

    public static FlattenResult Write(byte[]? sourcePdf, IReadOnlyList<PdfPageGeometry>? sourcePages, DocumentModel model,
        IReadOnlyList<ImportedPdf>? imported = null) =>
        Flatten(EditablePdfWriter.Write(sourcePdf, sourcePages, model, imported));

    /// <summary>Merges the annotations of an editable PDF written by <see cref="EditablePdfWriter"/> into its pages.</summary>
    public static FlattenResult Flatten(byte[] editable)
    {
        using var document = PdfReader.Open(new MemoryStream(editable, writable: false), PdfDocumentOpenMode.Modify);
        document.Options.CompressContentStreams = true;
        int app = 0, foreign = 0, kept = 0, notes = 0;
        foreach (PdfPage page in document.Pages)
        {
            var annots = page.Elements.GetArray("/Annots");
            if (annots is null) continue;
            var merged = new List<PdfDictionary>();
            var drawing = new StringBuilder();
            var objects = XObjects(page);
            foreach (var item in annots.Elements.ToArray())
            {
                if (EditablePdfReader.Resolve(item) is not { } annotation) continue;
                bool own = IsOwn(annotation);
                string subtype = annotation.Elements.GetName("/Subtype");
                if (own && subtype == "/Text")
                {
                    // A note stays a standard note so its text is not lost (spec 24.9); only our data leaves it.
                    foreach (var key in annotation.Elements.Keys.Where(IsAppKey).ToArray()) annotation.Elements.Remove(key);
                    annotation.Elements.SetString("/NM", Guid.NewGuid().ToString("D"));
                    notes++;
                    continue;
                }
                int flags = annotation.Elements.GetInteger("/F");
                if (!own && (subtype is "/Popup" or "/Link" || (flags & (Hidden | NoView)) != 0)) continue; // not drawn content: kept as is
                if (!own && (subtype == "/Widget" || (flags & (NoZoom | NoRotate)) != 0 || annotation.Elements.ContainsKey("/OC")) || Placement(annotation) is not { } place)
                {
                    if (own) throw new InvalidDataException("An app annotation has no appearance to flatten.");
                    kept++;
                    continue;
                }
                string name = Unique(objects, own ? "/PNA" : "/PNF");
                objects.Elements[name] = place.Form.Reference is { } reference ? reference : place.Form;
                string state = "";
                if (!own && Opacity(annotation) is { } alpha)
                {
                    // Viewers composite an appearance with the annotation's /CA as one group (ISO 32000-1 12.5.2): keep that look.
                    name = Group(page, objects, name, place.Form);
                    var states = page.Resources.Elements.GetDictionary("/ExtGState");
                    if (states is null) { states = new PdfDictionary(page.Owner); page.Resources.Elements["/ExtGState"] = states; }
                    state = Unique(states, "/PNG");
                    var gs = new PdfDictionary(page.Owner);
                    gs.Elements.SetName("/Type", "/ExtGState");
                    gs.Elements["/CA"] = AnnotationAppearance.Real(alpha); gs.Elements["/ca"] = AnnotationAppearance.Real(alpha);
                    states.Elements[state] = gs;
                    state += " gs ";
                }
                drawing.Append("q ").Append(state).Append(Numbers(place.Matrix.ToArray())).Append(" cm ").Append(name).Append(" Do Q\n");
                merged.Add(annotation);
                if (own) app++; else foreign++;
            }
            if (merged.Count == 0) continue;
            // The original content may leave its graphics state changed: isolate it before drawing on top.
            page.Contents.PrependContent().CreateStream("q\n"u8.ToArray());
            page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("Q\n" + drawing));
            var removed = new HashSet<PdfDictionary>(merged, ReferenceEqualityComparer.Instance);
            foreach (var annotation in merged)
                if (annotation.Elements.GetDictionary("/Popup") is { } popup) removed.Add(popup);
            for (int i = annots.Elements.Count - 1; i >= 0; i--)
                if (EditablePdfReader.Resolve(annots.Elements[i]) is { } a &&
                    (removed.Contains(a) || a.Elements.GetName("/Subtype") == "/Popup" && a.Elements.GetDictionary("/Parent") is { } parent && removed.Contains(parent)))
                    annots.Elements.RemoveAt(i);
            if (annots.Elements.Count == 0) page.Elements.Remove("/Annots");
        }
        RemoveAppData(document);
        using var output = new MemoryStream();
        document.Save(output, false);
        return new(output.ToArray(), app, foreign, kept);
    }

    /// <summary>
    /// Checks a written Flatten file: the renderer opens it with <paramref name="pageCount"/> pages and nothing in it is
    /// app data any more (no header, no app annotation, no key of ours), so the app opens it as a plain PDF (spec 14.5).
    /// </summary>
    public static void Verify(byte[] written, int pageCount)
    {
        using (var engine = new PdfiumDocument(written))
            if (engine.PageCount != pageCount) throw new InvalidDataException("The flattened PDF has a different page count.");
        using var document = PdfReader.Open(new MemoryStream(written, writable: false), PdfDocumentOpenMode.Import);
        foreach (var item in document.Internals.GetAllObjects())
            if (item is PdfDictionary dictionary && dictionary.Elements.Keys.Any(IsAppKey))
                throw new InvalidDataException("The flattened PDF still contains app data.");
        foreach (PdfPage page in document.Pages)
            if (page.Elements.GetArray("/Annots") is { } annots && annots.Elements.Any(a => EditablePdfReader.Resolve(a) is { } d && IsOwn(d)))
                throw new InvalidDataException("The flattened PDF still contains app annotations.");
        var read = EditablePdfReader.Read(written, PdfPageGeometryReader.Read(written));
        if (read.RestoredCount != 0 || read.ReadOnlyCount != 0) throw new InvalidDataException("The flattened PDF still restores app annotations.");
    }

    private static bool IsOwn(PdfDictionary annotation) =>
        (annotation.Elements.GetString("/NM") ?? "").StartsWith(EditablePdfKeys.NamePrefix, StringComparison.Ordinal) || annotation.Elements.ContainsKey(EditablePdfKeys.Envelope);

    private static bool IsAppKey(string key) => key.StartsWith("/PdfNotes", StringComparison.Ordinal);

    /// <summary>
    /// The normal appearance and the matrix that draws it at /Rect (ISO 32000-1 12.5.5: the form's bounding box after its
    /// /Matrix is fitted to the annotation rectangle). Null when there is no usable appearance.
    /// </summary>
    private static (PdfDictionary Form, Affine Matrix)? Placement(PdfDictionary annotation)
    {
        try
        {
            var normal = annotation.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
            if (normal is not null && normal.Stream is null)
                normal = annotation.Elements.GetName("/AS") is { Length: > 0 } state ? normal.Elements.GetDictionary(state) : null;
            if (normal?.Stream is null || annotation.Elements.GetArray("/Rect") is null) return null;
            var rect = annotation.Elements.GetRectangle("/Rect");
            var bbox = normal.Elements.GetRectangle("/BBox");
            var matrix = normal.Elements.GetArray("/Matrix") is { Elements.Count: 6 } values ? Affine.FromArray(values.Elements.Select(Number).ToArray()) : Affine.Identity;
            var corners = AnnotationGeometry.Corners(new(bbox.X1, bbox.Y1, bbox.Width, bbox.Height), matrix);
            double x0 = corners.Min(p => p.X), y0 = corners.Min(p => p.Y), x1 = corners.Max(p => p.X), y1 = corners.Max(p => p.Y);
            if (!(x1 - x0 > 1e-9 && y1 - y0 > 1e-9 && rect.Width > 1e-9 && rect.Height > 1e-9)) return null;
            var place = Affine.Translation(-x0, -y0).Then(Affine.Scale(rect.Width / (x1 - x0), rect.Height / (y1 - y0))).Then(Affine.Translation(rect.X1, rect.Y1));
            return place.ToArray().All(double.IsFinite) ? (normal, place) : null;
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or InvalidCastException or ArgumentException) { return null; }
    }

    /// <summary>The annotation's constant opacity (/CA) when below 1, else null.</summary>
    private static double? Opacity(PdfDictionary annotation)
    {
        if (annotation.Elements["/CA"] is not (PdfReal or PdfInteger)) return null;
        double alpha = Number(annotation.Elements["/CA"]);
        if (!double.IsFinite(alpha)) return null;
        alpha = Math.Clamp(alpha, 0, 1);
        return alpha < 1 ? alpha : null;
    }

    /// <summary>A transparency group form drawing <paramref name="form"/> unchanged, so an opacity applies to it as a whole.</summary>
    private static string Group(PdfPage page, PdfDictionary objects, string inner, PdfDictionary form)
    {
        var bbox = form.Elements.GetRectangle("/BBox");
        var matrix = form.Elements.GetArray("/Matrix") is { Elements.Count: 6 } values ? Affine.FromArray(values.Elements.Select(Number).ToArray()) : Affine.Identity;
        var corners = AnnotationGeometry.Corners(new(bbox.X1, bbox.Y1, bbox.Width, bbox.Height), matrix);
        var wrapper = new PdfDictionary(page.Owner);
        wrapper.Elements.SetName("/Type", "/XObject"); wrapper.Elements.SetName("/Subtype", "/Form");
        wrapper.Elements["/BBox"] = AnnotationAppearance.Array(page.Owner, [corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y)]);
        var group = new PdfDictionary(page.Owner); group.Elements.SetName("/S", "/Transparency"); wrapper.Elements["/Group"] = group;
        var own = new PdfDictionary(page.Owner); own.Elements["/A"] = objects.Elements[inner];
        var resources = new PdfDictionary(page.Owner); resources.Elements["/XObject"] = own; wrapper.Elements["/Resources"] = resources;
        wrapper.CreateStream("/A Do\n"u8.ToArray());
        page.Owner.Internals.AddObject(wrapper);
        string name = Unique(objects, "/PNG");
        objects.Elements[name] = wrapper.Reference!;
        return name;
    }

    private static PdfDictionary XObjects(PdfPage page)
    {
        var objects = page.Resources.Elements.GetDictionary("/XObject");
        if (objects is null) { objects = new PdfDictionary(page.Owner); page.Resources.Elements["/XObject"] = objects; }
        return objects;
    }

    private static string Unique(PdfDictionary objects, string prefix)
    {
        for (int n = 0; ; n++)
            if (!objects.Elements.ContainsKey(prefix + n.ToString(CultureInfo.InvariantCulture))) return prefix + n.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Removes every key of ours (catalog header, image originals, retained source, page fingerprints, text layer and form markers).</summary>
    private static void RemoveAppData(PdfDocument document)
    {
        PageContentWriter.RemoveMetadata(document);
        foreach (var item in document.Internals.GetAllObjects())
            if (item is PdfDictionary dictionary)
                foreach (string key in dictionary.Elements.Keys.Where(IsAppKey).ToArray()) dictionary.Elements.Remove(key);
        foreach (PdfPage page in document.Pages)
            foreach (string key in page.Elements.Keys.Where(IsAppKey).ToArray()) page.Elements.Remove(key);
    }

    private static double Number(PdfItem? item) => item switch { PdfReal r => r.Value, PdfInteger n => n.Value, _ => throw new InvalidDataException("Invalid numeric array.") };
    private static string Numbers(IEnumerable<double> numbers) => string.Join(" ", numbers.Select(n => n.ToString("0.######", CultureInfo.InvariantCulture)));
}
