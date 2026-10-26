using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.App.Canvas;
using PdfNotes.Cli;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Text;

namespace PdfNotes.Tests.Cli;

/// <summary>
/// CLI C4 (cli_plan 4.6, 6): every annotation kind made by <c>add</c>, tagged and reopened as ordinary app writing;
/// <c>--find</c> ambiguity, <c>find → add mark</c>, the text overflow policy and source key replacement.
/// </summary>
public sealed class CliAddTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    private readonly string sample;
    private readonly string appFolder;

    public CliAddTests()
    {
        Directory.CreateDirectory(folder);
        sample = Path.Combine(folder, "cli-sample.pdf");
        appFolder = Path.Combine(folder, "app");
        ProbeSamplePdf.CreateCli(sample);
    }

    public void Dispose() => Directory.Delete(folder, true);

    private string Out(string name) => Path.Combine(folder, name);

    private JsonObject Write(params string[] args) => CliRunner.Ok([.. args, "--app-folder", appFolder]);

    private JsonObject WriteFails(string code, int exit, params string[] args) => CliRunner.Fails(code, exit, [.. args, "--app-folder", appFolder]);

    private static double[] Numbers(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<double>()).ToArray();

    private string Png(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (200, 80, (byte)(i % 255), 255);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        string path = Out($"image-{width}x{height}.png");
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    /// <summary>One <c>add</c> command per kind on the sample (page 2 is turned 90°, 5 is scan-like and turned 180°, 6 has a UserUnit).</summary>
    private (string Kind, string[] Args)[] AllKinds()
    {
        string png = Png(40, 20);
        return
        [
            ("text", ["add", "text", "-p", "1", "--rect", "320,60,180,0", "--text", "**굵게** 보통 ==강조==", "--style", "{\"size\":14,\"border\":true}"]),
            ("mark", ["add", "mark", "-p", "1", "--chars", "0:3", "--kind", "underline"]),
            ("highlight-stroke", ["add", "highlight-stroke", "-p", "5", "--rect", "60,120,300,10"]),
            ("highlight-stroke", ["add", "highlight-stroke", "-p", "1", "--points", "[[60,460],[200,470],[300,460]]", "--width", "10"]),
            ("line", ["add", "line", "-p", "1", "--points", "[[320,200],[260,240],[200,230]]", "--end-arrow", "--dash", "dash", "--color", "#c00000"]),
            ("shape", ["add", "shape", "-p", "2", "--kind", "triangle", "--rect", "100,300,120,80"]),
            ("shape", ["add", "shape", "-p", "1", "--kind", "polygon", "--points", "[[400,500],[480,520],[440,580]]", "--no-border"]),
            ("ink", ["add", "ink", "-p", "3", "--points", "[[80,300],[120,320],[160,300],[200,330]]", "--width", "3", "--color", "#1E88E5"]),
            ("image", ["add", "image", "-p", "1", "--file", png, "--rect", "400,700,80,0"]),
            ("note", ["add", "note", "-p", "6", "--at", "200,200", "--text", "메모\n둘째 줄", "--lock"]),
        ];
    }

    /// <summary>Runs <paramref name="steps"/> in turn, each on the previous output, with one batch id: the last output and the IDs made.</summary>
    private (string Output, List<(string Kind, string Id)> Made) AddAll((string Kind, string[] Args)[] steps)
    {
        string input = sample;
        var made = new List<(string Kind, string Id)>();
        for (int i = 0; i < steps.Length; i++)
        {
            string output = Out($"step{i}.pdf");
            var result = Write([.. steps[i].Args.Take(2), input, .. steps[i].Args.Skip(2), "-o", output, "--batch-id", "run-c4",
                "--source-key", $"c4/{i}", "--group-key", "c4"]);
            var annotation = result["annotation"]!;
            Assert.Equal(steps[i].Kind, annotation["kind"]!.GetValue<string>());
            Assert.Equal("ai", annotation["tags"]!["author"]!.GetValue<string>());
            Assert.Equal($"c4/{i}", annotation["tags"]!["sourceKey"]!.GetValue<string>());
            Assert.False(result["replaced"]!.GetValue<bool>());
            made.Add((steps[i].Kind, annotation["id"]!.GetValue<string>()));
            input = output;
        }
        return (input, made);
    }

    [Fact]
    public async Task EveryKindIsTaggedAndReopensAsEditableAppWriting()
    {
        var steps = AllKinds();
        var (input, made) = AddAll(steps);
        var list = CliRunner.Ok("annot", "list", input, "--batch-id", "run-c4");
        Assert.Equal(steps.Length, list["count"]!.GetValue<int>());
        Assert.Equal(steps.Length, CliRunner.Ok("annot", "list", input, "--group-key", "c4")["count"]!.GetValue<int>());
        var byId = list["annotations"]!.AsArray().ToDictionary(a => a!["id"]!.GetValue<string>(), a => a!);
        Assert.Equal("#C00000", byId[made[4].Id]["color"]!.GetValue<string>());
        Assert.True(byId[made[4].Id]["endArrow"]!.GetValue<bool>());
        Assert.Equal("quadratic", byId[made[4].Id]["lineKind"]!.GetValue<string>());
        Assert.True(byId[made[9].Id]["locked"]!.GetValue<bool>());
        // The image keeps its aspect (h 0); the shape on the turned page covers the given page rectangle.
        Assert.Equal([400, 700, 80, 40], Numbers(byId[made[8].Id]["bounds"]));
        Assert.Equal([99, 299, 122, 82], Numbers(byId[made[5].Id]["bounds"])); // the 2 pt border is centered on the box

        // The app restores every one of them as its own writing (not read-only), with the tags, and can edit them.
        using var document = await OpenDocument.OpenPdfAsync(input);
        Assert.Equal(steps.Length, document.Source!.AppData.RestoredCount);
        Assert.Equal(0, document.Source.AppData.ReadOnlyCount);
        var model = document.Session.Current;
        foreach (var (kind, id) in made)
        {
            var annotation = model.FindAnnotation(Guid.Parse(id))!.Annotation;
            Assert.Equal("run-c4", model.TagOf(annotation.Id)!.BatchId);
            var bounds = AnnotationGeometry.LocalBounds(annotation, TextLayout.Bounds);
            Assert.True(bounds.Width > 0 && bounds.Height > 0, kind);
            // Selecting it hits it at its middle, and moving it is the ordinary app edit (glyph marks never move).
            var local = annotation switch
            {
                StrokeAnnotation { IsOutlined: false } stroke => stroke.Points[0],
                LineAnnotation line => line.Start,
                _ => new Point2(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2),
            };
            Assert.True(AnnotationGeometry.Hit(annotation, annotation.Transform.Transform(local), 1, TextLayout.Bounds), kind);
            if (AnnotationGeometry.CanTransform(annotation) && !annotation.Locked)
            {
                var moved = annotation with { Transform = annotation.Transform.Then(Affine.Translation(5, 5)) };
                Assert.True(document.Session.Execute(PdfNotes.Core.Commands.DocumentEdits.UpdateAnnotations(document.Session.Current, [moved])));
            }
        }
        string resaved = Out("resaved.pdf");
        await document.SaveAsync(resaved);
        Assert.Equal(steps.Length, CliRunner.Ok("annot", "list", resaved, "--batch-id", "run-c4")["count"]!.GetValue<int>());
    }

    /// <summary>
    /// cli_plan 4.2 left from C1: <c>render</c> draws annotations from the Flatten PDF; the app screen draws them with
    /// <see cref="AnnotationPainter"/>. On every page of every kind both must look the same: of the pixels either one
    /// paints, only edge pixels may differ by up to 1 px. <c>PDFNOTES_RENDER_COMPARE_OUT</c> keeps the images for a look.
    /// </summary>
    [Fact]
    public void RenderedAnnotationsMatchTheAppPainter()
    {
        var (output, _) = AddAll(AllKinds());
        string? keep = Environment.GetEnvironmentVariable("PDFNOTES_RENDER_COMPARE_OUT");
        var report = new List<string>();
        Sta(() =>
        {
            using var doc = CliDocument.Open(output);
            const double ppp = 2;
            foreach (var page in doc.Model.Pages.Where(p => p.Annotations.Count > 0))
            {
                var paper = PageRaster.Render(doc, page, ppp, null, _ => false, out _);
                var flat = PageRaster.Render(doc, page, ppp, null, _ => true, out _);
                var view = new ViewTransform(page.Setup.Frame, ppp / Units.DipsPerPoint, 1, default);
                var app = PageRaster.Compose(paper.Width, paper.Height, context =>
                {
                    context.DrawImage(PageRaster.ToBitmap(paper), new System.Windows.Rect(0, 0, paper.Width, paper.Height));
                    foreach (var a in page.Annotations)
                        if (a is ImageAnnotation image) AnnotationPainter.PaintImage(context, image, doc.Model.Resources[image.ResourceId], view);
                        else AnnotationPainter.Paint(context, a, view);
                });
                var appPixels = new byte[paper.Width * paper.Height * 4];
                new FormatConvertedBitmap(app, PixelFormats.Bgra32, null, 0).CopyPixels(appPixels, paper.Width * 4, 0);
                int painted = 0, different = 0;
                static int Diff(byte[] a, int ia, byte[] b, int ib) => Math.Max(Math.Abs(a[ia] - b[ib]), Math.Max(Math.Abs(a[ia + 1] - b[ib + 1]), Math.Abs(a[ia + 2] - b[ib + 2])));
                int F(int x, int y) => y * paper.Stride + x * 4;
                int A(int x, int y) => (y * paper.Width + x) * 4;
                // Edge pixels are anti-aliased differently (PDFium and WPF cover a partly painted pixel by other amounts and
                // round edges to other sides), so a pixel only differs when its color is outside the range of the other
                // image's colors within 1 px.
                bool Near(byte[] a, Func<int, int, int> ai, byte[] b, Func<int, int, int> bi, int x, int y)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int low = 255, high = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                                if (x + dx >= 0 && y + dy >= 0 && x + dx < paper.Width && y + dy < paper.Height)
                                {
                                    int v = b[bi(x + dx, y + dy) + c];
                                    low = Math.Min(low, v);
                                    high = Math.Max(high, v);
                                }
                        int own = a[ai(x, y) + c];
                        if (own < low - 16 || own > high + 16) return false;
                    }
                    return true;
                }
                for (int y = 0; y < paper.Height; y++)
                    for (int x = 0; x < paper.Width; x++)
                    {
                        if (Diff(flat.Bgra, F(x, y), paper.Bgra, F(x, y)) <= 8 && Diff(appPixels, A(x, y), paper.Bgra, F(x, y)) <= 8) continue;
                        painted++;
                        if (Diff(flat.Bgra, F(x, y), appPixels, A(x, y)) > 48 &&
                            !(Near(flat.Bgra, F, appPixels, A, x, y) && Near(appPixels, A, flat.Bgra, F, x, y))) different++;
                    }
                double share = (double)different / Math.Max(1, painted);
                report.Add($"page {doc.NumberOf(page)}: painted {painted}, different {different} ({share:P2})");
                if (keep is not null)
                {
                    PageRaster.SavePng(PageRaster.ToBitmap(flat), Path.Combine(keep, $"render-p{doc.NumberOf(page)}.png"));
                    PageRaster.SavePng(app, Path.Combine(keep, $"painter-p{doc.NumberOf(page)}.png"));
                }
                Assert.True(painted > 100, report[^1]);
                Assert.True(share < 0.002, string.Join("\n", report));
            }
        });
        if (keep is not null) File.WriteAllLines(Path.Combine(keep, "compare.txt"), report);
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "timed out");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void FindNeedsExactlyOneCandidate()
    {
        string term = ProbeSamplePdf.CliRepeatedTerm;
        var ambiguous = WriteFails("AMBIGUOUS_MATCH", ExitCodes.Unsatisfiable, "add", "mark", sample, "-p", "1", "--find", term, "-o", Out("a.pdf"));
        Assert.Equal(3, ambiguous["error"]!["details"]!["matches"]!.AsArray().Count);
        Assert.False(File.Exists(Out("a.pdf")));
        WriteFails("NOT_FOUND", ExitCodes.Unsatisfiable, "add", "mark", sample, "-p", "1", "--find", "없는 문구", "-o", Out("a.pdf"));
        WriteFails("NOT_FOUND", ExitCodes.Unsatisfiable, "add", "mark", sample, "-p", "1", "--find", term, "--occurrence", "4", "-o", Out("a.pdf"));

        // Narrowed to the caption by --within, or picked by --occurrence: the same characters as find gives.
        var candidates = CliRunner.Ok("find", sample, "-p", "1", "--text", term)["matches"]!.AsArray();
        var caption = candidates[2]!;
        var byWithin = Write("add", "mark", sample, "-p", "1", "--find", term, "--within", "0,360,595,30", "-o", Out("w.pdf"));
        var byOccurrence = Write("add", "mark", sample, "-p", "1", "--find", term, "--occurrence", "3", "-o", Out("o.pdf"));
        foreach (var result in new[] { byWithin, byOccurrence })
        {
            Assert.True(JsonNode.DeepEquals(caption["chars"], result["chars"]));
            Assert.True(JsonNode.DeepEquals(caption["quads"], result["annotation"]!["quads"]));
            Assert.Equal(caption["text"]!.GetValue<string>(), result["text"]!.GetValue<string>());
        }
    }

    [Fact]
    public void FindThenChosenCandidateThenMark()
    {
        // The recommended path (cli_plan 4.6.1): find, choose, then add mark with the candidate's chars or quads.
        var chosen = CliRunner.Ok("find", sample, "-p", "1", "--text", ProbeSamplePdf.CliRepeatedTerm)["matches"]![1]!;
        var chars = chosen["chars"]!;
        var byChars = Write("add", "mark", sample, "-p", chosen["pageId"]!.GetValue<string>(), "--chars", $"{chars["start"]}:{chars["end"]}",
            "--color", "#43A047", "--height", "12", "--dry-run");
        Assert.True(JsonNode.DeepEquals(chosen["quads"], byChars["annotation"]!["quads"]));
        Assert.Equal(12, byChars["annotation"]!["height"]!.GetValue<double>());
        Assert.Equal("#43A047", byChars["annotation"]!["color"]!.GetValue<string>());
        var byQuads = Write("add", "mark", sample, "-p", "1", "--quads", chosen["quads"]!.ToJsonString(), "--kind", "strikeout", "--dry-run");
        Assert.True(JsonNode.DeepEquals(chosen["quads"], byQuads["annotation"]!["quads"]));
        Assert.Equal("strikeout", byQuads["annotation"]!["markKind"]!.GetValue<string>());
        Assert.Null(byQuads["chars"]);

        // Words are the text command's words.
        var words = CliRunner.Ok("text", sample, "-p", "1", "--fields", "words")["words"]!.AsArray();
        var w = words[2]!;
        var byWords = Write("add", "mark", sample, "-p", "1", "--words", "2:3", "--dry-run");
        Assert.Equal(w["start"]!.GetValue<int>(), byWords["chars"]!["start"]!.GetValue<int>());
        Assert.Equal(w["end"]!.GetValue<int>(), byWords["chars"]!["end"]!.GetValue<int>());
        WriteFails("RANGE_OUT_OF_TEXT", ExitCodes.InvalidInput, "add", "mark", sample, "-p", "1", "--chars", "0:100000", "--dry-run");
        WriteFails("INVALID_ARGUMENT", ExitCodes.InvalidInput, "add", "mark", sample, "-p", "1", "--chars", "0:3", "--words", "0:1", "--dry-run");
    }

    [Fact]
    public void TextBoxesFollowMeasureAndRefuseToLeaveThePage()
    {
        const string text = "베르누이 방정식은 **유선을 따라** 에너지 보존을 적용한 식이다.";
        var measured = CliRunner.Ok("measure", "--text", text, "--width", "110");
        var made = Write("add", "text", sample, "-p", "1", "--rect", "460,300,110,0", "--text", text, "--dry-run");
        Assert.Equal([460, 300, 110, measured["height"]!.GetValue<double>()], Numbers(made["rect"]));
        Assert.Equal(measured["lineCount"]!.GetValue<int>(), made["layout"]!["lineCount"]!.GetValue<int>());
        Assert.Equal(Numbers(made["rect"]), Numbers(made["annotation"]!["bounds"]));

        // Auto height that runs off the bottom: PAGE_OVERFLOW (exit 2) and no file; allowed, it is made with a warning.
        string output = Out("overflow.pdf");
        var error = WriteFails("PAGE_OVERFLOW", ExitCodes.Unsatisfiable, "add", "text", sample, "-p", "1", "--rect", "460,800,110,0", "--text", text, "-o", output);
        Assert.True(error["error"]!["details"]!["overflow"]!["bottom"]!.GetValue<double>() > 0);
        Assert.False(File.Exists(output));
        var allowed = Write("add", "text", sample, "-p", "1", "--rect", "460,800,110,0", "--text", text, "--overflow", "allow", "-o", output);
        Assert.Contains(allowed["warnings"]!.AsArray(), w => w!["code"]!.GetValue<string>() == "PAGE_OVERFLOW");
        Assert.Equal(measured["height"]!.GetValue<double>(), Numbers(allowed["rect"])[3]); // never shrunk or rewrapped

        // A fixed height that is too small: TEXT_OVERFLOW.
        WriteFails("TEXT_OVERFLOW", ExitCodes.Unsatisfiable, "add", "text", sample, "-p", "1", "--rect", "60,500,110,20", "--text", text, "--no-auto-height", "--dry-run");
        var fits = Write("add", "text", sample, "-p", "1", "--rect", "60,500,400,40", "--text", "짧은 글", "--no-auto-height", "--dry-run");
        Assert.True(fits["layout"]!["fits"]!.GetValue<bool>());
        // Missing glyphs are always reported.
        var missing = Write("add", "text", sample, "-p", "1", "--rect", "60,500,200,0", "--text", "A\U000F0000B", "--dry-run");
        Assert.Contains(missing["warnings"]!.AsArray(), w => w!["code"]!.GetValue<string>() == "MISSING_GLYPH");
    }

    [Fact]
    public async Task SourceKeyReplacesTheSameAnnotation()
    {
        string first = Out("first.pdf"), second = Out("second.pdf"), third = Out("third.pdf");
        var a = Write("add", "text", sample, "-p", "1", "--rect", "320,60,180,0", "--text", "첫 번째", "--source-key", "k/text", "-o", first, "--batch-id", "run-1");
        Write("add", "shape", first, "-p", "1", "--kind", "rect", "--rect", "320,120,40,40", "-o", second, "--overwrite");
        // Same key: the same ID and stacking position, the new text and batch id; the count does not grow.
        var b = Write("add", "text", second, "-p", "1", "--rect", "330,70,150,0", "--text", "두 번째", "--source-key", "k/text", "-o", third, "--batch-id", "run-2");
        Assert.True(b["replaced"]!.GetValue<bool>());
        Assert.Null(b["previousPage"]);
        Assert.Equal(a["annotation"]!["id"]!.GetValue<string>(), b["annotation"]!["id"]!.GetValue<string>());
        Assert.Equal(0, b["annotation"]!["z"]!.GetValue<int>());
        Assert.Equal("두 번째", b["annotation"]!["text"]!.GetValue<string>());
        Assert.Equal("run-2", b["annotation"]!["tags"]!["batchId"]!.GetValue<string>());
        Assert.Equal(2, CliRunner.Ok("annot", "list", third)["count"]!.GetValue<int>());

        // Another page moves it there; another kind is refused.
        var moved = Write("add", "text", third, "-p", "3", "--rect", "60,300,150,0", "--text", "세 번째", "--source-key", "k/text", "--dry-run");
        Assert.Equal(1, moved["previousPage"]!["page"]!.GetValue<int>());
        Assert.Equal(3, moved["annotation"]!["page"]!.GetValue<int>());
        WriteFails("SOURCE_KEY_KIND_MISMATCH", ExitCodes.Unsatisfiable, "add", "mark", third, "-p", "1", "--chars", "0:2", "--source-key", "k/text", "--dry-run");

        // Images: a replaced picture's unused resource leaves with it.
        string img1 = Png(10, 10), img2 = Png(20, 10), withImage = Out("image.pdf"), replaced = Out("image2.pdf");
        Write("add", "image", sample, "-p", "1", "--file", img1, "--source-key", "k/img", "-o", withImage);
        var r = Write("add", "image", withImage, "-p", "1", "--file", img2, "--rect", "100,100,40,0", "--source-key", "k/img", "-o", replaced);
        Assert.True(r["replaced"]!.GetValue<bool>());
        Assert.Equal([100, 100, 40, 20], Numbers(r["annotation"]!["bounds"]));
        Assert.Equal(1, CliRunner.Ok("info", replaced)["pages"]![0]!["annotationCount"]!.GetValue<int>());
        using var document = await OpenDocument.OpenPdfAsync(replaced);
        Assert.Single(document.Session.Current.Resources); // the first picture's resource left with it
    }
}
