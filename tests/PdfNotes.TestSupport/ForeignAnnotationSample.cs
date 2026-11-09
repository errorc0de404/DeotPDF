using System.Globalization;
using PdfSharp.Pdf;

namespace PdfNotes.Pdf.Prototype;

/// <summary>
/// Validation sample (D11, spec 14.6): the generated text PDF plus standard annotations as other apps write them —
/// Ink (two paths, with a popup), Highlight, Line, FreeText, Square, Circle, Polygon, PolyLine and a locked Square on
/// page 1; a FreeText and a Square on the /Rotate 90 page 2 — and annotations that must stay read-only: a Stamp, a
/// hidden Ink and a sticky note (Text with popup). Every annotation has its own appearance stream.
/// </summary>
public static class ForeignAnnotationSample
{
    public const int ConvertiblePdfAnnotations = 12; // the sticky note converts too since D13i (spec 24.9)

    public static void Create(string path)
    {
        ProbeSamplePdf.Create(path, 2);
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        var first = doc.Pages[0];
        Add(doc, first, Annot(doc, "/Ink", [60, 560, 260, 640], "1 0 0 RG 3 w 70 600 m 150 630 l 250 570 l S 80 570 m 200 580 l S",
            ("/C", Arr(doc, 1, 0, 0)), ("/CA", new PdfReal(.8)), ("/BS", Dict(doc, ("/W", new PdfReal(3)))),
            ("/InkList", new PdfArray(doc, Arr(doc, 70, 600, 150, 630, 250, 570), Arr(doc, 80, 570, 200, 580))), ("/NM", new PdfString("foreign-ink"))), popup: true);
        Add(doc, first, Annot(doc, "/Highlight", [68, 662, 330, 684], "1 1 0 rg 68 664 262 20 re f",
            ("/C", Arr(doc, 1, 1, 0)), ("/CA", new PdfReal(.5)), ("/QuadPoints", Arr(doc, 68, 684, 330, 684, 68, 664, 330, 664))));
        Add(doc, first, Annot(doc, "/Line", [300, 500, 520, 560], "0 0 1 RG 2 w [4 2] 0 d 310 510 m 510 550 l S",
            ("/C", Arr(doc, 0, 0, 1)), ("/L", Arr(doc, 310, 510, 510, 550)), ("/LE", new PdfArray(doc, new PdfName("/None"), new PdfName("/OpenArrow"))),
            ("/BS", Dict(doc, ("/W", new PdfReal(2)), ("/S", new PdfName("/D")), ("/D", Arr(doc, 4, 2))))));
        Add(doc, first, Annot(doc, "/FreeText", [320, 600, 520, 640], "BT /F0 14 Tf 0 0 1 rg 324 616 Td (Foreign note) Tj ET",
            ("/Contents", new PdfString("Foreign note")), ("/DA", new PdfString("0 0 1 rg /Helv 14 Tf")), ("/Q", new PdfInteger(0))));
        Add(doc, first, Annot(doc, "/Square", [60, 380, 200, 460], "0 .5 0 RG .8 1 .8 rg 2 w 61 381 138 78 re B",
            ("/C", Arr(doc, 0, .5, 0)), ("/IC", Arr(doc, .8, 1, .8)), ("/BS", Dict(doc, ("/W", new PdfReal(2))))));
        Add(doc, first, Annot(doc, "/Circle", [240, 380, 340, 460], ".5 0 .5 RG 1 w 290 420 m 290 420 l S",
            ("/C", Arr(doc, .5, 0, .5))));
        Add(doc, first, Annot(doc, "/Polygon", [380, 360, 540, 470], "1 .5 0 RG 1 .9 .7 rg 390 370 m 530 380 l 470 460 l h B",
            ("/C", Arr(doc, 1, .5, 0)), ("/IC", Arr(doc, 1, .9, .7)), ("/Vertices", Arr(doc, 390, 370, 530, 380, 470, 460))));
        Add(doc, first, Annot(doc, "/PolyLine", [60, 300, 300, 350], ".2 .2 .2 RG 70 310 m 150 340 l 220 310 l 290 340 l S",
            ("/C", Arr(doc, .2, .2, .2)), ("/Vertices", Arr(doc, 70, 310, 150, 340, 220, 310, 290, 340))));
        Add(doc, first, Annot(doc, "/Square", [400, 250, 500, 300], "0 0 0 RG 1 w 400.5 250.5 99 49 re S",
            ("/C", Arr(doc, 0, 0, 0)), ("/F", new PdfInteger(4 | 128))));
        // Read-only: other kinds, hidden, sticky note with its popup.
        Add(doc, first, Annot(doc, "/Stamp", [440, 120, 520, 160], "1 0 0 rg 440 120 80 40 re f"));
        Add(doc, first, Annot(doc, "/Ink", [60, 200, 160, 240], "0 1 0 RG 70 210 m 150 230 l S",
            ("/C", Arr(doc, 0, 1, 0)), ("/InkList", new PdfArray(doc, Arr(doc, 70, 210, 150, 230))), ("/F", new PdfInteger(4 | 2))));
        Add(doc, first, Annot(doc, "/Text", [540, 700, 560, 720], "1 1 0 rg 540 700 20 20 re f", ("/Contents", new PdfString("note"))), popup: true);

        var second = doc.Pages[1]; // /Rotate 90, CropBox (40 50 590 740), UserUnit 2
        Add(doc, second, Annot(doc, "/FreeText", [100, 500, 300, 540], "BT /F0 14 Tf 0 0 0 rg 104 516 Td (Turned note) Tj ET",
            ("/Contents", new PdfString("Turned note")), ("/DA", new PdfString("0 g /Helv 14 Tf"))));
        Add(doc, second, Annot(doc, "/Square", [100, 300, 260, 380], "1 0 0 RG 2 w 101 301 158 78 re S",
            ("/C", Arr(doc, 1, 0, 0)), ("/BS", Dict(doc, ("/W", new PdfReal(2))))));
        doc.Save(path);
    }

    private static PdfArray Arr(PdfDocument doc, params double[] values) => new(doc, values.Select(v => (PdfItem)new PdfReal(v)).ToArray());

    private static PdfDictionary Dict(PdfDocument doc, params (string Key, PdfItem Value)[] entries)
    {
        var d = new PdfDictionary(doc);
        foreach (var (k, v) in entries) d.Elements[k] = v;
        return d;
    }

    private static PdfDictionary Annot(PdfDocument doc, string subtype, double[] rect, string appearance, params (string Key, PdfItem Value)[] entries)
    {
        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject"); form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = Arr(doc, rect);
        var font = new PdfDictionary(doc);
        font.Elements.SetName("/Type", "/Font"); font.Elements.SetName("/Subtype", "/Type1"); font.Elements.SetName("/BaseFont", "/Helvetica");
        var fonts = new PdfDictionary(doc); fonts.Elements["/F0"] = font;
        var resources = new PdfDictionary(doc); resources.Elements["/Font"] = fonts;
        form.Elements["/Resources"] = resources;
        form.CreateStream(System.Text.Encoding.ASCII.GetBytes(appearance + "\n"));
        doc.Internals.AddObject(form);
        var ap = new PdfDictionary(doc); ap.Elements["/N"] = form.Reference!;
        var a = new PdfDictionary(doc);
        a.Elements.SetName("/Type", "/Annot"); a.Elements.SetName("/Subtype", subtype);
        a.Elements["/Rect"] = Arr(doc, rect);
        a.Elements.SetInteger("/F", 4);
        a.Elements["/AP"] = ap;
        a.Elements["/M"] = new PdfString("D:20261013120000+09'00'");
        foreach (var (k, v) in entries) a.Elements[k] = v;
        doc.Internals.AddObject(a);
        return a;
    }

    private static void Add(PdfDocument doc, PdfPage page, PdfDictionary annot, bool popup = false)
    {
        var annots = page.Elements.GetArray("/Annots") ?? new PdfArray(doc);
        if (!page.Elements.ContainsKey("/Annots")) page.Elements["/Annots"] = annots;
        annots.Elements.Add(annot.Reference!);
        if (!popup) return;
        var rect = annot.Elements.GetArray("/Rect")!;
        var p = new PdfDictionary(doc);
        p.Elements.SetName("/Type", "/Annot"); p.Elements.SetName("/Subtype", "/Popup");
        p.Elements["/Rect"] = new PdfArray(doc, new PdfReal(rect.Elements.GetReal(2)), new PdfReal(rect.Elements.GetReal(1)),
            new PdfReal(rect.Elements.GetReal(2) + 120), new PdfReal(rect.Elements.GetReal(1) + 60));
        p.Elements["/Parent"] = annot.Reference!;
        doc.Internals.AddObject(p);
        annot.Elements["/Popup"] = p.Reference!;
        annots.Elements.Add(p.Reference!);
    }

    internal static string N(double v) => v.ToString(CultureInfo.InvariantCulture);
}
