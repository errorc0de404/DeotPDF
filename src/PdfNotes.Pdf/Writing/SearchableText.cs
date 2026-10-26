using System.Security.Cryptography;
using System.Text;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfNotes.Pdf.Writing;

/// <summary>Positioned Unicode page text for selection/search in external readers. The annotation retains its
/// exact shaped appearance. Empty Type3 glyphs carry measured bounds and ToUnicode, never a font binary.
/// Each text box owns one authenticated content stream, removed with its restored annotation on open.</summary>
internal static class SearchableText
{
    internal const string LayerKey = "/PdfNotesTextLayer", HashKey = "/PdfNotesTextHash";
    private static string N(double value) => AnnotationAppearance.N(value);
    private static string Numbers(IEnumerable<double> values) => string.Join(' ', values.Select(N));

    internal static void Add(PdfDocument doc, PdfPage page, PdfPageBox pageBox, TextBoxAnnotation box, PdfDictionary annotation)
    {
        var layout = TextLayout.Measure(box);
        if (layout.Characters.IsEmpty) return;
        var fonts = new PdfDictionary(doc);
        var code = new StringBuilder("q\n");
        var m = box.Transform.Then(pageBox.CanonicalToPdfUser);
        code.Append(Numbers(m.ToArray())).Append(" cm\n0 0 ").Append(N(box.WidthPt)).Append(' ').Append(N(layout.Height)).Append(" re W n\nBT\n3 Tr\n");
        int group = 0;
        foreach (var line in layout.Characters.GroupBy(c => c.Line))
        foreach (var chunk in line.Chunk(255))
        {
            string fontName = "/F" + group++;
            fonts.Elements[fontName] = Font(doc, chunk).Reference!;
            var b = chunk[0].Bounds;
            code.Append(fontName).Append(' ').Append(N(b.Height)).Append(" Tf\n");
            code.Append("1 0 0 -1 ").Append(N(b.X)).Append(' ').Append(N(b.Y + b.Height)).Append(" Tm <");
            for (int i = 0; i < chunk.Length; i++) code.Append((i + 1).ToString("X2"));
            code.Append("> Tj\n");
        }
        code.Append("ET\nQ\n");
        var form = Stream(doc, code.ToString());
        form.Elements.SetName("/Type", "/XObject"); form.Elements.SetName("/Subtype", "/Form"); form.Elements.SetInteger("/FormType", 1);
        form.Elements["/BBox"] = AnnotationAppearance.Array(doc, [pageBox.Left, pageBox.Bottom, pageBox.Right, pageBox.Top]);
        var resources = new PdfDictionary(doc); resources.Elements["/Font"] = fonts; form.Elements["/Resources"] = resources;
        string name = "/PNT" + box.Id.ToString("N");
        var objects = page.Resources.Elements.GetDictionary("/XObject");
        if (objects is null) { objects = new PdfDictionary(doc); page.Resources.Elements["/XObject"] = objects; }
        objects.Elements[name] = form.Reference!;
        var content = page.Contents.AppendContent(); content.CreateStream(Encoding.ASCII.GetBytes("q " + name + " Do Q\n"));
        content.Elements.SetString("/PdfNotesTextId", box.Id.ToString("D"));
        content.Elements.SetString("/PdfNotesTextName", name);
        content.Elements["/PdfNotesTextForm"] = form.Reference!;
        annotation.Elements[LayerKey] = content.Reference!;
        annotation.Elements.SetString(HashKey, Hash(content));
    }

    private static PdfDictionary Font(PdfDocument doc, TextLayout.Character[] characters)
    {
        var origin = characters[0].Bounds;
        double units = 1000 / origin.Height;
        double Bottom(TextLayout.Character c) => (origin.Y + origin.Height - c.Bounds.Y - c.Bounds.Height) * units;
        double Advance(int i) => (i + 1 < characters.Length ? characters[i + 1].Bounds.X - characters[i].Bounds.X : characters[i].Bounds.Width) * units;
        var font = new PdfDictionary(doc); font.Elements.SetName("/Type", "/Font"); font.Elements.SetName("/Subtype", "/Type3");
        font.Elements["/FontMatrix"] = AnnotationAppearance.Array(doc, [.001, 0, 0, .001, 0, 0]);
        font.Elements["/FontBBox"] = AnnotationAppearance.Array(doc, [0, characters.Min(Bottom), characters.Max(c => c.Bounds.Width * units), characters.Max(c => Bottom(c) + c.Bounds.Height * units)]);
        font.Elements.SetInteger("/FirstChar", 1); font.Elements.SetInteger("/LastChar", characters.Length);
        font.Elements["/Widths"] = AnnotationAppearance.Array(doc, Enumerable.Range(0, characters.Length).Select(Advance));
        var procs = new PdfDictionary(doc); var differences = new PdfArray(doc); differences.Elements.Add(new PdfInteger(1));
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /PdfNotesUnicode def\n/CMapType 2 def\n1 begincodespacerange\n<00> <FF>\nendcodespacerange\n");
        // CMap blocks are at most 100 entries (PDF text specification).
        for (int start = 0; start < characters.Length; start += 100)
        {
            int count = Math.Min(100, characters.Length - start); cmap.Append(count).Append(" beginbfchar\n");
            for (int i = start; i < start + count; i++)
                cmap.Append('<').Append((i + 1).ToString("X2")).Append("> <").Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(characters[i].Text))).Append(">\n");
            cmap.Append("endbfchar\n");
        }
        for (int i = 0; i < characters.Length; i++)
        {
            string name = "/c" + (i + 1); differences.Elements.Add(new PdfName(name));
            var b = characters[i].Bounds;
            double bottom = Bottom(characters[i]);
            procs.Elements[name] = Stream(doc, $"{N(Advance(i))} 0 0 {N(bottom)} {N(b.Width * units)} {N(bottom + b.Height * units)} d1\n").Reference!;
        }
        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        font.Elements["/ToUnicode"] = Stream(doc, cmap.ToString()).Reference!;
        font.Elements["/CharProcs"] = procs; font.Elements["/Resources"] = new PdfDictionary(doc);
        var encoding = new PdfDictionary(doc); encoding.Elements.SetName("/Type", "/Encoding"); encoding.Elements["/Differences"] = differences; font.Elements["/Encoding"] = encoding;
        doc.Internals.AddObject(font); return font;
    }

    private static PdfDictionary Stream(PdfDocument doc, string code)
    {
        var item = new PdfDictionary(doc); item.CreateStream(Encoding.ASCII.GetBytes(code)); item.Stream.Zip(); doc.Internals.AddObject(item); return item;
    }

    internal static bool Verified(PdfPage page, PdfDictionary annotation, Guid id)
    {
        if (!annotation.Elements.ContainsKey(LayerKey)) return !annotation.Elements.ContainsKey(HashKey); // earlier D06 PDFs
        try
        {
            var layer = annotation.Elements.GetDictionary(LayerKey);
            if (layer?.Stream is null || layer.Elements.GetString("/PdfNotesTextId") != id.ToString("D") ||
                annotation.Elements.GetString(HashKey) != Hash(layer)) return false;
            string name = layer.Elements.GetString("/PdfNotesTextName");
            var form = layer.Elements.GetDictionary("/PdfNotesTextForm");
            return page.Contents.Elements.Cast<PdfItem>().Any(item => Resolve(item)?.Reference == layer.Reference) &&
                page.Resources.Elements.GetDictionary("/XObject")?.Elements.GetDictionary(name)?.Reference == form?.Reference &&
                Encoding.ASCII.GetString(layer.Stream.UnfilteredValue) == "q " + name + " Do Q\n";
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidCastException or ArgumentException or InvalidDataException or NotSupportedException) { return false; }
    }

    internal static void Remove(PdfPage page, PdfDictionary annotation)
    {
        if (annotation.Elements.GetDictionary(LayerKey) is not { } layer) return;
        for (int i = page.Contents.Elements.Count - 1; i >= 0; i--)
            if (Resolve(page.Contents.Elements[i])?.Reference == layer.Reference) page.Contents.Elements.RemoveAt(i);
        page.Resources.Elements.GetDictionary("/XObject")?.Elements.Remove(layer.Elements.GetString("/PdfNotesTextName"));
    }

    private static PdfDictionary? Resolve(PdfItem item) => item is PdfReference r ? r.Value as PdfDictionary : item as PdfDictionary;
    /// <param name="pageReferences">
    /// Background hash only (D13h fix): a page reached through a reference (a link's /Dest) counts as "a page" instead of
    /// its content. Hashing the target page made the hash depend on other pages' order and keys, so moving or deleting
    /// pages of a PDF with links failed save verification. False reproduces the hash of files saved before.
    /// </param>
    internal static string Hash(PdfDictionary layer, bool background = false, bool pageReferences = false)
    {
        var code = new StringBuilder(); int objects = 0;
        var active = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        void Write(PdfItem item, int depth)
        {
            if (++objects > 1_000_000 || depth > 20) throw new InvalidDataException("Text layer resource graph exceeds limits.");
            if (item is PdfReference reference) { Write(reference.Value ?? throw new InvalidDataException("Missing text resource."), depth + 1); return; }
            if (item is PdfDictionary dictionary)
            {
                if (pageReferences && depth > 0 && dictionary.Elements.GetName("/Type") == "/Page") { code.Append("page"); return; }
                if (background && !active.Add(dictionary)) { code.Append("cycle"); return; }
                code.Append('{');
                foreach (string key in dictionary.Elements.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (key is "/Length" or "/Filter" or "/DecodeParms") continue;
                    if (background && (key is "/P" or "/Parent" or "/Prev" or "/Next" || key == "/XObject" && dictionary.Elements.GetDictionary(key)?.Elements.Count == 0)) continue;
                    code.Append(key).Append(':'); Write(dictionary.Elements[key] ?? throw new InvalidDataException("Missing text dictionary value."), depth + 1); code.Append(';');
                }
                if (dictionary.Stream is { } stream)
                {
                    if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("Text stream exceeds limit.");
                    byte[] bytes = stream.UnfilteredValue;
                    if (bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("Decoded text stream exceeds limit.");
                    code.Append(Convert.ToHexString(SHA256.HashData(bytes)));
                }
                code.Append('}'); if (background) active.Remove(dictionary); return;
            }
            if (item is PdfArray array) { code.Append('['); foreach (var child in array.Elements) { Write(child, depth + 1); code.Append(';'); } code.Append(']'); return; }
            string value = item switch { PdfReal r => N(r.Value), PdfInteger i => N(i.Value), PdfLiteral l => l.Value, PdfString s => Convert.ToHexString(Encoding.UTF8.GetBytes(s.Value)), _ => item.ToString()! };
            code.Append(value);
        }
        Write(layer, 0); return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.ToString())));
    }
}
