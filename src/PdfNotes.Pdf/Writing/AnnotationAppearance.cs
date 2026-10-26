using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfSharp.Pdf;

namespace PdfNotes.Pdf.Writing;

/// <summary>
/// Where the editable PDF keeps app data (schema v1, docs/schema-v1.md). Each app annotation is a /Stamp
/// annotation with a standard appearance plus an app-only envelope stream; the document header is a catalog stream.
/// </summary>
public static class EditablePdfKeys
{
    public const string Header = "/PdfNotes";
    public const string Envelope = "/PdfNotesData";
    public const string AppearanceHash = "/PdfNotesAP";
    public const string NamePrefix = "PdfNotes:";
    public const int PdfFlagPrint = 4;
}

/// <summary>
/// Standard appearance streams of app annotations (spec 14.2). Every kind the reader may restore must be listed in
/// <see cref="IsSupported"/>; a kind without an appearance is never saved or restored as editable.
/// </summary>
public static class AnnotationAppearance
{
    /// <summary>Kinds with a screen painter and a PDF appearance (D04 strokes and their D08 eraser pieces, D05 images, D06 text, D07 lines, D09 PDF text highlights, D11 shapes).</summary>
    public static bool IsSupported(AnnotationKind kind) => kind is AnnotationKind.Ink or AnnotationKind.FreeHighlight or AnnotationKind.Image or AnnotationKind.TextBox
        or AnnotationKind.Line or AnnotationKind.PdfTextHighlight or AnnotationKind.Shape or AnnotationKind.TextMarkup or AnnotationKind.Note;

    /// <summary>
    /// The standard subtype an app annotation is written as (spec 14.2, 24.8–24.9): underline/strikeout markups and notes
    /// use their standard types so other viewers treat them as such; everything else is a stamp with our appearance.
    /// </summary>
    public static string Subtype(Annotation annotation) => annotation switch
    {
        PdfTextMarkup { MarkupKind: TextMarkupKind.Underline } => "/Underline",
        PdfTextMarkup => "/StrikeOut",
        StickyNote => "/Text",
        _ => "/Stamp"
    };

    /// <summary>Subtypes an app annotation may have when it is read back.</summary>
    public static bool IsAppSubtype(string subtype) => subtype is "/Stamp" or "/Underline" or "/StrikeOut" or "/Text";

    /// <summary>
    /// Builds the appearance form XObject of <paramref name="annotation"/> on a page with <paramref name="box"/>.
    /// The form draws in canonical coordinates through one <c>cm</c> (local → canonical → PDF user space), so the
    /// annotation rotates with the page like the page content. <paramref name="rect"/> is the annotation /Rect and
    /// <paramref name="hash"/> the value <see cref="Hash(PdfDictionary)"/> returns for the form after a save/read round trip.
    /// </summary>
    internal static PdfDictionary Create(PdfDocument document, PdfPageBox box, Annotation annotation, IReadOnlyDictionary<Guid, PdfDictionary> images, out double[] rect, out string hash)
    {
        if (annotation is TextBoxAnnotation text) return CreateText(document, box, text, out rect, out hash);
        if (annotation is ImageAnnotation image) return CreateImage(document, box, image, images[image.ResourceId], out rect, out hash);
        if (annotation is LineAnnotation line) return CreateLine(document, box, line, out rect, out hash);
        if (annotation is PdfTextHighlight highlight) return CreateTextHighlight(document, box, highlight, out rect, out hash);
        if (annotation is PdfTextMarkup markup) return CreateFill(document, box, TextSelection.PaintedQuads(markup), markup.Color, markup.Opacity, out rect, out hash);
        if (annotation is StickyNote note) return CreateNote(document, box, note, out rect, out hash);
        if (annotation is ShapeAnnotation shape) return CreateShape(document, box, shape, out rect, out hash);
        if (annotation is not StrokeAnnotation stroke) throw new NotSupportedException($"{annotation.Kind} has no PDF appearance yet.");
        var toUser = annotation.Transform.Then(box.CanonicalToPdfUser);
        var local = AnnotationGeometry.LocalBounds(stroke);
        rect = UserBounds(toUser, local, margin: 1);

        var code = new StringBuilder();
        code.Append("q ").Append(Numbers(toUser.ToArray())).Append(" cm /GS0 gs\n");
        var c = stroke.Color;
        string rgb = $"{N(c.R / 255d)} {N(c.G / 255d)} {N(c.B / 255d)}";
        if (stroke.IsOutlined)
        {
            // Area-eraser piece (D08): its painted region as one even-odd filled path, composited once.
            code.Append(rgb).Append(" rg\n");
            foreach (var ring in stroke.Outline)
            {
                for (int i = 0; i < ring.Length; i++)
                    code.Append(N(ring[i].X)).Append(' ').Append(N(ring[i].Y)).Append(i == 0 ? " m\n" : " l\n");
                code.Append("h\n");
            }
            code.Append("f*\n");
        }
        else if (stroke.Points.Length == 1)
        {
            // A click is a round dot of the stroke width (spec 7.2).
            code.Append(rgb).Append(" rg\n");
            AppendCircle(code, stroke.Points[0], stroke.WidthPt / 2);
            code.Append("f\n");
        }
        else
        {
            // One path painted once: a highlighter's self-overlap is composited a single time (spec 9.2).
            code.Append(rgb).Append(" RG ").Append(N(stroke.WidthPt)).Append(" w 1 J 1 j\n");
            for (int i = 0; i < stroke.Points.Length; i++)
                code.Append(N(stroke.Points[i].X)).Append(' ').Append(N(stroke.Points[i].Y)).Append(i == 0 ? " m\n" : " l\n");
            code.Append("S\n");
        }
        code.Append("Q\n");

        var state = new PdfDictionary(document);
        state.Elements.SetName("/Type", "/ExtGState");
        state.Elements["/CA"] = new PdfLiteral(N(stroke.Opacity));
        state.Elements["/ca"] = new PdfLiteral(N(stroke.Opacity));
        var states = new PdfDictionary(document);
        states.Elements["/GS0"] = state;
        var resources = new PdfDictionary(document);
        resources.Elements["/ExtGState"] = states;

        byte[] content = Encoding.ASCII.GetBytes(code.ToString());
        hash = Hash(content, rect, stroke.Opacity, stroke.Opacity, "", matrix: false);
        var form = new PdfDictionary(document);
        form.CreateStream(content);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements.SetInteger("/FormType", 1);
        form.Elements["/BBox"] = Array(document, rect);
        form.Elements["/Resources"] = resources;
        form.Stream.Zip();
        document.Internals.AddObject(form);
        return form;
    }

    /// <summary>
    /// Fingerprint of everything that determines the visible result: drawing operators, opacity and bounding box.
    /// Stored next to the envelope so an appearance changed by another app is not hidden behind restored data.
    /// </summary>
    internal static string Hash(PdfDictionary form)
    {
        var bbox = form.Elements.GetArray("/BBox");
        var values = bbox is null ? [] : Enumerable.Range(0, bbox.Elements.Count).Select(bbox.Elements.GetReal).ToArray();
        var state = form.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/ExtGState")?.Elements.GetDictionary("/GS0");
        string basic = Hash(form.Stream?.UnfilteredValue ?? [], values, state?.Elements.GetReal("/CA") ?? -1, state?.Elements.GetReal("/ca") ?? -1,
            state?.Elements.GetName("/BM") ?? "", form.Elements.ContainsKey("/Matrix"));
        var image = form.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject")?.Elements.GetDictionary("/Image");
        if (form.Elements.GetBoolean("/PdfNotesText") || form.Elements.GetBoolean("/PdfNotesShape"))
        {
            var states = form.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/ExtGState")!;
            var alpha = string.Join(";", states.Elements.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => k + "=" + N(states.Elements.GetDictionary(k)!.Elements.GetReal("/ca")) + "," + N(states.Elements.GetDictionary(k)!.Elements.GetReal("/CA")) + "," + states.Elements.GetDictionary(k)!.Elements.GetName("/BM")));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basic + alpha)));
        }
        if (form.Elements.GetBoolean("/PdfNotesLine"))
        {
            var group = form.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject")?.Elements.GetDictionary("/L")
                ?? throw new InvalidDataException("Line group is missing.");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basic + LineGroupHash(group))));
        }
        return image is null ? basic : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basic + ImageHash(image))));
    }

    /// <summary>The drawing of a line lives in a transparency group; its operators, bounds and group type are fingerprinted too.</summary>
    private static string LineGroupHash(PdfDictionary group)
    {
        var bbox = group.Elements.GetArray("/BBox");
        var values = bbox is null ? [] : Enumerable.Range(0, bbox.Elements.Count).Select(bbox.Elements.GetReal).ToArray();
        string inner = Hash(group.Stream?.UnfilteredValue ?? [], values, -1, -1, group.Elements.GetDictionary("/Group")?.Elements.GetName("/S") ?? "",
            group.Elements.ContainsKey("/Matrix") || group.Elements.ContainsKey("/Resources"));
        return "|line:" + inner;
    }

    /// <summary>
    /// Line or Bézier with dash, round caps and arrow heads (spec 8). The body and heads are drawn opaque inside a
    /// transparency group that is painted once with the line opacity, so the overlap under a head is not darker —
    /// matching the screen painter's single opacity layer. Quadratics are written as their exact cubic elevation.
    /// </summary>
    private static PdfDictionary CreateLine(PdfDocument doc, PdfPageBox box, LineAnnotation line, out double[] rect, out string hash)
    {
        var toUser = line.Transform.Then(box.CanonicalToPdfUser);
        rect = UserBounds(toUser, LineGeometry.Bounds(line), 1);
        var c = line.Color;
        string rgb = $"{N(c.R / 255d)} {N(c.G / 255d)} {N(c.B / 255d)}";
        var code = new StringBuilder();
        code.Append("q ").Append(Numbers(toUser.ToArray())).Append(" cm\n");
        if (LineGeometry.Body(line) is { } body)
        {
            code.Append(rgb).Append(" RG ").Append(N(line.WidthPt)).Append(" w 1 J 1 j [");
            code.Append(string.Join(' ', LineGeometry.DashPattern(line.Dash).Select(v => N(v * line.WidthPt)))).Append("] 0 d\n");
            var cubic = body.Length == 3 ? LineGeometry.Elevate(body) : body;
            code.Append(N(cubic[0].X)).Append(' ').Append(N(cubic[0].Y)).Append(" m\n");
            if (cubic.Length == 2) code.Append(N(cubic[1].X)).Append(' ').Append(N(cubic[1].Y)).Append(" l\n");
            else code.Append(Numbers([cubic[1].X, cubic[1].Y, cubic[2].X, cubic[2].Y, cubic[3].X, cubic[3].Y])).Append(" c\n");
            code.Append("S\n");
        }
        foreach (var head in LineGeometry.ArrowHeads(line))
        {
            code.Append(rgb).Append(" rg ").Append(N(head[0].X)).Append(' ').Append(N(head[0].Y)).Append(" m ");
            code.Append(N(head[1].X)).Append(' ').Append(N(head[1].Y)).Append(" l ").Append(N(head[2].X)).Append(' ').Append(N(head[2].Y)).Append(" l h f\n");
        }
        code.Append("Q\n");

        var group = new PdfDictionary(doc);
        group.Elements.SetName("/Type", "/XObject"); group.Elements.SetName("/Subtype", "/Form"); group.Elements.SetInteger("/FormType", 1);
        group.Elements["/BBox"] = new PdfArray(doc, rect.Select(v => (PdfItem)Real(v)).ToArray());
        var transparency = new PdfDictionary(doc);
        transparency.Elements.SetName("/S", "/Transparency");
        group.Elements["/Group"] = transparency;
        group.CreateStream(Encoding.ASCII.GetBytes(code.ToString()));
        group.Stream.Zip();
        doc.Internals.AddObject(group);

        var state = new PdfDictionary(doc);
        state.Elements.SetName("/Type", "/ExtGState");
        state.Elements["/CA"] = Real(line.Opacity);
        state.Elements["/ca"] = Real(line.Opacity);
        var states = new PdfDictionary(doc); states.Elements["/GS0"] = state;
        var xobjects = new PdfDictionary(doc); xobjects.Elements["/L"] = group.Reference!;
        var resources = new PdfDictionary(doc);
        resources.Elements["/ExtGState"] = states; resources.Elements["/XObject"] = xobjects;
        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject"); form.Elements.SetName("/Subtype", "/Form"); form.Elements.SetInteger("/FormType", 1);
        form.Elements["/BBox"] = new PdfArray(doc, rect.Select(v => (PdfItem)Real(v)).ToArray());
        form.Elements["/Resources"] = resources;
        form.Elements.SetBoolean("/PdfNotesLine", true);
        form.CreateStream(Encoding.ASCII.GetBytes("/GS0 gs /L Do\n"));
        hash = Hash(form); form.Stream.Zip(); doc.Internals.AddObject(form);
        return form;
    }

    /// <summary>
    /// Shape (D11): the even-odd fill with its own opacity, then the border with its own opacity, round joins and caps
    /// and the line dash pattern — the same order and outline as the screen painter (<see cref="ShapeGeometry"/>).
    /// The ellipse is written as four cubic arcs.
    /// </summary>
    private static PdfDictionary CreateShape(PdfDocument doc, PdfPageBox box, ShapeAnnotation shape, out double[] rect, out string hash)
    {
        var toUser = shape.Transform.Then(box.CanonicalToPdfUser);
        rect = UserBounds(toUser, ShapeGeometry.Bounds(shape), 1);
        var style = shape.Style;
        var path = new StringBuilder();
        if (ShapeGeometry.Vertices(shape) is { } vertices)
        {
            for (int i = 0; i < vertices.Length; i++) path.Append(N(vertices[i].X)).Append(' ').Append(N(vertices[i].Y)).Append(i == 0 ? " m\n" : " l\n");
        }
        else
        {
            var e = ShapeGeometry.EllipseCubics(shape.Box);
            path.Append(N(e[0].X)).Append(' ').Append(N(e[0].Y)).Append(" m\n");
            for (int i = 1; i < e.Length; i += 3) path.Append(Numbers([e[i].X, e[i].Y, e[i + 1].X, e[i + 1].Y, e[i + 2].X, e[i + 2].Y])).Append(" c\n");
        }
        if (shape.IsClosed) path.Append("h\n");
        var states = new PdfDictionary(doc);
        var code = new StringBuilder("q ").Append(Numbers(toUser.ToArray())).Append(" cm\n");
        if (ShapeGeometry.HasFill(shape))
        {
            var f = style.FillColor;
            states.Elements["/GF"] = AlphaState(doc, style.FillOpacity);
            code.Append("/GF gs ").Append($"{N(f.R / 255d)} {N(f.G / 255d)} {N(f.B / 255d)} rg\n").Append(path).Append("f*\n");
        }
        if (style.BorderEnabled)
        {
            var c = style.BorderColor;
            states.Elements["/GB"] = AlphaState(doc, style.BorderOpacity);
            code.Append("/GB gs ").Append($"{N(c.R / 255d)} {N(c.G / 255d)} {N(c.B / 255d)} RG ").Append(N(style.BorderWidthPt)).Append(" w 1 J 1 j [");
            code.Append(string.Join(' ', LineGeometry.DashPattern(style.BorderDash).Select(v => N(v * style.BorderWidthPt)))).Append("] 0 d\n").Append(path).Append("S\n");
        }
        code.Append("Q\n");
        var resources = new PdfDictionary(doc); resources.Elements["/ExtGState"] = states;
        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject"); form.Elements.SetName("/Subtype", "/Form"); form.Elements.SetInteger("/FormType", 1);
        form.Elements["/BBox"] = new PdfArray(doc, rect.Select(v => (PdfItem)Real(v)).ToArray());
        form.Elements["/Resources"] = resources; form.Elements.SetBoolean("/PdfNotesShape", true);
        form.CreateStream(Encoding.ASCII.GetBytes(code.ToString()));
        hash = Hash(form); form.Stream.Zip(); doc.Internals.AddObject(form);
        return form;
    }

    private static PdfDictionary AlphaState(PdfDocument doc, double opacity)
    {
        var state = new PdfDictionary(doc);
        state.Elements.SetName("/Type", "/ExtGState");
        state.Elements["/ca"] = Real(opacity); state.Elements["/CA"] = Real(opacity);
        return state;
    }

    /// <summary>
    /// PDF glyph highlight (spec 9.2–9.3): every painted line quad in one nonzero fill, so overlapping lines are
    /// composited once with the highlight opacity, exactly like the screen painter. Normal blending (D04).
    /// </summary>
    private static PdfDictionary CreateTextHighlight(PdfDocument doc, PdfPageBox box, PdfTextHighlight highlight, out double[] rect, out string hash)
    {
        var toUser = box.CanonicalToPdfUser;
        rect = UserBounds(toUser, AnnotationGeometry.LocalBounds(highlight), 1);
        var c = highlight.Color;
        var code = new StringBuilder();
        code.Append("q ").Append(Numbers(toUser.ToArray())).Append(" cm /GS0 gs\n");
        code.Append($"{N(c.R / 255d)} {N(c.G / 255d)} {N(c.B / 255d)} rg\n");
        foreach (var quad in TextSelection.PaintedQuads(highlight))
        {
            var ring = TextSelection.Ring(quad);
            code.Append(N(ring[0].X)).Append(' ').Append(N(ring[0].Y)).Append(" m ");
            for (int i = 1; i < 4; i++) code.Append(N(ring[i].X)).Append(' ').Append(N(ring[i].Y)).Append(" l ");
            code.Append("h\n");
        }
        code.Append("f\nQ\n");
        var state = new PdfDictionary(doc);
        state.Elements.SetName("/Type", "/ExtGState");
        state.Elements["/CA"] = new PdfLiteral(N(highlight.Opacity));
        state.Elements["/ca"] = new PdfLiteral(N(highlight.Opacity));
        var states = new PdfDictionary(doc); states.Elements["/GS0"] = state;
        var resources = new PdfDictionary(doc); resources.Elements["/ExtGState"] = states;
        byte[] content = Encoding.ASCII.GetBytes(code.ToString());
        hash = Hash(content, rect, highlight.Opacity, highlight.Opacity, "", matrix: false);
        var form = new PdfDictionary(doc);
        form.CreateStream(content);
        form.Elements.SetName("/Type", "/XObject"); form.Elements.SetName("/Subtype", "/Form"); form.Elements.SetInteger("/FormType", 1);
        form.Elements["/BBox"] = Array(doc, rect);
        form.Elements["/Resources"] = resources;
        form.Stream.Zip();
        doc.Internals.AddObject(form);
        return form;
    }

    /// <summary>Underline/strikeout bands (spec 24.8): one nonzero fill composited once with the opacity, like the screen.</summary>
    private static PdfDictionary CreateFill(PdfDocument doc, PdfPageBox box, IEnumerable<Quad> quads, RgbColor c, double opacity, out double[] rect, out string hash)
    {
        var toUser = box.CanonicalToPdfUser;
        var all = quads.ToArray();
        rect = UserBounds(toUser, TextSelection.Bounds(all), 1);
        var code = new StringBuilder();
        code.Append("q ").Append(Numbers(toUser.ToArray())).Append(" cm /GS0 gs\n");
        code.Append($"{N(c.R / 255d)} {N(c.G / 255d)} {N(c.B / 255d)} rg\n");
        foreach (var quad in all)
        {
            var ring = TextSelection.Ring(quad);
            code.Append(N(ring[0].X)).Append(' ').Append(N(ring[0].Y)).Append(" m ");
            for (int i = 1; i < 4; i++) code.Append(N(ring[i].X)).Append(' ').Append(N(ring[i].Y)).Append(" l ");
            code.Append("h\n");
        }
        code.Append("f\nQ\n");
        return Form(doc, code, rect, opacity, out hash);
    }

    /// <summary>
    /// Sticky note icon (spec 24.9): a rounded square in the note color with a darker edge and three text lines, 20 pt, drawn in
    /// canonical points through the note's translation. The text itself is the annotation's /Contents.
    /// </summary>
    private static PdfDictionary CreateNote(PdfDocument doc, PdfPageBox box, StickyNote note, out double[] rect, out string hash)
    {
        var toUser = note.Transform.Then(box.CanonicalToPdfUser);
        const double s = StickyNote.IconPt;
        rect = UserBounds(toUser, new Rect2(0, 0, s, s), 0.5);
        var c = note.Color;
        string fill = $"{N(c.R / 255d)} {N(c.G / 255d)} {N(c.B / 255d)}", edge = $"{N(c.R / 510d)} {N(c.G / 510d)} {N(c.B / 510d)}";
        var code = new StringBuilder();
        code.Append("q ").Append(Numbers(toUser.ToArray())).Append(" cm /GS0 gs\n");
        code.Append(fill).Append(" rg ").Append(edge).Append(" RG 1 w 1 j\n");
        code.Append(NoteOutline(s)).Append("B\n");
        code.Append(edge).Append(" RG 1.2 w 1 J\n");
        foreach (double y in new[] { 6.5, 10.0, 13.5 }) code.Append($"5 {N(y)} m {N(y < 13 ? 15 : 11)} {N(y)} l S\n");
        code.Append("Q\n");
        return Form(doc, code, rect, 1, out hash);
    }

    /// <summary>The icon's rounded square (radius 3) as a closed path in local points (y down).</summary>
    internal static string NoteOutline(double s)
    {
        const double r = 3, k = r * 0.5523;
        return $"{N(r)} 0 m {N(s - r)} 0 l {N(s - r + k)} 0 {N(s)} {N(r - k)} {N(s)} {N(r)} c {N(s)} {N(s - r)} l {N(s)} {N(s - r + k)} {N(s - r + k)} {N(s)} {N(s - r)} {N(s)} c " +
               $"{N(r)} {N(s)} l {N(r - k)} {N(s)} 0 {N(s - r + k)} 0 {N(s - r)} c 0 {N(r)} l 0 {N(r - k)} {N(r - k)} 0 {N(r)} 0 c h ";
    }

    private static PdfDictionary Form(PdfDocument doc, StringBuilder code, double[] rect, double opacity, out string hash)
    {
        var state = new PdfDictionary(doc);
        state.Elements.SetName("/Type", "/ExtGState");
        state.Elements["/CA"] = new PdfLiteral(N(opacity));
        state.Elements["/ca"] = new PdfLiteral(N(opacity));
        var states = new PdfDictionary(doc); states.Elements["/GS0"] = state;
        var resources = new PdfDictionary(doc); resources.Elements["/ExtGState"] = states;
        byte[] content = Encoding.ASCII.GetBytes(code.ToString());
        hash = Hash(content, rect, opacity, opacity, "", matrix: false);
        var form = new PdfDictionary(doc);
        form.CreateStream(content);
        form.Elements.SetName("/Type", "/XObject"); form.Elements.SetName("/Subtype", "/Form"); form.Elements.SetInteger("/FormType", 1);
        form.Elements["/BBox"] = Array(doc, rect);
        form.Elements["/Resources"] = resources;
        form.Stream.Zip();
        doc.Internals.AddObject(form);
        return form;
    }

    private static string ImageHash(PdfDictionary image)
    {
        var text = new StringBuilder(Convert.ToHexString(SHA256.HashData(image.Stream?.Value ?? [])));
        foreach (string key in new[] { "/Width", "/Height", "/BitsPerComponent", "/ColorSpace", "/Filter", "/Decode", "/DecodeParms", "/Mask", "/Interpolate" })
            text.Append('|').Append(key).Append('=').Append(ItemHash(image.Elements[key]));
        if (image.Elements.GetDictionary("/SMask") is { } mask) text.Append('|').Append(ImageHash(mask));
        return text.ToString();
    }

    private static string ItemHash(PdfItem? item, int depth = 0)
    {
        if (depth > 12) throw new InvalidDataException("Image resources are too deeply nested.");
        if (item is PdfSharp.Pdf.Advanced.PdfReference reference) return ItemHash(reference.Value, depth + 1);
        if (item is PdfArray array) return "[" + string.Join(",", array.Elements.Select(v => ItemHash(v, depth + 1))) + "]";
        if (item is PdfDictionary dict)
        {
            string entries = string.Join(";", dict.Elements.Keys.Where(k => k != "/Length").OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => k + "=" + ItemHash(dict.Elements[k], depth + 1)));
            return entries + (dict.Stream is null ? "" : Convert.ToHexString(SHA256.HashData(dict.Stream.Value)));
        }
        return item?.ToString() ?? "";
    }

    private static PdfDictionary CreateImage(PdfDocument doc, PdfPageBox box, ImageAnnotation image, PdfDictionary raster, out double[] rect, out string hash)
    {
        var m = image.Transform.Then(box.CanonicalToPdfUser);
        var b = image.LocalBounds;
        rect = UserBounds(m, b, 1);
        var form = new PdfDictionary(doc);
        form.CreateStream(Encoding.ASCII.GetBytes($"q {Numbers(m.ToArray())} cm {N(b.Width)} 0 0 {N(-b.Height)} {N(b.X)} {N(b.Y + b.Height)} cm /Image Do Q\n"));
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements.SetInteger("/FormType", 1);
        form.Elements["/BBox"] = Array(doc, rect);
        var xobjects = new PdfDictionary(doc);
        xobjects.Elements["/Image"] = raster.Reference!;
        var resources = new PdfDictionary(doc);
        resources.Elements["/XObject"] = xobjects;
        form.Elements["/Resources"] = resources;
        string basic = Hash(form.Stream.UnfilteredValue, rect, -1, -1, "", false);
        hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basic + ImageHash(raster))));
        form.Stream.Zip();
        doc.Internals.AddObject(form);
        return form;
    }

    private static PdfDictionary CreateText(PdfDocument doc, PdfPageBox box, TextBoxAnnotation text, out double[] rect, out string hash)
    {
        var layout = PdfNotes.Text.TextLayout.Measure(text);
        var toUser = text.Transform.Then(box.CanonicalToPdfUser);
        rect = UserBounds(toUser, new(0, 0, text.WidthPt, layout.Height), 1);
        var states = new PdfDictionary(doc);
        var code = new StringBuilder("q " + Numbers(toUser.ToArray()) + " cm\n");
        TextPaths(layout.Paint, code, doc, states, 1);
        code.Append("Q\n");
        var resources = new PdfDictionary(doc); resources.Elements["/ExtGState"] = states;
        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject"); form.Elements.SetName("/Subtype", "/Form"); form.Elements.SetInteger("/FormType", 1);
        form.Elements["/BBox"] = new PdfArray(doc, rect.Select(v => (PdfItem)Real(v)).ToArray());
        form.Elements["/Resources"] = resources; form.Elements.SetBoolean("/PdfNotesText", true);
        form.CreateStream(Encoding.ASCII.GetBytes(code.ToString()));
        hash = Hash(form); form.Stream.Zip(); doc.Internals.AddObject(form); return form;
    }
    private static void TextPaths(PdfNotes.Text.TextPaint paint, StringBuilder code, PdfDocument doc, PdfDictionary states, double opacity)
    {
        if (paint is PdfNotes.Text.TextPaintGroup group)
        {
            code.Append("q ").Append(Numbers(group.Transform.ToArray())).Append(" cm\n");
            if (group.Clip is { } clip) { Path(clip, code); code.Append("W n\n"); }
            foreach (var child in group.Children) TextPaths(child, code, doc, states, opacity * group.Opacity);
            code.Append("Q\n"); return;
        }
        var shape = (PdfNotes.Text.TextPaintShape)paint;
        if (shape.Fill is { } fill)
        {
            Paint(fill, opacity, false, code, doc, states); Path(shape.Figures, code);
            code.Append(shape.EvenOdd ? "f*\n" : "f\n");
        }
        if (shape.Stroke is { } stroke)
        {
            Paint(stroke, opacity, true, code, doc, states); code.Append(N(shape.StrokeWidth)).Append(" w\n");
            Path(shape.Figures, code); code.Append("S\n");
        }
    }
    private static void Paint(PdfNotes.Text.TextColor color, double opacity, bool stroke, StringBuilder code, PdfDocument doc, PdfDictionary states)
    {
        string key = "/T" + states.Elements.Count;
        var state = new PdfDictionary(doc); state.Elements.SetName("/Type", "/ExtGState");
        state.Elements["/ca"] = Real(color.Alpha * opacity); state.Elements["/CA"] = Real(color.Alpha * opacity);
        states.Elements[key] = state;
        code.Append(key).Append(" gs ").Append(N(color.R / 255d)).Append(' ').Append(N(color.G / 255d)).Append(' ').Append(N(color.B / 255d)).Append(stroke ? " RG\n" : " rg\n");
    }
    /// <summary>D01 policy: 0.02pt outline flattening (done by the text engine). No font binaries, same shaped glyphs as the editor.</summary>
    private static void Path(IEnumerable<PdfNotes.Text.TextFigure> figures, StringBuilder code)
    {
        foreach (var f in figures)
        {
            var p = f.Points[0]; code.Append(N(p.X)).Append(' ').Append(N(p.Y)).Append(" m\n");
            for (int i = 1; i < f.Points.Length; i++) { p = f.Points[i]; code.Append(N(p.X)).Append(' ').Append(N(p.Y)).Append(" l\n"); }
            if (f.Closed) code.Append("h\n");
        }
    }

    private static string Hash(byte[] content, IReadOnlyList<double> bbox, double strokeAlpha, double fillAlpha, string blend, bool matrix)
    {
        var text = new StringBuilder(Convert.ToHexString(SHA256.HashData(content)));
        foreach (double v in bbox) text.Append(',').Append(N(v));
        text.Append('|').Append(N(strokeAlpha)).Append('|').Append(N(fillAlpha)).Append('|').Append(blend).Append('|').Append(matrix);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// A real already rounded to the 6 decimals of <see cref="N"/>. PDFsharp writes reals with 7 decimals; rounding a
    /// full-precision value twice (7, then 6 when hashing the parsed file) could change the 6th digit and make our
    /// own appearance look changed after a save/read round trip.
    /// </summary>
    internal static PdfReal Real(double value) => new(Math.Round(value, 6));

    /// <summary>Numbers are written as literals in our own format so their parsed value hashes identically.</summary>
    internal static PdfArray Array(PdfDocument document, IEnumerable<double> values) =>
        new(document, values.Select(v => (PdfItem)new PdfLiteral(N(v))).ToArray());

    private static double[] UserBounds(Affine toUser, Rect2 local, double margin)
    {
        Point2[] corners =
        [
            toUser.Transform(new(local.X, local.Y)), toUser.Transform(new(local.X + local.Width, local.Y)),
            toUser.Transform(new(local.X, local.Y + local.Height)), toUser.Transform(new(local.X + local.Width, local.Y + local.Height))
        ];
        return [corners.Min(p => p.X) - margin, corners.Min(p => p.Y) - margin, corners.Max(p => p.X) + margin, corners.Max(p => p.Y) + margin];
    }

    /// <summary>Circle as four cubic Bézier arcs.</summary>
    private static void AppendCircle(StringBuilder code, Point2 center, double r)
    {
        const double k = 0.5522847498307936;
        double x = center.X, y = center.Y, d = k * r;
        code.Append($"{N(x + r)} {N(y)} m\n");
        code.Append($"{N(x + r)} {N(y + d)} {N(x + d)} {N(y + r)} {N(x)} {N(y + r)} c\n");
        code.Append($"{N(x - d)} {N(y + r)} {N(x - r)} {N(y + d)} {N(x - r)} {N(y)} c\n");
        code.Append($"{N(x - r)} {N(y - d)} {N(x - d)} {N(y - r)} {N(x)} {N(y - r)} c\n");
        code.Append($"{N(x + d)} {N(y - r)} {N(x + r)} {N(y - d)} {N(x + r)} {N(y)} c\n");
    }

    private static string Numbers(double[] values) => string.Join(' ', values.Select(N));

    /// <summary>PDF number: invariant, no exponent, 6 decimals (well below a device pixel at 800%).</summary>
    internal static string N(double value)
    {
        string text = Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }
}
