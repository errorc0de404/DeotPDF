using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Nodes;
using PdfNotes.Cli;
using PdfNotes.Cli.Commands;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Text;

namespace PdfNotes.Tests.Cli;

/// <summary>
/// CLI C2 (cli_plan 4.3, 4.4): find, layout, ink, space, measure and fonts on the C0 sample and a list page.
/// </summary>
public sealed class CliQueryTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    private readonly string sample;

    public CliQueryTests()
    {
        Directory.CreateDirectory(folder);
        sample = Path.Combine(folder, "cli-sample.pdf");
        ProbeSamplePdf.CreateCli(sample);
    }

    public void Dispose() => Directory.Delete(folder, true);

    private string Out(string name) => Path.Combine(folder, name);

    [Fact]
    public void FindListsEveryCandidateInOrderReadyForAddMark()
    {
        string term = ProbeSamplePdf.CliRepeatedTerm;
        var found = CliRunner.Ok("find", sample, "--text", term, "-p", "1");
        var matches = found["matches"]!.AsArray();
        Assert.Equal(3, found["count"]!.GetValue<int>());
        var chars = CliRunner.Ok("text", sample, "-p", "1", "--fields", "chars")["chars"]!.AsArray();
        int previous = -1;
        foreach (var m in matches)
        {
            int start = m!["chars"]!["start"]!.GetValue<int>(), end = m["chars"]!["end"]!.GetValue<int>();
            Assert.True(start > previous, "candidates are ordered by their first character");
            previous = start;
            Assert.Equal(term, string.Concat(chars.Skip(start).Take(end - start).Select(c => c!["text"]!.GetValue<string>())));
            Assert.Equal(1, m["score"]!.GetValue<double>());
            Assert.Single(m["quads"]!.AsArray());
            // The candidate goes into add.mark as it is: page ID with its chars, or with its quads.
            var markTarget = JsonNode.Parse("{\"$ref\":\"common.json#/$defs/markTarget\"}")!;
            Assert.Empty(CliSchema.Validate(new JsonObject { ["page"] = m["pageId"]!.DeepClone(), ["chars"] = m["chars"]!.DeepClone() }, markTarget));
            Assert.Empty(CliSchema.Validate(new JsonObject { ["page"] = m["pageId"]!.DeepClone(), ["quads"] = m["quads"]!.DeepClone() }, markTarget));
        }
        Assert.Contains("실험 장치", matches[2]!["after"]!.GetValue<string>());

        // Spacing, case and line breaks do not matter; --within keeps candidates wholly inside the rectangle.
        Assert.Equal(3, CliRunner.Ok("find", sample, "--text", term.Replace(" ", ""), "-p", "1")["count"]!.GetValue<int>());
        Assert.Equal(1, CliRunner.Ok("find", sample, "--text", "ENGLISH  words", "-p", "1")["count"]!.GetValue<int>());
        var title = matches[0]!["quads"]![0]!.AsArray();
        double top = title.Min(p => p![1]!.GetValue<double>()), bottom = title.Max(p => p![1]!.GetValue<double>());
        var within = CliRunner.Ok("find", sample, "--text", term, "-p", "1", "--within", $"0,{top - 2},595,{bottom - top + 4}");
        Assert.Equal(matches[0]!["chars"]!.ToJsonString(), Assert.Single(within["matches"]!.AsArray())!["chars"]!.ToJsonString());

        // Every page, in page order.
        var pages = CliRunner.Ok("find", sample, "--text", "0123456789")["matches"]!.AsArray().Select(m => m!["page"]!.GetValue<int>()).ToArray();
        Assert.Equal([1, 2, 3, 4, 6], pages);
    }

    [Fact]
    public void FuzzyFindScoresNearMissesBelowOne()
    {
        string typo = ProbeSamplePdf.CliRepeatedTerm[..^1] + "삭"; // one wrong syllable, sample text (D14e)
        Assert.Equal(0, CliRunner.Ok("find", sample, "--text", typo, "-p", "1")["count"]!.GetValue<int>());
        var fuzzy = CliRunner.Ok("find", sample, "--text", typo, "-p", "1", "--fuzzy")["matches"]!.AsArray();
        Assert.Equal(3, fuzzy.Count);
        foreach (var m in fuzzy)
        {
            double score = m!["score"]!.GetValue<double>();
            Assert.InRange(score, 0.8, 0.999);
            Assert.Equal(ProbeSamplePdf.CliRepeatedTerm, m["text"]!.GetValue<string>());
        }
        CliRunner.Fails("INVALID_ARGUMENT", ExitCodes.InvalidInput, "find", sample, "--text", typo, "--min-score", "0.9");
    }

    [Fact]
    public async Task FindIncludesTextBoxesOnlyWhenAsked()
    {
        string saved = Out("boxed.pdf");
        using (var document = await PdfNotes.Editor.Services.OpenDocument.OpenPdfAsync(sample))
        {
            var page = document.Session.Current.Pages[0];
            var box = RichText.Build(RichText.ParseText("메모: " + ProbeSamplePdf.CliRepeatedTerm), RichText.BoxStyle.Default, 200, Affine.Translation(330, 600));
            document.Session.Execute(DocumentEdits.AddAnnotation(document.Session.Current, page.Id, box));
            await document.SaveAsync(saved);
        }
        var plain = CliRunner.Ok("find", saved, "--text", ProbeSamplePdf.CliRepeatedTerm, "-p", "1");
        Assert.Null(plain["annotationMatches"]);
        var both = CliRunner.Ok("find", saved, "--text", ProbeSamplePdf.CliRepeatedTerm, "-p", "1", "--include-annotations");
        Assert.Equal(3, both["count"]!.GetValue<int>());
        var box1 = Assert.Single(both["annotationMatches"]!.AsArray())!;
        Assert.Equal("text", box1["kind"]!.GetValue<string>());
        Assert.Equal(ProbeSamplePdf.CliRepeatedTerm, box1["text"]!.GetValue<string>());
        Assert.Equal(4, box1["range"]!["start"]!.GetValue<int>());
    }

    [Fact]
    public void LayoutGroupsParagraphsListItemsAndIndents()
    {
        string list = Out("list.pdf");
        ProbeSamplePdf.CreateCliList(list);
        var blocks = CliRunner.Ok("layout", list, "-p", "1")["blocks"]!.AsArray();
        var texts = blocks.Select(b => b!["text"]!.GetValue<string>()).ToArray();
        Assert.Equal(
        [
            "Lecture notes on flow",
            "A paragraph that runs over\ntwo lines of plain text.",
            "• First item of the list\ncontinues on a second line",
            "• Second item",
            "- a nested point",
            "1. Numbered step",
            "2. Next step",
        ], texts);
        Assert.Equal(new string?[] { null, null, "•", "•", "-", "1.", "2." }.AsEnumerable(), blocks.Select(b => b!["marker"]?.GetValue<string>()));
        Assert.Equal([0, 0, 0, 0, 1, 0, 0], blocks.Select(b => b!["indent"]!.GetValue<int>()).ToArray());
        // Line numbers are the text command's; ranges follow each other.
        var lines = CliRunner.Ok("text", list, "-p", "1", "--fields", "lines")["lines"]!.AsArray();
        int next = 0;
        foreach (var b in blocks)
        {
            int start = b!["lines"]!["start"]!.GetValue<int>(), end = b["lines"]!["end"]!.GetValue<int>();
            Assert.Equal(next, start);
            next = end;
            Assert.Equal(lines[start]!["start"]!.GetValue<int>(), b["chars"]!["start"]!.GetValue<int>());
            Assert.Equal(lines[end - 1]!["end"]!.GetValue<int>(), b["chars"]!["end"]!.GetValue<int>());
        }
        Assert.Equal(lines.Count, next);

        // The rotated sample page groups its two lines (sizes 16 pt, 30 pt apart) as separate blocks of one direction.
        var rotated = CliRunner.Ok("layout", sample, "-p", "2")["blocks"]!.AsArray();
        Assert.Contains(rotated, b => b!["text"]!.GetValue<string>() == "한글 English 0123456789");
    }

    [Fact]
    public void InkFindsTheScanFiguresInCanonicalPoints()
    {
        // Page 5 is turned 180° and has no text layer: ink comes from pixels and maps back to canonical points.
        var ink = CliRunner.Ok("ink", sample, "-p", "5", "--blocks");
        Assert.Equal("#FFFFFF", ink["paper"]!.GetValue<string>());
        var blocks = ink["blocks"]!.AsArray().Select(b => b!["rect"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray()).ToList();
        // The framed figure: a 2 pt line around (60, 340, 260, 160).
        Assert.Contains(blocks, r => Near(r, [59, 339, 262, 162], 1.5));
        // Each dark bar is its own piece (12 pt apart, merged only within 6 pt).
        for (int line = 0; line < 8; line++)
            Assert.Contains(blocks, r => Near(r, [60, 120 + line * 22, 380 - line % 3 * 60, 10], 1.5));
        // The grid: a cell inside a bar is mostly ink, a cell of empty paper has none.
        var cells = ink["cells"]!.AsArray();
        double cell = ink["cell"]!.GetValue<double>();
        Assert.True(cells[(int)(125 / cell)]![(int)(100 / cell)]!.GetValue<double>() > 0.3);
        Assert.Equal(0, cells[(int)(700 / cell)]![(int)(500 / cell)]!.GetValue<double>());
        Assert.Equal((int)Math.Ceiling(ProbeSamplePdf.CliPages[4].CanonicalWidth / cell), ink["columns"]!.GetValue<int>());
    }

    [Fact]
    public void SpaceCandidatesNeverOverlapInkOrAvoidedAreas()
    {
        double[] avoid = [300, 450, 200, 200];
        var space = CliRunner.Ok("space", sample, "-p", "1", "--size", "120,60", "--avoid", "[[300,450,200,200]]", "--gap", "6", "--limit", "50");
        var candidates = space["candidates"]!.AsArray();
        Assert.True(candidates.Count >= 5);
        var rects = candidates.Select(c => c!["rect"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray()).ToList();
        foreach (var r in rects)
        {
            Assert.Equal(120, r[2]);
            Assert.Equal(60, r[3]);
            Assert.False(Overlap(r, [avoid[0] - 6, avoid[1] - 6, avoid[2] + 12, avoid[3] + 12]), $"candidate {string.Join(",", r)} touches the avoided area");
            Assert.True(r[0] >= 0 && r[1] >= 0 && r[0] + r[2] <= 595.28 && r[1] + r[3] <= 841.89);
        }
        Assert.All(candidates, c => Assert.True(c!["inkRatio"]!.GetValue<double>() < 0.02));
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++) Assert.False(Overlap(rects[i], rects[j]));
        // Nothing of the page text is covered: no candidate overlaps a text line grown by the gap.
        foreach (var line in CliRunner.Ok("text", sample, "-p", "1", "--fields", "lines")["lines"]!.AsArray())
        {
            var q = line!["quad"]!.AsArray().Select(p => (X: p![0]!.GetValue<double>(), Y: p[1]!.GetValue<double>())).ToArray();
            double[] box = [q.Min(p => p.X) - 5, q.Min(p => p.Y) - 5, q.Max(p => p.X) - q.Min(p => p.X) + 10, q.Max(p => p.Y) - q.Min(p => p.Y) + 10];
            Assert.All(rects, r => Assert.False(Overlap(r, box), $"candidate {string.Join(",", r)} covers text"));
        }

        var largest = CliRunner.Ok("space", sample, "-p", "1", "--largest", "3")["candidates"]!.AsArray();
        Assert.Equal(3, largest.Count);
        var areas = largest.Select(c => c!["area"]!.GetValue<double>()).ToArray();
        Assert.True(areas[0] >= areas[1] && areas[1] >= areas[2]);
        CliRunner.Fails("NO_SPACE", ExitCodes.Unsatisfiable, "space", sample, "-p", "1", "--size", "590,800");
        CliRunner.Fails("INVALID_ARGUMENT", ExitCodes.InvalidInput, "space", sample, "-p", "1");
    }

    [Fact]
    public void MeasureMatchesTheBoxTheAppLaysOut()
    {
        // Korean, English, digits, bold and color in one box; the app's own runs for the same text.
        string spans = "[{\"text\":\"베르누이 방정식 \"},{\"text\":\"Bernoulli 2024\",\"bold\":true,\"color\":\"#C00000\"},{\"text\":\" 유선을 따라 에너지 보존\",\"size\":14}]";
        var measured = CliRunner.Ok("measure", "--text", spans, "--width", "160", "--height", "30");
        string text = "베르누이 방정식 Bernoulli 2024 유선을 따라 에너지 보존";
        int a = "베르누이 방정식 ".Length, b = "Bernoulli 2024".Length;
        var d = TextEditing.Default;
        var app = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Identity, text,
            [d with { Start = 0, Length = a }, d with { Start = a, Length = b, Bold = true, Color = new RgbColor(0xC0, 0, 0) },
             d with { Start = a + b, Length = text.Length - a - b, SizePt = 14 }],
            TextAlignment.Left, 160, TextBoxStyle.Default, []);
        double height = TextLayout.Measure(app).Height;
        Assert.Equal(160, measured["width"]!.GetValue<double>(), 0.5);
        Assert.Equal(height, measured["height"]!.GetValue<double>(), 0.5);
        Assert.True(measured["lineCount"]!.GetValue<int>() >= 2);
        Assert.False(measured["fits"]!.GetValue<bool>());
        Assert.Equal(height - 30, measured["overflowHeight"]!.GetValue<double>(), 0.01);
        var lines = measured["lines"]!.AsArray();
        Assert.Equal(0, lines[0]!["start"]!.GetValue<int>());
        Assert.Equal(text.Length, lines[^1]!["end"]!.GetValue<int>());
        Assert.Empty(measured["warnings"]!.AsArray());
        Assert.Equal(4, measured["effectiveStyle"]!["padding"]!.GetValue<double>());

        // No width: one line per written line.
        Assert.Equal(1, CliRunner.Ok("measure", "--text", "one line of text")["lineCount"]!.GetValue<int>());
        Assert.Equal(2, CliRunner.Ok("measure", "--text", "first\nsecond")["lineCount"]!.GetValue<int>());
        // The short marks: **bold** and ==highlight==.
        var marked = RichText.ParseText("보통 **굵게** ==강조==");
        Assert.Equal([null, true, null, null], marked.Select(s => s.Bold).ToArray());
        Assert.NotNull(marked[3].Highlight);
    }

    [Fact]
    public void MeasureReportsMissingGlyphsAndFontSubstitution()
    {
        string missing = char.ConvertFromUtf32(0x10FFFD); // a private-use code point no font draws
        var measured = CliRunner.Ok("measure", "--text", $"[{{\"text\":\"abc {missing} 가\",\"font\":\"No Such Font 123\"}}]", "--width", "200");
        var codes = measured["warnings"]!.AsArray().Select(w => w!["code"]!.GetValue<string>()).ToArray();
        Assert.Contains("MISSING_GLYPH", codes);
        Assert.Contains("FONT_NOT_INSTALLED", codes);
        var glyph = Assert.Single(measured["missingGlyphs"]!.AsArray())!;
        Assert.Equal(["U+10FFFD"], glyph["codepoints"]!.AsArray().Select(c => c!.GetValue<string>()).ToArray());
        Assert.Equal(4, glyph["start"]!.GetValue<int>());
        Assert.All(measured["fonts"]!.AsArray().Where(f => f!["start"]!.GetValue<int>() < 4), f => Assert.Equal(TextLayout.FallbackFont, f!["font"]!.GetValue<string>()));
        Assert.Equal(TextLayout.FallbackFont, measured["effectiveStyle"]!["fontUsed"]!.GetValue<string>());

        var fonts = CliRunner.Ok("fonts");
        Assert.Equal(TextLayout.FallbackFont, fonts["fallback"]!.GetValue<string>());
        Assert.Contains(TextLayout.FallbackFont, fonts["fonts"]!.AsArray().Select(f => f!.GetValue<string>()));
        CliRunner.Fails("INVALID_ARGUMENT", ExitCodes.InvalidInput, "measure", "--text", "[{\"text\":\"x\",\"weight\":700}]");
        CliRunner.Fails("INVALID_ARGUMENT", ExitCodes.InvalidInput, "measure", "--text", "   ");
    }

    private static bool Near(double[] a, double[] b, double tolerance) => a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= tolerance);

    private static bool Overlap(double[] a, double[] b) =>
        a[0] < b[0] + b[2] - 1e-6 && b[0] < a[0] + a[2] - 1e-6 && a[1] < b[1] + b[3] - 1e-6 && b[1] < a[1] + a[3] - 1e-6;
}
