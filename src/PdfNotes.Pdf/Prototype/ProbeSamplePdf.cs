using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace PdfNotes.Pdf.Prototype;

public static class ProbeSamplePdf
{
    public static void Create(string path, int pageCount = 3)
    {
        if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = new SampleFontResolver();
        using var doc = new PdfDocument();
        doc.Info.Title = "PdfNotes D01 generated validation sample";
        var font = new XFont("D01-Malgun", 18, XFontStyleEx.Regular,
            new XPdfFontOptions(PdfFontEncoding.Unicode));
        for (int i = 0; i < pageCount; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
            using (var gfx = XGraphics.FromPdfPage(page))
            {
                gfx.DrawString($"원본 본문 / Original PDF - Page {i + 1}", font, XBrushes.Black, new XPoint(70, 120));
                gfx.DrawString("복사 가능한 한글과 English 123", font, XBrushes.Black, new XPoint(70, 154));
                gfx.DrawRectangle(new XPen(XColors.SteelBlue, 1), 70, 180, 220, 48);
                gfx.DrawLine(new XPen(XColors.LightGray, 1), 60, 280, 500, 280);
            }
            if (i == 1)
            {
                page.CropBox = new PdfRectangle(new XPoint(40, 50), new XPoint(590, 740));
                page.Rotate = 90;
                page.Elements.SetReal("/UserUnit", 2);
            }
            else if (i == 2)
            {
                page.CropBox = new PdfRectangle(new XPoint(20, 30), new XPoint(600, 760));
                page.Rotate = 270;
            }
            // Foreign annotation with its own standard appearance must remain visible.
            var form = new PdfDictionary(doc);
            form.Elements.SetName("/Type", "/XObject");
            form.Elements.SetName("/Subtype", "/Form");
            form.Elements["/BBox"] = new PdfArray(doc, new PdfReal(0), new PdfReal(0), new PdfReal(18), new PdfReal(18));
            form.CreateStream(System.Text.Encoding.ASCII.GetBytes("1 .6 0 rg 0 0 18 18 re f\n"));
            doc.Internals.AddObject(form);
            var ap = new PdfDictionary(doc);
            ap.Elements["/N"] = form.Reference!;
            var annotation = new PdfDictionary(doc);
            annotation.Elements.SetName("/Type", "/Annot");
            annotation.Elements.SetName("/Subtype", "/Stamp");
            annotation.Elements.SetString("/NM", "Foreign-D01");
            annotation.Elements.SetInteger("/F", 4);
            annotation.Elements["/Rect"] = new PdfArray(doc, new PdfReal(80), new PdfReal(520), new PdfReal(98), new PdfReal(538));
            annotation.Elements["/AP"] = ap;
            doc.Internals.AddObject(annotation);
            page.Elements["/Annots"] = new PdfArray(doc, annotation.Reference!);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        doc.Save(path);
    }

    /// <summary>One page description of <see cref="CreateMixed"/>: media size, crop box (null = media), /Rotate, /UserUnit.</summary>
    public sealed record MixedPage(double Width, double Height, PdfRectangle? Crop, int Rotate, double UserUnit = 1);

    /// <summary>D03 view validation sample: different sizes, all four rotations, CropBox offsets and a UserUnit page.</summary>
    public static readonly MixedPage[] MixedPages =
    [
        new(595.28, 841.89, null, 0),
        new(612, 792, new PdfRectangle(new XPoint(36, 48), new XPoint(590, 760)), 90),
        new(419.53, 595.28, null, 180),
        new(841.89, 595.28, new PdfRectangle(new XPoint(30, 20), new XPoint(800, 580)), 270),
        new(600, 800, new PdfRectangle(new XPoint(50, 40), new XPoint(550, 760)), 0, 1.5),
        new(1000, 1400, null, 0),
    ];

    public static void CreateMixed(string path, int repeat = 1)
    {
        if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = new SampleFontResolver();
        using var doc = new PdfDocument();
        doc.Info.Title = "PdfNotes D03 generated view sample";
        var font = new XFont("D01-Malgun", 16, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
        int number = 0;
        for (int r = 0; r < repeat; r++)
        foreach (var spec in MixedPages)
        {
            number++;
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(spec.Width);
            page.Height = XUnit.FromPoint(spec.Height);
            using (var gfx = XGraphics.FromPdfPage(page))
            {
                double left = (spec.Crop?.X1 ?? 0) + 40, top = spec.Height - (spec.Crop?.Y2 ?? spec.Height) + 60;
                gfx.DrawString($"Page {number} 페이지 · Rotate {spec.Rotate}", font, XBrushes.Black, new XPoint(left, top));
                gfx.DrawString("한글 English 0123456789", font, XBrushes.Black, new XPoint(left, top + 30));
                gfx.DrawRectangle(new XPen(XColors.SteelBlue, 2), left, top + 50, 200, 80);
            }
            if (spec.Crop is { } crop) page.CropBox = crop;
            if (spec.Rotate != 0) page.Rotate = spec.Rotate;
            if (spec.UserUnit != 1) page.Elements.SetReal("/UserUnit", spec.UserUnit);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        doc.Save(path);
    }

    /// <summary>Areas of the links on page 1 of <see cref="CreateLinked"/> in PDF user space (left, bottom, right, top).</summary>
    public static readonly (string Name, double Left, double Bottom, double Right, double Top)[] LinkAreas =
    [
        ("page3", 70, 640, 270, 664), ("page5", 70, 600, 270, 624), ("web", 70, 560, 270, 584), ("mail", 70, 520, 270, 544), ("script", 70, 480, 270, 504),
    ];

    /// <summary>Destination of the "page3" link: page 3 at user-space y 300 (a point below the top).</summary>
    public const double LinkTargetY = 300;

    public const string LinkUrl = "https://example.com/pdfnotes";

    /// <summary>
    /// D13h sample (spec 24.3–24.4): <paramref name="pageCount"/> letter pages; page 1 holds links to page 3 (/XYZ at
    /// <see cref="LinkTargetY"/>), page 5, a web address, a mail address and a javascript: URI (never opened); the outline is
    /// 1장 → page 1 (child 1.1 → page 2), 2장 → page 4 (child 2.1 → page 5).
    /// </summary>
    public static void CreateLinked(string path, int pageCount = 5)
    {
        if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = new SampleFontResolver();
        using var doc = new PdfDocument();
        doc.Info.Title = "PdfNotes D13h link sample";
        var font = new XFont("D01-Malgun", 16, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
        for (int i = 0; i < pageCount; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"Page {i + 1} 페이지", font, XBrushes.Black, new XPoint(70, 80));
            gfx.DrawString($"Target line {i + 1}", font, XBrushes.Black, new XPoint(70, 792 - LinkTargetY + 14));
            if (i == 0)
                foreach (var (name, l, b, r, t) in LinkAreas)
                    gfx.DrawString("→ " + name, font, XBrushes.Blue, new XPoint(l + 4, 792 - b - 6));
        }
        PdfDictionary Link((string, double Left, double Bottom, double Right, double Top) area)
        {
            var link = new PdfDictionary(doc);
            link.Elements.SetName("/Type", "/Annot");
            link.Elements.SetName("/Subtype", "/Link");
            link.Elements["/Rect"] = new PdfArray(doc, new PdfReal(area.Left), new PdfReal(area.Bottom), new PdfReal(area.Right), new PdfReal(area.Top));
            link.Elements["/Border"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));
            doc.Internals.AddObject(link);
            return link;
        }
        PdfDictionary Uri(string uri)
        {
            var action = new PdfDictionary(doc);
            action.Elements.SetName("/S", "/URI");
            action.Elements.SetString("/URI", uri);
            return action;
        }
        var links = new PdfArray(doc);
        var toPage3 = Link(LinkAreas[0]);
        toPage3.Elements["/Dest"] = new PdfArray(doc, doc.Pages[Math.Min(2, pageCount - 1)].Reference!, new PdfName("/XYZ"), new PdfReal(0), new PdfReal(LinkTargetY), PdfNull.Value);
        var toPage5 = Link(LinkAreas[1]);
        toPage5.Elements["/Dest"] = new PdfArray(doc, doc.Pages[pageCount - 1].Reference!, new PdfName("/Fit"));
        var web = Link(LinkAreas[2]); web.Elements["/A"] = Uri(LinkUrl);
        var mail = Link(LinkAreas[3]); mail.Elements["/A"] = Uri("mailto:notes@example.com");
        var script = Link(LinkAreas[4]); script.Elements["/A"] = Uri("javascript:alert(1)");
        foreach (var link in new[] { toPage3, toPage5, web, mail, script }) links.Elements.Add(link.Reference!);
        doc.Pages[0].Elements["/Annots"] = links;

        var first = doc.Outlines.Add("1장 시작", doc.Pages[0], true);
        first.Outlines.Add("1.1 둘째 쪽", doc.Pages[Math.Min(1, pageCount - 1)]);
        var second = doc.Outlines.Add("2장", doc.Pages[Math.Min(3, pageCount - 1)], true);
        second.Outlines.Add("2.1 마지막 쪽", doc.Pages[pageCount - 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        doc.Save(path);
    }

    /// <summary>Copy of <paramref name="source"/> protected with a user password (cannot be opened without it).</summary>
    public static void CreatePasswordProtected(string source, string path) => Protect(source, path, "user-secret", "owner-secret");

    /// <summary>Copy of <paramref name="source"/> encrypted with only an owner password (opens without a password).</summary>
    public static void CreateOwnerEncrypted(string source, string path) => Protect(source, path, null, "owner-secret");

    private static void Protect(string source, string path, string? user, string owner)
    {
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(source, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        if (user is not null) doc.SecuritySettings.UserPassword = user;
        doc.SecuritySettings.OwnerPassword = owner;
        doc.Save(path);
    }

    /// <summary>Copy of <paramref name="source"/> with a filled signature field (fake signature value, structure only).</summary>
    public static void CreateSigned(string source, string path)
    {
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(source, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        var signature = new PdfDictionary(doc);
        signature.Elements.SetName("/Type", "/Sig");
        signature.Elements.SetName("/Filter", "/Adobe.PPKLite");
        signature.Elements.SetName("/SubFilter", "/adbe.pkcs7.detached");
        signature.Elements["/ByteRange"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));
        signature.Elements["/Contents"] = new PdfString("0000");
        doc.Internals.AddObject(signature);
        var field = new PdfDictionary(doc);
        field.Elements.SetName("/FT", "/Sig");
        field.Elements.SetString("/T", "Signature1");
        field.Elements["/V"] = signature.Reference!;
        doc.Internals.AddObject(field);
        var form = new PdfDictionary(doc);
        form.Elements["/Fields"] = new PdfArray(doc, field.Reference!);
        form.Elements.SetInteger("/SigFlags", 3);
        doc.Internals.Catalog.Elements["/AcroForm"] = form;
        doc.Save(path);
    }


    /// <summary>One page of <see cref="CreateCli"/>: media size, crop box (null = media), /Rotate, /UserUnit, content kind.</summary>
    public sealed record CliPage(double Width, double Height, PdfRectangle? Crop, int Rotate, double UserUnit, CliPageContent Content)
    {
        private double Left => Crop?.X1 ?? 0;
        private double Top => Crop?.Y2 ?? Height;

        /// <summary>Canonical size (spec 15): the visible crop box in points, before /Rotate.</summary>
        public double CanonicalWidth => ((Crop?.X2 ?? Width) - Left) * UserUnit;
        public double CanonicalHeight => (Top - (Crop?.Y1 ?? 0)) * UserUnit;

        /// <summary>Red registration crosses at known canonical points (asymmetric, so a wrong rotation or offset shows).</summary>
        public (double X, double Y)[] Fiducials => [(40, 50), (CanonicalWidth - 70, 80), (90, CanonicalHeight - 60)];

        /// <summary>A canonical point in the XGraphics space of the unrotated page (top-left of the media box, user units).</summary>
        internal XPoint ToGraphics(double x, double y) => new(Left + x / UserUnit, Height - Top + y / UserUnit);
    }

    public enum CliPageContent { Text, Lecture, Scan }

    /// <summary>The term that appears three times on the lecture page of <see cref="CreateCli"/> (title, body, caption).</summary>
    public const string CliRepeatedTerm = "베르누이 방정식"; // sample text, not UI text (D14e)

    /// <summary>
    /// CLI C0 sample: a lecture-like page with a repeated term, all four rotations, crop boxes with a non-zero origin,
    /// a media box larger than the crop box, a UserUnit page and an image-like page without a text layer. Every page
    /// carries red crosses at <see cref="CliPage.Fiducials"/>.
    /// </summary>
    public static readonly CliPage[] CliPages =
    [
        new(595.28, 841.89, null, 0, 1, CliPageContent.Lecture),
        new(612, 792, new PdfRectangle(new XPoint(36, 48), new XPoint(590, 760)), 90, 1, CliPageContent.Text),
        new(600, 800, new PdfRectangle(new XPoint(50, 40), new XPoint(550, 760)), 0, 1, CliPageContent.Text),
        new(841.89, 595.28, new PdfRectangle(new XPoint(30, 20), new XPoint(800, 580)), 270, 1, CliPageContent.Text),
        new(595.28, 841.89, null, 180, 1, CliPageContent.Scan),
        new(600, 800, new PdfRectangle(new XPoint(50, 40), new XPoint(550, 760)), 0, 1.5, CliPageContent.Text),
    ];

    public static void CreateCli(string path)
    {
        if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = new SampleFontResolver();
        using var doc = new PdfDocument();
        doc.Info.Title = "PdfNotes CLI generated sample";
        for (int n = 0; n < CliPages.Length; n++)
        {
            var spec = CliPages[n];
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(spec.Width);
            page.Height = XUnit.FromPoint(spec.Height);
            double u = spec.UserUnit;
            using (var gfx = XGraphics.FromPdfPage(page))
            {
                XFont Font(double size) => new("D01-Malgun", size / u, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
                void Text(string text, double size, double x, double y) => gfx.DrawString(text, Font(size), XBrushes.Black, spec.ToGraphics(x, y));
                switch (spec.Content)
                {
                    case CliPageContent.Lecture:
                        Text(CliRepeatedTerm, 24, 60, 120);
                        Text(CliRepeatedTerm + "은 유선을 따라 에너지 보존을 적용한 식이다.", 14, 60, 170); // sample text, not UI text (D14e)
                        Text("p + 0.5 rho v^2 + rho g z = const", 14, 60, 200);
                        gfx.DrawRectangle(new XPen(XColors.SteelBlue, 1.5 / u), new XRect(spec.ToGraphics(60, 230), new XSize(240 / u, 120 / u)));
                        Text("그림 1. " + CliRepeatedTerm + " 실험 장치", 11, 60, 375); // sample text, not UI text (D14e)
                        Text("English words and numbers 0123456789", 14, 60, 420);
                        break;
                    case CliPageContent.Text:
                        Text($"Page {n + 1} 페이지 · Rotate {spec.Rotate}", 16, 60, 130); // sample text, not UI text (D14e)
                        Text("한글 English 0123456789", 16, 60, 160); // sample text, not UI text (D14e)
                        break;
                    case CliPageContent.Scan:
                        // Scan-like page: dark bars where lines of text would be, a framed figure, and no text layer.
                        for (int line = 0; line < 8; line++)
                            gfx.DrawRectangle(XBrushes.DimGray, new XRect(spec.ToGraphics(60, 120 + line * 22), new XSize((380 - line % 3 * 60) / u, 10 / u)));
                        gfx.DrawRectangle(new XPen(XColors.Black, 2 / u), new XRect(spec.ToGraphics(60, 340), new XSize(260 / u, 160 / u)));
                        break;
                }
                var red = new XPen(XColor.FromArgb(255, 0, 0), 1.6 / u);
                foreach (var (x, y) in spec.Fiducials)
                {
                    gfx.DrawLine(red, spec.ToGraphics(x - 8, y), spec.ToGraphics(x + 8, y));
                    gfx.DrawLine(red, spec.ToGraphics(x, y - 8), spec.ToGraphics(x, y + 8));
                }
            }
            if (spec.Crop is { } crop) page.CropBox = crop;
            if (spec.Rotate != 0) page.Rotate = spec.Rotate;
            if (spec.UserUnit != 1) page.Elements.SetReal("/UserUnit", spec.UserUnit);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        doc.Save(path);
    }

    /// <summary>The lines of <see cref="CreateCliList"/>: text, left edge and baseline in canonical points (A4, 12 pt).</summary>
    public static readonly (string Text, double X, double Y)[] CliListLines =
    [
        ("Lecture notes on flow", 60, 100),
        ("A paragraph that runs over", 60, 140),
        ("two lines of plain text.", 60, 155),
        ("• First item of the list", 60, 195),
        ("continues on a second line", 72, 210),
        ("• Second item", 60, 230),
        ("- a nested point", 90, 250),
        ("1. Numbered step", 60, 290),
        ("2. Next step", 60, 310),
    ];

    /// <summary>CLI C2 layout sample: a title, a two-line paragraph, a bullet list with a nested point and numbered steps.</summary>
    public static void CreateCliList(string path)
    {
        if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = new SampleFontResolver();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(595.28);
        page.Height = XUnit.FromPoint(841.89);
        using (var gfx = XGraphics.FromPdfPage(page))
            foreach (var (text, x, y) in CliListLines)
                gfx.DrawString(text, new XFont("D01-Malgun", y == 100 ? 18 : 12, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode)),
                    XBrushes.Black, new XPoint(x, y));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        doc.Save(path);
    }

    // Sample generation only. Production appearance uses WPF outlines, not this resolver.
    private sealed class SampleFontResolver : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
            familyName == "D01-Malgun" ? new FontResolverInfo("malgun") : null;
        public byte[] GetFont(string faceName) => faceName == "malgun"
            ? File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "malgun.ttf"))
            : throw new InvalidOperationException("Unknown sample font.");
    }
}
