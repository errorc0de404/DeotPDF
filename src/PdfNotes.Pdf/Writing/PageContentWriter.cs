using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Reading;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfNotes.Pdf.Writing;

/// <summary>Vector-preserving page placement. Always starts from the original stripped source, never a previous wrapper.</summary>
internal static class PageContentWriter
{
    internal static byte[] SingleBackground(DocumentSources source, PageSetup setup)
    {
        var (_, bytes, pages, index) = source.Locate(setup.Source.PdfPageIndex!.Value, setup.Source.ForeignConverted);
        using var stripped = EditablePdfReader.OpenStripped(bytes, pages, out var background, saveBackground: true);
        using var import = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(background.BackgroundPdf, false), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        using var document = new PdfDocument();
        var page = document.AddPage(import.Pages[index]); Apply(page, setup, pages[index]);
        using var output = new MemoryStream(); document.Save(output, false); return output.ToArray();
    }
    internal const string OriginalKey = "/PdfNotesOriginal";
    internal const string OriginalHashKey = "/PdfNotesOriginalHash";
    internal const string BackgroundHashKey = "/PdfNotesBackgroundHash";
    /// <summary>Fingerprint of a page's visible background; <paramref name="legacy"/> is the form of files saved before D13h.</summary>
    internal static string BackgroundHash(PdfPage page, bool legacy = false)
    {
        var selected = new PdfDictionary(page.Owner);
        foreach (var key in new[] { "/Contents", "/Resources", "/Annots", "/MediaBox", "/CropBox", "/Rotate", "/UserUnit" })
            if (page.Elements[key] is { } value && !(value is PdfArray { Elements.Count: 0 })) selected.Elements[key] = value;
        return SearchableText.Hash(selected, background: true, pageReferences: !legacy);
    }
    internal static bool BackgroundVerified(PdfDocument document)
    {
        try
        {
            return document.Pages.Cast<PdfPage>().All(p => p.Elements.GetString(BackgroundHashKey) is { } stored &&
                (stored == BackgroundHash(p) || stored == BackgroundHash(p, legacy: true)));
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException) { return false; }
    }
    internal static byte[]? Original(PdfDocument document)
    {
        try
        {
            var entry = document.Internals.Catalog.Elements.GetDictionary(OriginalKey);
            if (entry?.Stream is null || entry.Stream.Length > 512 * 1024 * 1024) return null;
            byte[] bytes = entry.Stream.UnfilteredValue;
            return bytes.Length <= 512 * 1024 * 1024 && Convert.ToHexString(SHA256.HashData(bytes)) == document.Internals.Catalog.Elements.GetString(OriginalHashKey) ? bytes : null;
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or InvalidCastException or ArgumentException or NotSupportedException) { return null; }
    }
    internal static void StoreOriginal(PdfDocument document, byte[] bytes)
    {
        var stream = new PdfDictionary(document); stream.CreateStream(bytes); stream.Stream.Zip(); document.Internals.AddObject(stream);
        document.Internals.Catalog.Elements[OriginalKey] = stream.Reference!;
        document.Internals.Catalog.Elements.SetString(OriginalHashKey, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    /// <summary>
    /// The stored original followed by imported pages (spec 11.9). <paramref name="original"/> is the stripped source
    /// without app metadata, or null for a new note; imported PDFs are already stripped.
    /// </summary>
    internal static byte[] Append(byte[]? original, IReadOnlyList<ImportedPdf> imported)
    {
        using var input = original is null ? null : new MemoryStream(original, writable: false);
        using var document = input is null ? new PdfDocument() : PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        foreach (var part in imported)
        {
            using var import = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(part.Bytes, false), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            foreach (PdfPage page in import.Pages) AddImported(document, page);
        }
        using var output = new MemoryStream(); document.Save(output, false); return output.ToArray();
    }

    /// <summary>Adds a page of another document. PDFsharp's import leaves out /UserUnit, which is part of the page size.</summary>
    internal static PdfPage AddImported(PdfDocument document, PdfPage page, int? at = null)
    {
        var added = at is int index ? document.Pages.Insert(index, page) : document.AddPage(page);
        if (page.Elements.ContainsKey("/UserUnit") && !added.Elements.ContainsKey("/UserUnit"))
            added.Elements.SetReal("/UserUnit", page.Elements.GetReal("/UserUnit"));
        return added;
    }

    internal static void RemoveMetadata(PdfDocument document)
    {
        foreach (var key in new[] { EditablePdfKeys.Header, "/PdfNotesResources", OriginalKey, OriginalHashKey }) document.Internals.Catalog.Elements.Remove(key);
    }

    internal static void Apply(PdfPage page, PageSetup setup, PdfPageGeometry original)
    {
        if (setup.Content is not { } content) return;
        if (Math.Abs(original.Box.WidthPt - content.WidthPt) > .001 || Math.Abs(original.Box.HeightPt - content.HeightPt) > .001)
            throw new InvalidDataException("Original content dimensions do not match the placement.");
        var target = new PdfPageBox(0, 0, setup.WidthPt, setup.HeightPt);
        var matrix = original.Box.PdfUserToCanonical.Then(content.Transform).Then(target.CanonicalToPdfUser);
        // Preserve the original CropBox as a permanent source clip. Later page crops do not delete any source data.
        var b = original.Box;
        page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes($"q\n{Numbers(matrix.ToArray())} cm\n{N(b.Left)} {N(b.Bottom)} {N(b.Right - b.Left)} {N(b.Top - b.Bottom)} re W n\n"));
        page.Contents.AppendContent().CreateStream("\nQ\n"u8.ToArray());
        TransformForeignAnnotations(page, original.Box, matrix);
        page.Elements.Remove("/UserUnit");
        var paper = new PdfRectangle(new XPoint(0, 0), new XPoint(setup.WidthPt, setup.HeightPt));
        page.MediaBox = paper; page.CropBox = paper;
        foreach (var key in new[] { "/TrimBox", "/BleedBox", "/ArtBox" }) page.Elements.Remove(key);
        page.Elements.SetInteger("/Rotate", setup.Rotation);
    }

    private static void TransformForeignAnnotations(PdfPage page, PdfPageBox clip, Affine matrix)
    {
        var annots = page.Elements.GetArray("/Annots"); if (annots is null) return;
        for (int i = annots.Elements.Count - 1; i >= 0; i--)
        {
            if (EditablePdfReader.Resolve(annots.Elements[i]) is not { } a) continue;
            var rect = a.Elements.GetRectangle("/Rect");
            double left = Math.Max(rect.X1, clip.Left), bottom = Math.Max(rect.Y1, clip.Bottom);
            double right = Math.Min(rect.X2, clip.Right), top = Math.Min(rect.Y2, clip.Top);
            if (right <= left || top <= bottom) { annots.Elements.RemoveAt(i); continue; } // original remains in the retained source
            var appearance = a.Elements.GetDictionary("/AP"); var form = appearance?.Elements.GetDictionary("/N");
            if (form?.Stream is null && appearance?.Elements.GetDictionary("/N") is { } states)
                form = states.Elements.GetDictionary(a.Elements.GetName("/AS"));
            if (form?.Stream is not null)
            {
                var bbox = form.Elements.GetRectangle("/BBox");
                var apMatrix = form.Elements.GetArray("/Matrix") is { } values ? Affine.FromArray(values.Elements.Select(Number).ToArray()) : Affine.Identity;
                var corners = AnnotationGeometry.Corners(new(bbox.X1, bbox.Y1, bbox.Width, bbox.Height), apMatrix);
                double x0 = corners.Min(p => p.X), y0 = corners.Min(p => p.Y), x1 = corners.Max(p => p.X), y1 = corners.Max(p => p.Y);
                if (x1 <= x0 || y1 <= y0) throw new InvalidDataException("Foreign annotation appearance has an invalid bounding box.");
                var place = Affine.Translation(-x0, -y0).Then(Affine.Scale(rect.Width / (x1 - x0), rect.Height / (y1 - y0))).Then(Affine.Translation(rect.X1, rect.Y1));
                var wrapper = new PdfDictionary(page.Owner); wrapper.Elements.SetName("/Type", "/XObject"); wrapper.Elements.SetName("/Subtype", "/Form");
                wrapper.Elements["/BBox"] = AnnotationAppearance.Array(page.Owner, [left, bottom, right, top]);
                var resources = new PdfDictionary(page.Owner); var objects = new PdfDictionary(page.Owner); objects.Elements["/A"] = (PdfItem?)form.Reference ?? form;
                resources.Elements["/XObject"] = objects; wrapper.Elements["/Resources"] = resources;
                wrapper.CreateStream(Encoding.ASCII.GetBytes($"q {N(clip.Left)} {N(clip.Bottom)} {N(clip.Right - clip.Left)} {N(clip.Top - clip.Bottom)} re W n {Numbers(place.ToArray())} cm /A Do Q\n"));
                page.Owner.Internals.AddObject(wrapper);
                var ap = new PdfDictionary(page.Owner); ap.Elements["/N"] = wrapper.Reference!; a.Elements["/AP"] = ap;
            }
            else if (left != rect.X1 || bottom != rect.Y1 || right != rect.X2 || top != rect.Y2)
                throw new NotSupportedException("A partially clipped foreign annotation without an appearance cannot be transformed safely.");
            var p0 = matrix.Transform(new(left, bottom)); var p1 = matrix.Transform(new(right, top));
            a.Elements["/Rect"] = AnnotationAppearance.Array(page.Owner, [Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y)]);
            // Standard coordinate arrays used when a viewer synthesizes an appearance.
            foreach (string key in new[] { "/QuadPoints", "/Vertices", "/L" })
                if (a.Elements.GetArray(key) is { } points)
                    for (int k = 0; k + 1 < points.Elements.Count; k += 2)
                    { var p = matrix.Transform(new(Number(points.Elements[k]), Number(points.Elements[k + 1]))); points.Elements[k] = AnnotationAppearance.Real(p.X); points.Elements[k + 1] = AnnotationAppearance.Real(p.Y); }
            if (a.Elements.GetArray("/InkList") is { } ink)
                foreach (var item in ink.Elements)
                    if (item is PdfArray points)
                        for (int k = 0; k + 1 < points.Elements.Count; k += 2)
                        { var p = matrix.Transform(new(Number(points.Elements[k]), Number(points.Elements[k + 1]))); points.Elements[k] = AnnotationAppearance.Real(p.X); points.Elements[k + 1] = AnnotationAppearance.Real(p.Y); }
            double scale = Math.Abs(matrix.M11);
            if (a.Elements.GetDictionary("/BS") is { } borderStyle && borderStyle.Elements.ContainsKey("/W")) borderStyle.Elements["/W"] = AnnotationAppearance.Real(Number(borderStyle.Elements["/W"]) * scale);
            if (a.Elements.GetArray("/Border") is { Elements.Count: >= 3 } border)
                for (int k = 0; k < 3; k++) border.Elements[k] = AnnotationAppearance.Real(Number(border.Elements[k]) * scale);
            a.Elements.SetInteger("/F", a.Elements.GetInteger("/F") & ~24); // allow zoom/rotation with the page
        }
    }
    private static double Number(PdfItem? item) => item switch { PdfReal r => r.Value, PdfInteger n => n.Value, _ => throw new InvalidDataException("Invalid numeric array.") };
    private static string N(double number) => number.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Numbers(IEnumerable<double> numbers) => string.Join(" ", numbers.Select(N));
}
