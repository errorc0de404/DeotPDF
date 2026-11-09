using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Pdf.Prototype;

/// <summary>D01 storage proof, deliberately separate from the future production schema.</summary>
public static class ProbePdfStore
{
    private const string Marker = "PdfNotes.D01";
    private const int MetadataLimit = 4 * 1024 * 1024;
    private record Header(string App, int Version, Guid DocumentId, Guid[] Pages);
    private record Envelope(string App, int Version, Guid DocumentId, Guid PageId,
        string AppearanceHash, ProbeAnnotation Annotation);
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static PageMetrics[] GetMetrics(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        return doc.Pages.Cast<PdfPage>().Select(Metrics).ToArray();
    }

    private static PageMetrics Metrics(PdfPage p)
    {
        var crop = p.CropBox;
        if(crop.Width <= 0 || crop.Height <= 0) crop = p.MediaBox;
        double unit = p.Elements.ContainsKey("/UserUnit") ? p.Elements.GetReal("/UserUnit") : 1;
        if (!double.IsFinite(unit) || unit <= 0) throw new InvalidDataException("Invalid /UserUnit.");
        return new(crop.Width * unit, crop.Height * unit, crop.X1, crop.Y1,
            crop.X2, crop.Y2, p.Rotate, unit);
    }

    private static PdfDictionary Stream(PdfDocument doc, byte[] data)
    {
        var dict = new PdfDictionary(doc);
        dict.CreateStream(data);
        doc.Internals.AddObject(dict);
        return dict;
    }

    private static byte[] Bytes(PdfDocument doc)
    {
        using var buffer = new MemoryStream();
        doc.Save(buffer, false);
        return buffer.ToArray();
    }

    public static ProbeOpenResult Open(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        var warnings = new List<string>();
        var header = ReadJson<Header>(doc.Internals.Catalog.Elements.GetDictionary("/PdfNotes"));
        if (header is null || header.App != Marker || header.Version != 0 || header.DocumentId == Guid.Empty ||
            header.Pages is null || header.Pages.Length != doc.PageCount || header.Pages.Any(id => id == Guid.Empty) || header.Pages.Distinct().Count() != header.Pages.Length)
        {
            if (doc.Internals.Catalog.Elements.ContainsKey("/PdfNotes")) warnings.Add("Unknown or invalid D01 metadata: appearances remain read-only.");
            return new(null, Bytes(doc), warnings.ToArray());
        }
        var pages = new List<ProbePage>();
        var allIds = new HashSet<Guid>();
        for (int pageIndex = 0; pageIndex < doc.PageCount; pageIndex++)
        {
            var p = doc.Pages[pageIndex];
            Guid pageId = header.Pages[pageIndex];
            var restored = new List<ProbeAnnotation>();
            var annots = p.Elements.GetArray("/Annots");
            if (annots is not null)
            {
                for (int i = annots.Elements.Count - 1; i >= 0; i--)
                {
                    var a = Resolve(annots.Elements[i]);
                    if (a is null || !a.Elements.ContainsKey("/PdfNotesData")) continue;
                    var e = ReadJson<Envelope>(a.Elements.GetDictionary("/PdfNotesData"));
                    var form = a.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
                    bool valid = e is not null && e.Annotation is not null && e.App == Marker && e.Version == 0 &&
                        e.DocumentId == header.DocumentId && e.PageId == pageId &&
                        a.Elements.GetString("/NM") == $"PdfNotes:{e.Annotation.Id:D}" &&
                        form?.Stream is not null && e.AppearanceHash == Hash(form.Stream.UnfilteredValue) &&
                        Valid(e.Annotation) && allIds.Add(e.Annotation.Id);
                    if (!valid) { warnings.Add($"Page {pageIndex + 1}: unverified annotation preserved read-only."); continue; }
                    restored.Add(e!.Annotation!);
                    annots.Elements.RemoveAt(i); // hide only appearances that were successfully restored
                }
            }
            restored.Reverse();
            pages.Add(new(pageId, restored.ToArray()));
        }
        return new(new(header.DocumentId, pages.ToArray()), Bytes(doc), warnings.ToArray());
    }

    private static bool Valid(ProbeAnnotation a)
    {
        if (a.Id == Guid.Empty || a.Type is not ("text" or "ink" or "image" or "quadratic" or "cubic" or "highlight") || a.Paths is null) return false;
        if (a.TextRuns?.Any(r => string.IsNullOrWhiteSpace(r.Font) || !double.IsFinite(r.Size) || r.Size <= 0) == true) return false;
        foreach (var p in a.Paths)
        {
            if (p.Segments is null || !double.IsFinite(p.Width) || p.Width < 0 || !double.IsFinite(p.Opacity) || p.Opacity < 0 || p.Opacity > 1) return false;
            foreach (var s in p.Segments)
            {
                int count = s.Kind switch { "M" or "L" => 1, "C" => 3, "Z" => 0, _ => -1 };
                if (s.Points is null || s.Points.Length != count || s.Points.Any(v => !double.IsFinite(v.X) || !double.IsFinite(v.Y))) return false;
            }
        }
        if (a.Image is { } image && (image.PixelWidth <= 0 || image.PixelHeight <= 0 ||
            (long)image.PixelWidth * image.PixelHeight > 1_000_000 || image.Rgba.Length != (long)image.PixelWidth * image.PixelHeight * 4 ||
            !double.IsFinite(image.X) || !double.IsFinite(image.Y) || !double.IsFinite(image.Width) || !double.IsFinite(image.Height) || image.Width <= 0 || image.Height <= 0)) return false;
        return true;
    }

    private static T? ReadJson<T>(PdfDictionary? dictionary)
    {
        try
        {
            if (dictionary?.Stream is null || dictionary.Stream.Length > MetadataLimit) return default;
            var bytes = dictionary.Stream.UnfilteredValue;
            return bytes.Length <= MetadataLimit ? JsonSerializer.Deserialize<T>(bytes, JsonOptions) : default;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException) { return default; }
    }

    private static PdfDictionary? Resolve(PdfItem item) => item is PdfReference r ? r.Value as PdfDictionary : item as PdfDictionary;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string N(double n) => n.ToString("0.#########", CultureInfo.InvariantCulture);
    private static PdfArray Array(PdfDocument doc, params double[] values) => new(doc, values.Select(v => (PdfItem)new PdfReal(v)).ToArray());

    public static void SaveEditable(string source, ProbeModel model, string target) => Write(source, model, target, false);
    public static void ExportFlattened(string source, ProbeModel model, string target) => Write(source, model, target, true);

    private static void Write(string source, ProbeModel model, string target, bool flatten)
    {
        // Open strips ONLY validated own appearances. Foreign/unrestorable annotations survive.
        var opened = Open(source);
        using var buffer = new MemoryStream(opened.BackgroundPdf);
        using var doc = PdfReader.Open(buffer, PdfDocumentOpenMode.Modify);
        if (doc.PageCount != model.Pages.Length || model.DocumentId == Guid.Empty ||
            model.Pages.Select(p => p.Id).Distinct().Count() != doc.PageCount ||
            model.Pages.SelectMany(p => p.Annotations).Select(a => a.Id).Distinct().Count() != model.Pages.Sum(p => p.Annotations.Length))
            throw new InvalidDataException("Invalid D01 model identity/count.");
        if (opened.Model is not null && (opened.Model.DocumentId != model.DocumentId ||
            !opened.Model.Pages.Select(p => p.Id).SequenceEqual(model.Pages.Select(p => p.Id))))
            throw new InvalidDataException("Source/model identity mismatch.");
        for (int i = 0; i < model.Pages.Length; i++)
        {
            var page = doc.Pages[i];
            var metrics = Metrics(page);
            foreach (var a in model.Pages[i].Annotations)
            {
                if (!Valid(a)) throw new InvalidDataException("Invalid D01 annotation.");
                var form = Appearance(doc, metrics, a);
                if (flatten)
                {
                    var resources = page.Elements.GetDictionary("/Resources") ?? new PdfDictionary(doc);
                    page.Elements["/Resources"] = resources;
                    var xobjects = resources.Elements.GetDictionary("/XObject") ?? new PdfDictionary(doc);
                    resources.Elements["/XObject"] = xobjects;
                    string name = "/PN" + a.Id.ToString("N");
                    while (xobjects.Elements.ContainsKey(name)) name += "x";
                    xobjects.Elements[name] = form.Reference!;
                    var content = page.Contents.AppendContent();
                    content.CreateStream(Encoding.ASCII.GetBytes($"q 1 0 0 1 {N(metrics.CropLeft)} {N(metrics.CropBottom)} cm {name} Do Q\n"));
                }
                else
                {
                    var annotation = new PdfDictionary(doc);
                    annotation.Elements.SetName("/Type", "/Annot");
                    annotation.Elements.SetName("/Subtype", "/Stamp");
                    annotation.Elements.SetName("/Name", "/Approved");
                    annotation.Elements.SetInteger("/F", 4); // Print; no NoRotate/NoZoom flags
                    annotation.Elements.SetString("/NM", $"PdfNotes:{a.Id:D}");
                    annotation.Elements["/Rect"] = Array(doc, metrics.CropLeft, metrics.CropBottom, metrics.CropRight, metrics.CropTop);
                    var ap = new PdfDictionary(doc);
                    ap.Elements["/N"] = form.Reference!;
                    annotation.Elements["/AP"] = ap;
                    var e = new Envelope(Marker, 0, model.DocumentId, model.Pages[i].Id, Hash(form.Stream.UnfilteredValue), a);
                    byte[] json = JsonSerializer.SerializeToUtf8Bytes(e, JsonOptions);
                    if (json.Length > MetadataLimit) throw new InvalidDataException("D01 metadata exceeds its limit.");
                    annotation.Elements["/PdfNotesData"] = Stream(doc, json).Reference!;
                    doc.Internals.AddObject(annotation);
                    var annots = page.Elements.GetArray("/Annots") ?? new PdfArray(doc);
                    page.Elements["/Annots"] = annots;
                    annots.Elements.Add(annotation.Reference!);
                }
            }
        }
        if (flatten)
        {
            doc.Internals.Catalog.Elements.Remove("/PdfNotes");
            // Remaining foreign/invalid appearance objects stay visible; no private editable data is exported.
            foreach (PdfPage p in doc.Pages)
            {
                var annots = p.Elements.GetArray("/Annots");
                if (annots is not null)
                    foreach (var item in annots.Elements) Resolve(item)?.Elements.Remove("/PdfNotesData");
            }
        }
        else
        {
            var header = new Header(Marker, 0, model.DocumentId, model.Pages.Select(p => p.Id).ToArray());
            doc.Internals.Catalog.Elements["/PdfNotes"] = Stream(doc, JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions)).Reference!;
        }
        // D01 uses full rewriting and checks the temporary PDF before replacement.
        // D04 adds external-change detection, snapshot revisions and UI error handling.
        string absolute = Path.GetFullPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        string temporary = absolute + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            doc.Save(temporary);
            using (var check = new Engine.PdfiumDocument(temporary))
                if (check.PageCount != model.Pages.Length) throw new InvalidDataException("Written PDF failed reopen.");
            if (File.Exists(absolute)) File.Replace(temporary, absolute, null);
            else File.Move(temporary, absolute);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static PdfDictionary Appearance(PdfDocument doc, PageMetrics page, ProbeAnnotation a)
    {
        var resources = new PdfDictionary(doc);
        var states = new PdfDictionary(doc);
        resources.Elements["/ExtGState"] = states;
        var code = new StringBuilder($"q {N(1/page.UserUnit)} 0 0 {N(-1/page.UserUnit)} 0 {N(page.HeightPt/page.UserUnit)} cm\n");
        for (int i = 0; i < a.Paths.Length; i++)
        {
            var path = a.Paths[i];
            var state = new PdfDictionary(doc);
            state.Elements.SetReal("/CA", path.Opacity);
            state.Elements.SetReal("/ca", path.Opacity);
            if (a.Type == "highlight") state.Elements.SetName("/BM", "/Multiply");
            states.Elements["/S" + i] = state;
            code.Append($"q /S{i} gs {N(path.Color.R/255d)} {N(path.Color.G/255d)} {N(path.Color.B/255d)} {(path.Fill ? "rg" : "RG")} {N(path.Width)} w 1 J 1 j\n");
            foreach (var s in path.Segments)
            {
                foreach (var p in s.Points) code.Append($"{N(p.X)} {N(p.Y)} ");
                code.Append(s.Kind switch { "M" => "m\n", "L" => "l\n", "C" => "c\n", "Z" => "h\n", _ => throw new InvalidDataException() });
            }
            code.Append(path.Fill ? (path.EvenOdd ? "f* Q\n" : "f Q\n") : "S Q\n");
        }
        if (a.Image is { } image)
        {
            int count = image.PixelWidth * image.PixelHeight;
            var rgb = new byte[count * 3];
            var alpha = new byte[count];
            for (int i = 0; i < count; i++)
            {
                System.Array.Copy(image.Rgba, i*4, rgb, i*3, 3);
                alpha[i] = image.Rgba[i*4+3];
            }
            var mask = ImageStream(doc, image, alpha, "/DeviceGray");
            var raster = ImageStream(doc, image, rgb, "/DeviceRGB");
            raster.Elements["/SMask"] = mask.Reference!;
            var xobjects = new PdfDictionary(doc);
            xobjects.Elements["/Image"] = raster.Reference!;
            resources.Elements["/XObject"] = xobjects;
            code.Append($"q {N(image.Width)} 0 0 {N(-image.Height)} {N(image.X)} {N(image.Y+image.Height)} cm /Image Do Q\n");
        }
        code.Append("Q\n");
        var form = Stream(doc, Encoding.ASCII.GetBytes(code.ToString()));
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements.SetInteger("/FormType", 1);
        form.Elements["/BBox"] = Array(doc, 0, 0, page.WidthPt/page.UserUnit, page.HeightPt/page.UserUnit);
        form.Elements["/Resources"] = resources;
        return form;
    }

    private static PdfDictionary ImageStream(PdfDocument doc, ProbeImage image, byte[] bytes, string space)
    {
        var stream = Stream(doc, bytes);
        stream.Elements.SetName("/Type", "/XObject");
        stream.Elements.SetName("/Subtype", "/Image");
        stream.Elements.SetInteger("/Width", image.PixelWidth);
        stream.Elements.SetInteger("/Height", image.PixelHeight);
        stream.Elements.SetInteger("/BitsPerComponent", 8);
        stream.Elements.SetName("/ColorSpace", space);
        return stream;
    }
}
