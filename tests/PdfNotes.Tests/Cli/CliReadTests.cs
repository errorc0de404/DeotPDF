using System.IO;
using System.Text.Json.Nodes;
using PdfNotes.Cli;
using PdfNotes.Cli.Commands;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Tests.Cli;

/// <summary>
/// CLI C1 (cli_plan 4.1–4.3, 3.2–3.4): info, text, annot list, render and schema on the C0 sample
/// (<see cref="ProbeSamplePdf.CreateCli"/>): all four rotations, crop boxes with a non-zero origin, a media box larger
/// than the crop box, a UserUnit page and a page without a text layer, each with red crosses at known canonical points.
/// </summary>
public sealed class CliReadTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    private readonly string sample;

    public CliReadTests()
    {
        Directory.CreateDirectory(folder);
        sample = Path.Combine(folder, "cli-sample.pdf");
        ProbeSamplePdf.CreateCli(sample);
    }

    public void Dispose() => Directory.Delete(folder, true);

    private string Out(string name) => Path.Combine(folder, name);

    [Fact]
    public void InfoReportsCanonicalGeometryTextLayerAndStableDerivedPageIds()
    {
        var info = CliRunner.Ok("info", sample);
        Assert.StartsWith("sha256:", info["sourceHash"]!.GetValue<string>());
        Assert.Equal("derived", info["pageIds"]!.GetValue<string>());
        var pages = info["pages"]!.AsArray();
        Assert.Equal(ProbeSamplePdf.CliPages.Length, pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            var spec = ProbeSamplePdf.CliPages[i];
            var page = pages[i]!;
            Assert.Equal(i + 1, page["page"]!.GetValue<int>());
            Assert.Equal(spec.CanonicalWidth, page["width"]!.GetValue<double>(), 0.001);
            Assert.Equal(spec.CanonicalHeight, page["height"]!.GetValue<double>(), 0.001);
            Assert.Equal(spec.Rotate, page["rotation"]!.GetValue<int>());
            Assert.Equal(spec.Rotate is 90 or 270 ? spec.CanonicalHeight : spec.CanonicalWidth, page["displayWidth"]!.GetValue<double>(), 0.001);
            Assert.Equal(spec.Content != ProbeSamplePdf.CliPageContent.Scan, page["hasText"]!.GetValue<bool>());
        }
        // Same file, same IDs on every run; every page its own ID.
        var again = CliRunner.Ok("info", sample)["pages"]!.AsArray();
        Assert.Equal(pages.Select(p => p!["pageId"]!.GetValue<string>()), again.Select(p => p!["pageId"]!.GetValue<string>()));
        Assert.Equal(pages.Count, pages.Select(p => p!["pageId"]!.GetValue<string>()).Distinct().Count());
        // A page ID works wherever a page number does.
        string id = pages[3]!["pageId"]!.GetValue<string>();
        Assert.Equal(4, CliRunner.Ok("text", sample, "-p", id, "--fields", "lines")["page"]!.GetValue<int>());
    }

    [Fact]
    public void SavedDocumentKeepsItsOwnPageIdsAndListsAnnotations()
    {
        string saved = Out("saved.pdf");
        var expected = SaveWithShapes(saved);
        var info = CliRunner.Ok("info", saved);
        Assert.Equal("file", info["pageIds"]!.GetValue<string>());
        Assert.Equal(expected.Select(e => e.PageId.ToString()), info["pages"]!.AsArray().Select(p => p!["pageId"]!.GetValue<string>()));

        var list = CliRunner.Ok("annot", "list", saved);
        Assert.Equal(expected.Count, list["count"]!.GetValue<int>());
        foreach (var (pageId, shapeId, rect) in expected)
        {
            var item = list["annotations"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == shapeId.ToString())!;
            Assert.Equal(pageId.ToString(), item["pageId"]!.GetValue<string>());
            Assert.Equal("shape", item["kind"]!.GetValue<string>());
            // Painted bounds: the 2 pt default border reaches 1 pt outside the box.
            var bounds = item["bounds"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray();
            double half = ShapeStyle.Default.BorderWidthPt / 2;
            double[] expectedBounds = [rect.X - half, rect.Y - half, rect.Width + 2 * half, rect.Height + 2 * half];
            for (int k = 0; k < 4; k++) Assert.Equal(expectedBounds[k], bounds[k], 0.001);
            Assert.Null(item["tags"]);
        }
        Assert.Equal(1, CliRunner.Ok("annot", "list", saved, "-p", "2")["count"]!.GetValue<int>());
        Assert.Equal(0, CliRunner.Ok("annot", "list", saved, "--kind", "text")["count"]!.GetValue<int>());
    }

    [Fact]
    public void RenderPutsEveryKnownPointWherePageToPixelSays()
    {
        for (int i = 0; i < ProbeSamplePdf.CliPages.Length; i++)
        {
            var spec = ProbeSamplePdf.CliPages[i];
            string png = Out($"page{i + 1}.png");
            var result = CliRunner.Ok("render", sample, "-p", (i + 1).ToString(), "-o", png);
            var image = CliRunner.LoadPng(png);
            Assert.Equal(result["width"]!.GetValue<int>(), image.Width);
            Assert.Equal(result["height"]!.GetValue<int>(), image.Height);
            var toPixel = CliRunner.Matrix(result["pageToPixel"]);
            var toPage = CliRunner.Matrix(result["pixelToPage"]);
            foreach (var (x, y) in spec.Fiducials)
            {
                var expected = toPixel.Transform(new(x, y));
                var found = CliRunner.RedCentroid(image, expected) ?? throw new Xunit.Sdk.XunitException($"page {i + 1}: no cross near {expected}");
                Assert.True(Distance(found, expected) <= 1, $"page {i + 1} point ({x},{y}): expected pixel {expected}, cross at {found}");
                var back = toPage.Transform(found);
                Assert.True(Distance(back, new(x, y)) <= 1.5 * 72 / RenderCommand.DefaultDpi + 0.01, $"page {i + 1}: pixelToPage {back}");
            }
        }
    }

    [Fact]
    public void RegionRenderMatrixIncludesTheCropOffset()
    {
        for (int i = 0; i < ProbeSamplePdf.CliPages.Length; i++)
        {
            var (fx, fy) = ProbeSamplePdf.CliPages[i].Fiducials[1];
            string png = Out($"region{i + 1}.png");
            var result = CliRunner.Ok("render", sample, "-p", (i + 1).ToString(), "-o", png, "--dpi", "200", "--region", $"{fx - 30},{fy - 20},60,40");
            var image = CliRunner.LoadPng(png);
            var pixelRegion = result["pixelRegion"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
            Assert.Equal([image.Width, image.Height], pixelRegion[2..]);
            Assert.True(pixelRegion[0] > 0 && pixelRegion[1] > 0);
            var expected = CliRunner.Matrix(result["pageToPixel"]).Transform(new(fx, fy));
            var found = CliRunner.RedCentroid(image, expected, 30) ?? throw new Xunit.Sdk.XunitException($"page {i + 1}: no cross in region");
            Assert.True(Distance(found, expected) <= 1, $"page {i + 1}: expected {expected}, cross at {found}");
        }
        CliRunner.Fails("REGION_OUTSIDE_PAGE", ExitCodes.Unsatisfiable, "render", sample, "-p", "1", "-o", Out("x.png"), "--region", "-500,-500,100,100");
    }

    [Fact]
    public void GridCellsMapEachPageBackToItsOwnCoordinates()
    {
        string png = Out("grid.png");
        var result = CliRunner.Ok("render", sample, "--pages", "1-6", "--grid", "-o", png, "--dpi", "60", "--columns", "3");
        var image = CliRunner.LoadPng(png);
        var cells = result["cells"]!.AsArray();
        Assert.Equal(6, cells.Count);
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i]!;
            Assert.Equal(i + 1, cell["page"]!.GetValue<int>());
            var toPixel = CliRunner.Matrix(cell["pageToPixel"]);
            var rect = cell["pixelRect"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
            foreach (var (x, y) in ProbeSamplePdf.CliPages[i].Fiducials)
            {
                var expected = toPixel.Transform(new(x, y));
                Assert.InRange(expected.X, rect[0], rect[0] + rect[2]);
                Assert.InRange(expected.Y, rect[1], rect[1] + rect[3]);
                var found = CliRunner.RedCentroid(image, expected, 10) ?? throw new Xunit.Sdk.XunitException($"cell {i + 1}: no cross");
                Assert.True(Distance(found, expected) <= 1, $"cell {i + 1}: expected {expected}, cross at {found}");
                Assert.True(Distance(CliRunner.Matrix(cell["pixelToPage"]).Transform(expected), new(x, y)) < 0.01);
            }
        }
    }

    [Fact]
    public void AnnotationsDrawnThroughFlattenLeaveThePaperAsTheRendererDrawsIt()
    {
        string saved = Out("annotated.pdf");
        var shapes = SaveWithShapes(saved);
        for (int i = 0; i < shapes.Count; i++)
        {
            string withNotes = Out($"all{i}.png"), paper = Out($"none{i}.png");
            var all = CliRunner.Ok("render", saved, "-p", (i + 1).ToString(), "-o", withNotes);
            CliRunner.Ok("render", saved, "-p", (i + 1).ToString(), "-o", paper, "--annotations", "none");
            Assert.Equal(1, all["annotationsDrawn"]!.GetValue<int>());
            var a = CliRunner.LoadPng(withNotes);
            var b = CliRunner.LoadPng(paper);
            Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
            var toPixel = CliRunner.Matrix(all["pageToPixel"]);
            var corners = AnnotationGeometry.Corners(shapes[i].Rect, toPixel);
            double x0 = corners.Min(c => c.X) - 4, x1 = corners.Max(c => c.X) + 4, y0 = corners.Min(c => c.Y) - 4, y1 = corners.Max(c => c.Y) + 4;
            long outsideDiff = 0, outsideCount = 0, insideDiff = 0, insideCount = 0, outsideLarge = 0;
            for (int y = 0; y < a.Height; y++)
                for (int x = 0; x < a.Width; x++)
                {
                    int k = (y * a.Width + x) * 4;
                    int d = Math.Abs(a.Bgra[k] - b.Bgra[k]) + Math.Abs(a.Bgra[k + 1] - b.Bgra[k + 1]) + Math.Abs(a.Bgra[k + 2] - b.Bgra[k + 2]);
                    bool inside = x >= x0 && x <= x1 && y >= y0 && y <= y1;
                    if (inside) { insideDiff += d; insideCount++; }
                    else { outsideDiff += d; outsideCount++; if (d > 24) outsideLarge++; }
                }
            Assert.True((double)outsideDiff / outsideCount < 0.5, $"page {i + 1}: paper differs, mean {(double)outsideDiff / outsideCount}");
            Assert.True(outsideLarge <= outsideCount / 2000, $"page {i + 1}: {outsideLarge} paper pixels differ");
            Assert.True((double)insideDiff / insideCount > 30, $"page {i + 1}: shape not drawn");
        }
    }

    [Fact]
    public void TextNumbersCharactersFromZeroAndGroupsWordsAndLines()
    {
        var text = CliRunner.Ok("text", sample, "-p", "1");
        var chars = text["chars"]!.AsArray();
        Assert.Equal(text["charCount"]!.GetValue<int>(), chars.Count);
        for (int i = 0; i < chars.Count; i++) Assert.Equal(i, chars[i]!["index"]!.GetValue<int>());
        string all = string.Concat(chars.Select(c => c!["text"]!.GetValue<string>()));
        Assert.Equal(3, CountOf(all, ProbeSamplePdf.CliRepeatedTerm));

        var words = text["words"]!.AsArray();
        string firstWord = ProbeSamplePdf.CliRepeatedTerm.Split(' ')[0];
        var termWords = words.Where(w => w!["text"]!.GetValue<string>().StartsWith(firstWord)).ToList();
        Assert.Equal(3, termWords.Count);
        foreach (var w in termWords)
        {
            int start = w!["start"]!.GetValue<int>(), end = w["end"]!.GetValue<int>();
            Assert.Equal(w["text"]!.GetValue<string>(), string.Concat(chars.Skip(start).Take(end - start).Select(c => c!["text"]!.GetValue<string>())));
            Assert.Single(w["quads"]!.AsArray());
        }

        var lines = text["lines"]!.AsArray().Select(l => l!["text"]!.GetValue<string>()).ToList();
        Assert.Contains("English words and numbers 0123456789", lines);
        Assert.Contains(ProbeSamplePdf.CliRepeatedTerm, lines);
        Assert.Equal(5, lines.Count);

        // Word quads sit where the page shows that text: the title is above the body line.
        var title = termWords[0]!["quads"]![0]!.AsArray();
        var body = termWords[1]!["quads"]![0]!.AsArray();
        Assert.True(title[2]![1]!.GetValue<double>() < body[0]![1]!.GetValue<double>());

        // Rotated, cropped and UserUnit pages report the same text; the image-like page has none.
        foreach (int p in new[] { 2, 3, 4, 6 })
            Assert.Contains("한글 English 0123456789", CliRunner.Ok("text", sample, "-p", p.ToString(), "--fields", "lines")["lines"]!.AsArray().Select(l => l!["text"]!.GetValue<string>()));
        var scan = CliRunner.Ok("text", sample, "-p", "5");
        Assert.False(scan["hasText"]!.GetValue<bool>());
        Assert.Empty(scan["lines"]!.AsArray());
    }

    [Fact]
    public void RotatedLineQuadsFollowTheShownText()
    {
        // The sample text runs along canonical +x; /Rotate 90 turns it to run down the screen. The quad must land on the
        // inked glyphs of the render and keep its reading direction.
        var line = CliRunner.Ok("text", sample, "-p", "2", "--fields", "lines")["lines"]!.AsArray()
            .Single(l => l!["text"]!.GetValue<string>() == "한글 English 0123456789")!;
        var q = line["quad"]!.AsArray().Select(p => new Point2(p![0]!.GetValue<double>(), p[1]!.GetValue<double>())).ToArray();
        string png = Out("rot.png");
        var render = CliRunner.Ok("render", sample, "-p", "2", "-o", png);
        var toPixel = CliRunner.Matrix(render["pageToPixel"]);
        var ul = toPixel.Transform(q[0]); var ur = toPixel.Transform(q[1]); var ll = toPixel.Transform(q[2]);
        Assert.True(ur.Y - ul.Y > 100 && Math.Abs(ur.X - ul.X) < 1, $"reading direction on screen {ul} -> {ur}");
        Assert.True(ul.X > ll.X, "the glyph tops face right on a page turned clockwise");
        var image = CliRunner.LoadPng(png);
        int dark = 0, total = 0;
        for (int y = (int)ul.Y; y < (int)ur.Y; y++)
            for (int x = (int)ll.X; x < (int)ul.X; x++, total++)
            {
                int k = (y * image.Width + x) * 4;
                if (image.Bgra[k] < 100 && image.Bgra[k + 1] < 100 && image.Bgra[k + 2] < 100) dark++;
            }
        Assert.True(dark > total / 20, $"quad covers glyph ink: {dark}/{total} dark pixels");
    }

    [Fact]
    public void OverlayIsDrawnOnlyOnTheImage()
    {
        string png = Out("overlay.png");
        var (fx, fy) = ProbeSamplePdf.CliPages[0].Fiducials[0];
        var result = CliRunner.Ok("render", sample, "-p", "1", "-o", png, "--overlay", "[{\"rect\":[300,500,100,50],\"color\":\"#0000FF\",\"opacity\":0.5}]");
        Assert.Equal(1, result["overlayCount"]!.GetValue<int>());
        var image = CliRunner.LoadPng(png);
        var center = CliRunner.Matrix(result["pageToPixel"]).Transform(new(350, 525));
        int k = ((int)center.Y * image.Width + (int)center.X) * 4;
        Assert.True(image.Bgra[k] > 200 && image.Bgra[k + 2] < 160, "overlay is blue and translucent over white paper");
        Assert.NotNull(CliRunner.RedCentroid(image, CliRunner.Matrix(result["pageToPixel"]).Transform(new(fx, fy))));
        Assert.Equal(0, CliRunner.Ok("annot", "list", sample)["count"]!.GetValue<int>());
    }

    [Fact]
    public void SchemaListsCommandsAndDescribesEach()
    {
        var list = CliRunner.Ok("schema");
        var names = list["commands"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(CliApp.Commands.Select(c => c.Name), names);
        Assert.Contains("page shrink", names);
        Assert.Contains("measure", names);
        foreach (string name in names)
        {
            var one = CliRunner.Ok(["schema", .. name.Split(' ')]);
            Assert.NotNull(one["schema"]!["input"]);
            Assert.NotNull(one["schema"]!["output"]);
            Assert.NotNull(one["common"]!["$defs"]!["matrix"]);
        }
    }

    /// <summary>C6: <c>schema</c> lists every error code the CLI source throws, with the exit code it throws it with.</summary>
    [Fact]
    public void SchemaListsEveryErrorCode()
    {
        var listed = CliRunner.Ok("schema")["errorCodes"]!.AsArray().ToDictionary(e => e!["code"]!.GetValue<string>(), e => e!["exit"]!.GetValue<int>());
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "PdfNotes.slnx"))) root = root.Parent!;
        var exits = new Dictionary<string, int> { ["InvalidInput"] = 1, ["Unsatisfiable"] = 2, ["Locked"] = 3, ["Internal"] = 4 };
        var thrown = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root.FullName, "tools", "PdfNotes.Cli"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            string text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "new CliException\\(\"([A-Z_]+)\"(?<rest>[\\s\\S]*?)\\);\\s*?\\n"))
            {
                string code = m.Groups[1].Value;
                thrown.Add(code);
                Assert.True(listed.ContainsKey(code), $"{code} ({Path.GetFileName(file)}) is not in SchemaCommand.ErrorCodes");
                var exit = System.Text.RegularExpressions.Regex.Match(m.Groups["rest"].Value, "ExitCodes\\.([A-Za-z]+)");
                int expected = exit.Success ? exits[exit.Groups[1].Value] : ExitCodes.InvalidInput;
                if (code is not ("SAVE_FAILED" or "INTERNAL")) Assert.True(expected == listed[code], $"{code} exits {expected}, the table says {listed[code]}");
            }
        }
        Assert.Contains("FORBIDDEN", thrown);
        foreach (string code in new[] { "PAGE_OVERFLOW", "TEXT_OVERFLOW", "INVALID_ARGUMENT", "INTERNAL" }) Assert.True(listed.ContainsKey(code), code);
    }

    [Fact]
    public void ErrorsAreJsonWithCodesAndExitCodes()
    {
        CliRunner.Fails("UNKNOWN_COMMAND", ExitCodes.InvalidInput, "draw", sample);
        CliRunner.Fails("UNKNOWN_COMMAND", ExitCodes.InvalidInput);
        CliRunner.Fails("FILE_NOT_FOUND", ExitCodes.InvalidInput, "info", Out("missing.pdf"));
        CliRunner.Fails("PAGE_NOT_FOUND", ExitCodes.InvalidInput, "text", sample, "-p", "7");
        CliRunner.Fails("PAGE_NOT_FOUND", ExitCodes.InvalidInput, "text", sample, "-p", Guid.NewGuid().ToString());
        CliRunner.Fails("INVALID_ARGUMENT", ExitCodes.InvalidInput, "info", sample, "--bogus");
        CliRunner.Fails("INVALID_ARGUMENT", ExitCodes.InvalidInput, "text", sample);
        CliRunner.Fails("INVALID_ARGUMENT", ExitCodes.InvalidInput, "render", sample, "-p", "1", "-o", Out("a.png"), "--annotations", "some");
        CliRunner.Fails("INVALID_ARGUMENT", ExitCodes.InvalidInput, "render", sample, "-p", "1", "-o", Out("a.png"), "--grid");
        CliRunner.Fails("SAME_AS_INPUT", ExitCodes.InvalidInput, "render", sample, "-p", "1", "-o", sample);
        File.WriteAllText(Out("broken.pdf"), "not a pdf");
        CliRunner.Fails("PDF_OPEN_FAILED", ExitCodes.InvalidInput, "info", Out("broken.pdf"));
    }

    /// <summary>Saves the sample through the app path with one rectangle per page; returns page, shape and rectangle.</summary>
    private List<(Guid PageId, Guid ShapeId, Rect2 Rect)> SaveWithShapes(string path)
    {
        var result = new List<(Guid, Guid, Rect2)>();
        using var document = PdfNotes.Editor.Services.OpenDocument.OpenPdfAsync(sample).GetAwaiter().GetResult();
        foreach (var page in document.Session.Current.Pages)
        {
            var rect = new Rect2(page.Setup.WidthPt * 0.5, page.Setup.HeightPt * 0.55, 80, 50);
            var shape = new ShapeAnnotation(Guid.NewGuid(), false, Affine.Identity, ShapeKind.Rectangle, rect, [], ShapeStyle.Default);
            Assert.True(document.Session.Execute(DocumentEdits.AddAnnotation(document.Session.Current, page.Id, shape)));
            result.Add((page.Id, shape.Id, rect));
        }
        document.SaveAsync(path).GetAwaiter().GetResult();
        return result;
    }

    private static double Distance(Point2 a, Point2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static int CountOf(string text, string term)
    {
        int count = 0;
        for (int i = text.IndexOf(term, StringComparison.Ordinal); i >= 0; i = text.IndexOf(term, i + term.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
