using System.Collections.Immutable;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Tools;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Writing;
using PdfNotes.Render.Skia;
using SkiaSharp;
using PdfNotes.Core.Marks;

namespace PdfNotes.Desktop.Tests;

/// <summary>
/// Roadmap P4 A2: every annotation kind drawn by the Skia painter looks like its PDF appearance rendered by PDFium (what
/// other PDF apps and the flattened PDF show). Only anti-aliased edges may differ.
/// </summary>
public sealed class PaintTests
{
    private static readonly RgbColor Red = new(220, 40, 40), Blue = new(20, 60, 200), Yellow = new(255, 220, 0), Green = new(30, 150, 60);

    private static Resource Png()
    {
        // About as many pixels as it covers on the page (a photo, not a few pixels blown up: then smoothing differs).
        using var bitmap = new SKBitmap(160, 120);
        for (int y = 0; y < 120; y++)
            for (int x = 0; x < 160; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 255 / 160), (byte)(y * 255 / 120), (byte)(x < 80 ? 40 : 200)));
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return Resource.Create(Guid.NewGuid(), "image/png", data.ToArray());
    }

    /// <summary>One annotation of each kind (and the stroke variants), well apart on one page.</summary>
    private static (DocumentModel Model, Dictionary<string, Annotation> Kinds) Corpus()
    {
        var image = Png();
        var piece = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [], 8, Red, 1)
        {
            Outline = [[new(330, 40), new(380, 40), new(380, 90), new(330, 90)], [new(345, 55), new(365, 55), new(365, 75), new(345, 75)]]
        };
        string text = "한글 English 12";
        var kinds = new Dictionary<string, Annotation>
        {
            ["ink"] = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(40, 40), new(90, 60), new(140, 45), new(180, 80)], 4, Red, 1),
            ["pressure"] = new InkStroke(Guid.NewGuid(), false, Affine.Rigid(.2, 0, 0), [new(220, 40), new(260, 60), new(300, 50)], 6, Blue, 1) { Pressures = [0.1, 0.9, 0.4] },
            ["piece"] = piece,
            ["dot"] = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(420, 60)], 10, Green, 1),
            ["highlighter"] = new FreeHighlightStroke(Guid.NewGuid(), false, Affine.Identity, [new(40, 130), new(120, 140), new(60, 150), new(180, 135)], 14, Yellow, .4),
            ["line"] = new LineAnnotation(Guid.NewGuid(), false, Affine.Identity, LineKind.Straight, new(220, 120), new(360, 160), [], 3, DashStyle.Dash, true, true, Blue, .8),
            ["curve"] = new LineAnnotation(Guid.NewGuid(), false, Affine.Rigid(-.3, 10, 40), LineKind.Cubic, new(380, 120), new(500, 150),
                [new(400, 60), new(470, 200)], 2, DashStyle.DashDot, false, true, Green, 1),
            ["rectangle"] = new ShapeAnnotation(Guid.NewGuid(), false, Affine.Identity, ShapeKind.Rectangle, new(40, 200, 120, 70), [],
                new(new(120, 180, 255), .5, true, Blue, 3, .9, DashStyle.Dash)),
            ["ellipse"] = new ShapeAnnotation(Guid.NewGuid(), false, Affine.Rigid(.3, 0, 0), ShapeKind.Ellipse, new(260, 160, 100, 60), [],
                new(Yellow, .7, true, Red, 2, 1, DashStyle.Solid)),
            ["polyline"] = new ShapeAnnotation(Guid.NewGuid(), false, Affine.Identity, ShapeKind.Polyline, default,
                [new(400, 220), new(460, 260), new(420, 300), new(520, 280)], new(Yellow, 0, true, Green, 2.5, 1, DashStyle.Dot)),
            ["glyph highlight"] = new PdfTextHighlight(Guid.NewGuid(), false,
                [new Quad(new(40, 320), new(200, 320), new(40, 338), new(200, 338)), new Quad(new(40, 345), new(120, 345), new(40, 363), new(120, 363))],
                "x", HighlightHeightMode.Manual, 12, Yellow, .4),
            ["underline"] = new PdfTextMarkup(Guid.NewGuid(), false, [new Quad(new(240, 320), new(400, 320), new(240, 338), new(400, 338))], "x",
                TextMarkupKind.Underline, 1.5, Red, 1),
            ["text"] = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Rigid(.15, 60, 420), text,
                [new TextRun(0, 3, "Malgun Gothic", 18, Blue, true, false, false, false), new TextRun(3, text.Length - 3, "Malgun Gothic", 14, RgbColor.Black, false, false, true, false)],
                TextAlignment.Left, 200, new(new(255, 250, 200), .9, true, Red, .8), [new TextHighlightRange(4, 7, HighlightHeightMode.Auto, null, Yellow, .5)]),
            ["image"] = new ImageAnnotation(Guid.NewGuid(), false, Affine.Rigid(.25, 380, 400), image.Id, new(0, 0, 80, 60)),
            ["note"] = new StickyNote(Guid.NewGuid(), false, Affine.Translation(480, 480), "memo", new(255, 210, 60)),
        };
        var model = DocumentModel.Create([new PageSetup(560, 600, 0, PageSource.Generated, PageBackground.Blank)]);
        model = model with
        {
            Pages = [model.Pages[0] with { Annotations = [.. kinds.Values] }],
            Resources = model.Resources.Add(image.Id, image)
        };
        return (model, kinds);
    }

    [Fact]
    public void EveryKindLooksLikeItsPdfAppearance()
    {
        var (model, kinds) = Corpus();
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        const double PixelsPerPoint = 2;
        using var engine = new PdfiumDocument(pdf);
        var expected = engine.Render(0, PixelsPerPoint, annotations: true);

        var page = model.Pages[0];
        var transform = new ViewTransform(page.Setup.Frame, PixelsPerPoint / Units.DipsPerPoint, 1, default);
        using var surface = SKSurface.Create(new SKImageInfo(expected.Width, expected.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White);
        SkiaAnnotationPainter.PaintPage(surface.Canvas, page, transform, id => model.Resources.GetValueOrDefault(id));
        using var actual = surface.Snapshot();
        using var pixels = actual.PeekPixels();
        var bytes = pixels.GetPixelSpan();

        if (Environment.GetEnvironmentVariable("PDFNOTES_PAINT_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllBytes(Path.Combine(dump, "skia.png"), actual.Encode(SKEncodedImageFormat.Png, 100).ToArray());
            var info = new SKImageInfo(expected.Width, expected.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var pdfium = SKImage.FromPixelCopy(info, expected.Bgra, expected.Stride);
            File.WriteAllBytes(Path.Combine(dump, "pdfium.png"), pdfium.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        }
        var report = new List<string>();
        foreach (var (name, annotation) in kinds)
        {
            var b = AnnotationGeometry.Bounds([annotation], PdfNotes.Text.TextLayout.Bounds);
            int x0 = Math.Max(0, (int)(b.X * PixelsPerPoint) - 2), y0 = Math.Max(0, (int)(b.Y * PixelsPerPoint) - 2);
            int x1 = Math.Min(expected.Width, (int)((b.X + b.Width) * PixelsPerPoint) + 2), y1 = Math.Min(expected.Height, (int)((b.Y + b.Height) * PixelsPerPoint) + 2);
            long sum = 0, far = 0, n = 0, inked = 0, inkedHere = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = y * expected.Stride + x * 4, j = y * pixels.RowBytes + x * 4;
                    int d = Math.Abs(expected.Bgra[i] - bytes[j]) + Math.Abs(expected.Bgra[i + 1] - bytes[j + 1]) + Math.Abs(expected.Bgra[i + 2] - bytes[j + 2]);
                    sum += d; n++;
                    if (d > 120) far++;
                    if (expected.Bgra[i] + expected.Bgra[i + 1] + expected.Bgra[i + 2] < 700) inked++;
                    if (bytes[j] + bytes[j + 1] + bytes[j + 2] < 700) inkedHere++;
                }
            double mean = sum / (double)n, farShare = far / (double)n;
            report.Add($"{name}: mean {mean:0.0}, far {farShare:P2}, inked {inked}");
            Assert.True(inked > 20, $"{name}: PDFium drew nothing to compare ({string.Join("; ", report)})");
            // A dotted line: the dots are the same, but where the pattern starts again after a corner differs by a
            // fraction of a dot between Skia and PDFium (2026-11-04, compared by eye), so compare the ink instead.
            if (name == "polyline") Assert.True(Math.Abs(inkedHere - inked) < inked * 0.1 && mean < 25, string.Join("; ", report));
            else Assert.True(mean < 12 && farShare < 0.03, string.Join("; ", report));
        }
    }

    [Fact]
    public void PreviewMarksDraw()
    {
        // The tool marks have no PDF counterpart: check they draw where they should.
        var frame = new PageFrame(400, 300, 0);
        var transform = new ViewTransform(frame, 1, 1, default);
        using var surface = SKSurface.Create(new SKImageInfo(600, 450));
        surface.Canvas.Clear(SKColors.White);
        SkiaMarkPainter.Paint(surface.Canvas, [new EraserCircleMark(new(100, 100), 40), new LassoMark([new(200, 50), new(300, 60), new(250, 150)])], transform);
        SkiaMarkPainter.PaintSelectionFrame(surface.Canvas, [new(20, 200), new(120, 200), new(120, 260), new(20, 260), new(70, 180)]);
        using var image = surface.Snapshot();
        using var pixels = image.PeekPixels();
        var eraserEdge = transform.PageToDip.Transform(new(120, 100));
        Assert.NotEqual(SKColors.White, pixels.GetPixelColor((int)eraserEdge.X, (int)eraserEdge.Y));
        Assert.NotEqual(SKColors.White, pixels.GetPixelColor(20, 230)); // frame edge
        Assert.Equal(SKColors.White, pixels.GetPixelColor(500, 400));
    }
}
