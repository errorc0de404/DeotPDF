using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.App.Prototype;

public static class ProbeFactory
{
    public static string[] MissingFonts(ProbeModel model)
    {
        var installed=Fonts.SystemFontFamilies.Select(f=>f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return model.Pages.SelectMany(p=>p.Annotations).SelectMany(a=>a.TextRuns??[])
            .Select(r=>r.Font).Where(font=>!installed.Contains(font)).Distinct().ToArray();
    }

    public static ProbeModel Create(string source)
    {
        var metrics = ProbePdfStore.GetMetrics(source);
        using var reader = new PdfiumDocument(source);
        var pages = new List<ProbePage>();
        foreach (var (page, index) in metrics.Select((value, i) => (value, i)))
        {
            var items = new List<ProbeAnnotation>();
            ProbeTextRun[] runs = [new("한글 ", "Malgun Gothic", 22, new(28, 70, 180), Bold:true),
                new("노트 ", "Malgun Gothic", 22, new(140, 36, 140), Italic:true, Underline:true),
                new("/ PDF Notes", "Malgun Gothic", 18, new(30, 110, 70), Strike:true)];
            var textPaths = new List<ProbePath>();
            double x = 70;
            foreach (var run in runs)
            {
                var typeface = new Typeface(new FontFamily(run.Font), run.Italic ? FontStyles.Italic : FontStyles.Normal,
                    run.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                var formatted = new FormattedText(run.Text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
                    typeface, run.Size, Brushes.Black, 1);
                if (run.Underline) formatted.SetTextDecorations(TextDecorations.Underline);
                if (run.Strike) formatted.SetTextDecorations(TextDecorations.Strikethrough);
                var outline = formatted.BuildGeometry(new Point(x, 330 - formatted.Baseline));
                textPaths.AddRange(ToPaths(outline, run.Color));
                x += formatted.WidthIncludingTrailingWhitespace;
            }
            items.Add(new(Guid.NewGuid(), "text", textPaths.ToArray(), runs));
            items.Add(new(Guid.NewGuid(), "ink", [new([M(70,360), L(150,382), L(200,355), L(280,380)], new(220,50,55), false, 8, .45)]));
            // Quadratic converted exactly to a cubic; semantic type and controls survive in the path DTO.
            var a = new ProbePoint(70, 450); var c = new ProbePoint(180, 360); var b = new ProbePoint(300, 450);
            items.Add(new(Guid.NewGuid(), "quadratic", [new([M(a.X,a.Y), new("C", [
                new(a.X+2*(c.X-a.X)/3, a.Y+2*(c.Y-a.Y)/3), new(b.X+2*(c.X-b.X)/3,b.Y+2*(c.Y-b.Y)/3),b])], new(50,110,210), false, 3)]));
            items.Add(new(Guid.NewGuid(), "cubic", [new([M(70,500), new("C", [new(130,420), new(250,580),new(320,500)])], new(160,65,170), false, 3)]));
            var png = MakePng();
            items.Add(new(Guid.NewGuid(), "image", [], Image: new(350,355,100,100,32,32,png.Rgba,png.Png)));
            var chars = reader.ExtractText(index).Where(t => !string.IsNullOrWhiteSpace(t.Text)).Take(4).ToArray();
            foreach (var character in chars)
            {
                var q = character.Quad.Select(p => new ProbePoint((p.X-page.CropLeft)*page.UserUnit,
                    (page.CropTop-p.Y)*page.UserUnit)).ToArray();
                items.Add(new(Guid.NewGuid(), "highlight", [new([M(q[0].X,q[0].Y), L(q[1].X,q[1].Y),L(q[3].X,q[3].Y),L(q[2].X,q[2].Y),new("Z",[])], new(255,215,20), true, Opacity:.4)]));
            }
            pages.Add(new(Guid.NewGuid(), items.ToArray()));
        }
        return new(Guid.NewGuid(), pages.ToArray());
    }

    public static ProbeModel Edit(ProbeModel model) => model with
    {
        Pages = model.Pages.Select(p => p with { Annotations = p.Annotations.Select(a => a.Type == "ink"
            ? a with { Paths = a.Paths.Select(path => path with { Color = new(10,150,85), Width = 12 }).ToArray() } : a).ToArray() }).ToArray()
    };

    private static (byte[] Rgba, byte[] Png) MakePng()
    {
        var rgba = new byte[32*32*4];
        var bgra = new byte[rgba.Length];
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
        {
            int i = (y*32+x)*4;
            byte alpha = (byte)((x+y)%8 < 4 ? 110 : 255);
            rgba[i] = 25; rgba[i+1] = 155; rgba[i+2] = 220; rgba[i+3] = alpha;
            bgra[i] = 220; bgra[i+1] = 155; bgra[i+2] = 25; bgra[i+3] = alpha;
        }
        var bitmap = BitmapSource.Create(32,32,96,96,PixelFormats.Bgra32,null,bgra,128);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        return (rgba,stream.ToArray());
    }

    private static ProbeSegment M(double x, double y) => new("M",[new(x,y)]);
    private static ProbeSegment L(double x, double y) => new("L",[new(x,y)]);

    private static IEnumerable<ProbePath> ToPaths(Geometry outline, ProbeColor color)
    {
        // Flatten only glyph contours with 0.02pt tolerance. User curves remain cubic.
        var geometry = outline.GetFlattenedPathGeometry(.02, ToleranceType.Absolute);
        var segments = new List<ProbeSegment>();
        foreach (var figure in geometry.Figures)
        {
            Point start = geometry.Transform?.Transform(figure.StartPoint) ?? figure.StartPoint;
            segments.Add(M(start.X,start.Y));
            foreach (var segment in figure.Segments)
            {
                IEnumerable<Point> points = segment switch
                {
                    LineSegment line => [line.Point], PolyLineSegment poly => poly.Points,
                    _ => throw new InvalidOperationException("Unexpected flattened glyph segment.")
                };
                foreach (var point in points)
                {
                    Point p = geometry.Transform?.Transform(point) ?? point;
                    segments.Add(L(p.X,p.Y));
                }
            }
            if (figure.IsClosed) segments.Add(new("Z",[]));
        }
        yield return new(segments.ToArray(),color,true,EvenOdd:geometry.FillRule==FillRule.EvenOdd);
    }

    public static BitmapSource Bitmap(RenderedPage image)
    {
        var result = BitmapSource.Create(image.Width,image.Height,96,96,PixelFormats.Bgra32,null,image.Bgra,image.Stride);
        result.Freeze(); return result;
    }

    public static void SavePng(RenderedPage image, string path) => SavePng(Bitmap(image),path);
    public static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file);
    }
}
