using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.Cli;
using PdfNotes.Cli.Commands;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Tests.Cli;

/// <summary>
/// CLI C5 (cli_plan 4.7, 4.8, 5, 6, 8): <c>annot</c> edits that touch only the CLI's own annotations, <c>check</c> rules,
/// and <c>apply</c> — all or nothing, input page numbers and <c>$names</c>, run-time coordinates, replacement by source
/// key, regeneration that gives the same result.
/// </summary>
public sealed class CliEditTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    private readonly string sample;
    private readonly string appFolder;

    public CliEditTests()
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

    private static string Id(JsonObject result) => result["annotation"]!["id"]!.GetValue<string>();

    private string Plan(string name, string json)
    {
        string path = Out(name);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Runs <c>apply</c> and checks every operation's result against its own command's output schema.</summary>
    private JsonObject Apply(string input, string plan, params string[] options)
    {
        var applied = Write(["apply", input, plan, .. options]);
        foreach (var op in applied["ops"]!.AsArray())
        {
            var full = op!.DeepClone().AsObject();
            string command = full["op"]!.GetValue<string>().Replace('.', ' ');
            full.Remove("index");
            full.Remove("op");
            full["ok"] = true;
            full["command"] = command;
            full["sourceHash"] = applied["sourceHash"]!.DeepClone();
            foreach (var field in new[] { "dryRun", "flatten", "output", "outputHash", "pageCount", "pageIds", "previews" }) full[field] = applied[field]?.DeepClone();
            var errors = CliSchema.Validate(full, SchemaCommand.Load(SchemaCommand.FileName(command))["output"]!);
            Assert.True(errors.Count == 0, command + ": " + string.Join("\n", errors));
        }
        return applied;
    }

    private string Png(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (40, 160, 90, 255);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        string path = Out($"image-{width}x{height}.png");
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    /// <summary>The sample with one pen stroke of the user's own (no tag) on page 1, and that stroke's ID.</summary>
    private async Task<(string Path, string InkId)> WithUserInk()
    {
        using var document = await OpenDocument.OpenPdfAsync(sample);
        var page = document.Session.Current.Pages[0];
        var ink = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(60, 600), new(140, 620)], 2, RgbColor.Black, 1);
        document.Session.Execute(DocumentEdits.AddAnnotation(document.Session.Current, page.Id, ink));
        string path = Out("user.pdf");
        await document.WriteCopyAsync(path);
        return (path, ink.Id.ToString());
    }

    [Fact]
    public async Task AnnotEditsChangeOnlyTheCliAnnotations()
    {
        var (user, inkId) = await WithUserInk();
        string png = Png(40, 20), made = Out("made.pdf");
        Apply(user, Plan("make.json", $$"""
            { "batchId": "run-a", "ops": [
              { "op": "add.text", "page": 1, "rect": [320, 60, 180, 0], "text": "**첫** 문단", "sourceKey": "e/text", "groupKey": "e" },
              { "op": "add.mark", "page": 1, "chars": { "start": 0, "end": 3 }, "sourceKey": "e/mark", "groupKey": "e" },
              { "op": "add.shape", "page": 1, "kind": "rect", "rect": [320, 200, 60, 40], "sourceKey": "e/shape", "groupKey": "e" },
              { "op": "add.image", "page": 1, "file": {{JsonValue.Create(png).ToJsonString()}}, "rect": [400, 700, 80, 0], "sourceKey": "e/img", "groupKey": "e" },
              { "op": "add.note", "page": 1, "at": [500, 500], "text": "메모", "sourceKey": "e/note" },
              { "op": "add.ink", "page": 1, "points": [[60, 500], [100, 520]], "sourceKey": "e/ink" }
            ] }
            """), "-o", made);

        // Text: new text in the box's own style, same ID and stacking position, the run's batch id; then style and width.
        var before = CliRunner.Ok("annot", "list", made, "--source-key", "e/text")["annotations"]![0]!;
        var text = Write("annot", "update", made, "--source-key", "e/text", "--text", "바뀐 **글**", "--batch-id", "run-b", "--dry-run");
        Assert.Equal(before["id"]!.GetValue<string>(), Id(text));
        Assert.Equal(before["z"]!.GetValue<int>(), text["annotation"]!["z"]!.GetValue<int>());
        Assert.Equal("바뀐 글", text["annotation"]!["text"]!.GetValue<string>());
        Assert.Equal("run-b", text["annotation"]!["tags"]!["batchId"]!.GetValue<string>());
        Assert.True(text["changed"]!.GetValue<bool>());
        Assert.NotNull(text["layout"]);
        var styled = Write("annot", "update", made, "--source-key", "e/text", "--style", "{\"size\":20,\"alignment\":\"center\"}", "--width", "120", "--dry-run");
        Assert.All(styled["annotation"]!["runs"]!.AsArray(), r => Assert.Equal(20, r!["size"]!.GetValue<double>()));
        Assert.Contains(styled["annotation"]!["runs"]!.AsArray(), r => r!["bold"]!.GetValue<bool>()); // the bold run stays bold
        Assert.Equal(120, styled["annotation"]!["width"]!.GetValue<double>());
        Assert.Equal("center", styled["annotation"]!["alignment"]!.GetValue<string>());

        // Geometry: --size of an image, --at of a stroke, --rotate of a shape (a quarter turn swaps its sides).
        var image = Write("annot", "update", made, "--source-key", "e/img", "--size", "40,10", "--dry-run");
        Assert.Equal([400, 700, 40, 10], Numbers(image["annotation"]!["bounds"]));
        var ink = Write("annot", "update", made, "--source-key", "e/ink", "--at", "200,300", "--width", "4", "--dry-run");
        Assert.Equal([200, 300], Numbers(ink["annotation"]!["bounds"]).Take(2).ToArray());
        Assert.Equal(4, ink["annotation"]!["lineWidth"]!.GetValue<double>());
        var shape = Write("annot", "update", made, "--source-key", "e/shape", "--rotate", "90", "--fill", "#FF0000", "--dry-run");
        var b = Numbers(shape["annotation"]!["bounds"]);
        Assert.Equal(42, b[2], 2);
        Assert.Equal(62, b[3], 2);
        Assert.Equal("#FF0000", shape["annotation"]!["fill"]!.GetValue<string>());
        var mark = Write("annot", "update", made, "--source-key", "e/mark", "--color", "#00ff00", "--height", "10", "--dry-run");
        Assert.Equal("#00FF00", mark["annotation"]!["color"]!.GetValue<string>());
        Assert.Equal(10, mark["annotation"]!["height"]!.GetValue<double>());

        // The same edits in one apply reopen in the app as its own editable writing.
        string updated = Out("updated.pdf");
        var edits = Apply(made, Plan("edits.json", """
            [ { "op": "annot.update", "sourceKey": "e/text", "text": "바뀐 **글**", "style": { "size": 16 } },
              { "op": "annot.update", "sourceKey": "e/shape", "rotate": 30, "borderWidth": 4 },
              { "op": "annot.update", "sourceKey": "e/img", "size": [40, 10], "move": [-5, 5] },
              { "op": "annot.update", "sourceKey": "e/ink", "scale": 2 },
              { "op": "annot.update", "sourceKey": "e/note", "text": "고친 메모", "at": [480, 520] },
              { "op": "annot.update", "sourceKey": "e/mark", "color": "#00FF00" } ]
            """), "-o", updated);
        Assert.Equal(6, edits["summary"]!["annotations"]!["updated"]!.AsArray().Count);
        using (var document = await OpenDocument.OpenPdfAsync(updated))
        {
            Assert.Equal(7, document.Source!.AppData.RestoredCount);
            Assert.Equal(0, document.Source.AppData.ReadOnlyCount);
            Assert.Equal(6, document.Session.Current.LiveTags.Count());
        }

        // What a kind cannot take is refused.
        WriteFails("NOT_TRANSFORMABLE", ExitCodes.InvalidInput, "annot", "update", made, "--source-key", "e/mark", "--move", "5,5", "--dry-run");
        WriteFails("NOT_TRANSFORMABLE", ExitCodes.InvalidInput, "annot", "update", made, "--source-key", "e/note", "--rotate", "30", "--dry-run");
        WriteFails("INVALID_ARGUMENT", ExitCodes.InvalidInput, "annot", "update", made, "--source-key", "e/ink", "--fill", "#FF0000", "--dry-run");
        WriteFails("INVALID_ARGUMENT", ExitCodes.InvalidInput, "annot", "update", made, "--source-key", "e/ink", "--dry-run");
        WriteFails("ANNOTATION_NOT_FOUND", ExitCodes.InvalidInput, "annot", "update", made, "--source-key", "e/none", "--color", "#000000", "--dry-run");

        // The user's own stroke is read-only unless --allow-untagged (and then it stays untagged).
        WriteFails("FORBIDDEN", ExitCodes.Unsatisfiable, "annot", "update", made, "--id", inkId, "--color", "#FF0000", "--dry-run");
        WriteFails("FORBIDDEN", ExitCodes.Unsatisfiable, "annot", "delete", made, "--id", inkId, "--dry-run");
        WriteFails("FORBIDDEN", ExitCodes.Unsatisfiable, "annot", "order", made, "--id", inkId, "--to", "front", "--dry-run");
        WriteFails("FORBIDDEN", ExitCodes.Unsatisfiable, "annot", "lock", made, "--id", inkId, "--dry-run");
        var allowed = Write("annot", "update", made, "--id", inkId, "--color", "#FF0000", "--allow-untagged", "--dry-run");
        Assert.Equal("#FF0000", allowed["annotation"]!["color"]!.GetValue<string>());
        Assert.Null(allowed["annotation"]!["tags"]);

        // Order: to the back (under the user's stroke), then to a position.
        var back = Write("annot", "order", made, "--source-key", "e/text", "--to", "back", "--dry-run");
        Assert.Equal(0, back["annotation"]!["z"]!.GetValue<int>());
        var third = Write("annot", "order", made, "--source-key", "e/text", "--to", "3", "--dry-run");
        Assert.Equal(3, third["annotation"]!["z"]!.GetValue<int>());

        // Lock: a locked annotation is not changed; deleting its group needs --include-locked.
        string locked = Out("locked.pdf");
        Assert.True(Write("annot", "lock", made, "--source-key", "e/shape", "-o", locked)["annotation"]!["locked"]!.GetValue<bool>());
        WriteFails("LOCKED_ANNOTATION", ExitCodes.Unsatisfiable, "annot", "update", locked, "--source-key", "e/shape", "--move", "5,0", "--dry-run");
        WriteFails("LOCKED_ANNOTATION", ExitCodes.Unsatisfiable, "annot", "delete", locked, "--group-key", "e", "--dry-run");

        // Copy: to another page with a new key (keeps the group, unlocked); on its own page it steps 10 pt aside.
        var copy = Write("annot", "copy", locked, "--from-key", "e/shape", "--to", "3", "--source-key", "e/shape-copy", "--dry-run");
        Assert.Equal(3, copy["annotation"]!["page"]!.GetValue<int>());
        Assert.NotEqual(copy["source"]!["id"]!.GetValue<string>(), Id(copy));
        Assert.Equal("e", copy["annotation"]!["tags"]!["groupKey"]!.GetValue<string>());
        Assert.False(copy["annotation"]!["locked"]!.GetValue<bool>());
        var note = CliRunner.Ok("annot", "list", locked, "--source-key", "e/note")["annotations"]![0]!;
        var noteCopy = Write("annot", "copy", locked, "--from-key", "e/note", "--dry-run");
        Assert.Equal(Numbers(note["bounds"]).Take(2).Select(v => v + 10).ToArray(), Numbers(noteCopy["annotation"]!["bounds"]).Take(2).ToArray());
        Assert.Null(noteCopy["annotation"]!["tags"]!["sourceKey"]);
        WriteFails("NOT_TRANSFORMABLE", ExitCodes.InvalidInput, "annot", "copy", locked, "--from-key", "e/mark", "--to", "2", "--dry-run");
        var userCopy = Write("annot", "copy", locked, "--id", inkId, "--at", "300,650", "--dry-run"); // reading the user's stroke is fine
        Assert.Equal("ai", userCopy["annotation"]!["tags"]!["author"]!.GetValue<string>());

        // Delete by group key, locked ones too: the image's resource goes with it; the rest stays.
        string deleted = Out("deleted.pdf");
        var gone = Write("annot", "delete", locked, "--group-key", "e", "--include-locked", "-o", deleted);
        Assert.Equal(4, gone["count"]!.GetValue<int>());
        Assert.Equal(["e/img", "e/mark", "e/shape", "e/text"], gone["deleted"]!.AsArray().Select(d => d!["sourceKey"]!.GetValue<string>()).Order().ToArray());
        var left = CliRunner.Ok("annot", "list", deleted)["annotations"]!.AsArray();
        Assert.Equal(3, left.Count); // the user's stroke, the note, the pen stroke
        Assert.Contains(left, a => a!["id"]!.GetValue<string>() == inkId);
        Assert.Equal(0, Write("annot", "delete", deleted, "--group-key", "e", "--dry-run")["count"]!.GetValue<int>());
        using var reopened = await OpenDocument.OpenPdfAsync(deleted);
        Assert.Empty(reopened.Session.Current.Resources);
        Assert.Equal(2, reopened.Session.Current.Tags.Count);
    }

    [Fact]
    public void CheckReportsProblemsOfTheSelectedAnnotations()
    {
        string made = Out("check.pdf");
        Apply(sample, Plan("check-make.json", """
            { "ops": [
              { "op": "add.text", "page": 1, "rect": [60, 600, 200, 0], "text": "겹침 하나", "sourceKey": "c/t1", "groupKey": "g1" },
              { "op": "add.text", "page": 1, "rect": [100, 605, 200, 0], "text": "겹침 둘", "sourceKey": "c/t2", "groupKey": "g1" },
              { "op": "add.text", "page": 1, "rect": [500, 820, 200, 0], "text": "밖으로", "overflow": "allow", "sourceKey": "c/t3" },
              { "op": "add.text", "page": 1, "rect": [60, 700, 150, 0], "text": [{ "text": "작은 글", "size": 6 }, { "text": " 보통" }], "sourceKey": "c/t4" },
              { "op": "add.text", "page": 1, "rect": [300, 700, 100, 0], "text": "A􏿽B", "sourceKey": "c/t5" },
              { "op": "add.mark", "page": 1, "chars": { "start": 0, "end": 3 }, "sourceKey": "c/m" }
            ] }
            """), "-o", made);
        string rules = Plan("rules.json", """
            [
              { "rule": "overlapAmong", "select": { "kinds": ["text"], "groupKeys": ["g1"] } },
              { "rule": "overlapRegions", "select": { "kinds": ["mark"] }, "regions": [[50, 90, 200, 40]] },
              { "rule": "overlapRegions", "select": { "kinds": ["text"] }, "regions": [[50, 90, 200, 40]] },
              { "rule": "outsidePage", "select": { "tagged": true } },
              { "rule": "minFontSize", "size": 8 },
              { "rule": "missingGlyph", "level": "warn" },
              { "rule": "textOverflow", "select": { "sourceKeys": ["c/t1"] }, "maxHeight": 10 }
            ]
            """);
        var check = CliRunner.Ok("check", made, "--rules", "@" + rules);
        var problems = check["problems"]!.AsArray();
        string[] Keys(int rule) => problems.Where(p => p!["ruleIndex"]!.GetValue<int>() == rule).Select(p => p!["sourceKey"]!.GetValue<string>()).ToArray();
        Assert.Equal(["c/t1"], Keys(0));
        Assert.Equal("c/t2", problems.First(p => p!["ruleIndex"]!.GetValue<int>() == 0)!["details"]!["otherSourceKey"]!.GetValue<string>());
        Assert.Equal(["c/m"], Keys(1));
        Assert.Empty(Keys(2));
        Assert.Equal(["c/t3"], Keys(3));
        Assert.True(problems.First(p => p!["ruleIndex"]!.GetValue<int>() == 3)!["details"]!["overflow"]!["right"]!.GetValue<double>() > 0);
        Assert.Equal(["c/t4"], Keys(4));
        Assert.Equal("작은 글", problems.First(p => p!["ruleIndex"]!.GetValue<int>() == 4)!["details"]!["ranges"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(["c/t5"], Keys(5));
        Assert.Equal("warn", problems.First(p => p!["ruleIndex"]!.GetValue<int>() == 5)!["level"]!.GetValue<string>());
        Assert.Equal(["c/t1"], Keys(6));
        Assert.True(problems.First(p => p!["ruleIndex"]!.GetValue<int>() == 6)!["details"]!["tall"]!.GetValue<bool>());
        Assert.False(check["passed"]!.GetValue<bool>());
        // Other pages have none; a rule with a mistake is refused.
        Assert.Equal(0, CliRunner.Ok("check", made, "-p", "2", "--rules", "@" + rules)["count"]!.GetValue<int>());
        CliRunner.Fails("INVALID_ARGUMENT", ExitCodes.InvalidInput, "check", made, "--rules", "[{\"rule\":\"overlapAmong\",\"regions\":[[0,0,1,1]]}]");
        CliRunner.Fails("INVALID_ARGUMENT", ExitCodes.InvalidInput, "check", made, "--rules", "[{\"rule\":\"minFontSize\"}]");
    }

    /// <summary>The cli_plan 5.1 example: a copy of page 3 shrunk to 80 % at the top left, a mark by characters, a text box and an arrow in the freed margin.</summary>
    internal const string LecturePlan = """
        { "ops": [
          { "op": "page.duplicate", "page": 3, "as": "p3a" },
          { "op": "page.shrink", "page": "$p3a", "scale": 0.8, "anchor": "top-left" },
          { "op": "add.mark", "page": "$p3a", "chars": { "start": 0, "end": 4 }, "kind": "highlight", "color": "#FFEB3B",
            "sourceKey": "lecture-A/seg-012/highlight-01", "groupKey": "lecture-A/seg-012" },
          { "op": "add.text", "page": "$p3a", "rect": [410, 300, 80, 0], "overflow": "error",
            "sourceKey": "lecture-A/seg-012/text", "groupKey": "lecture-A/seg-012",
            "text": [{ "text": "베르누이 방정식은 " }, { "text": "유선을 따라", "bold": true }, { "text": " 에너지 보존을 적용한 식이다." }] },
          { "op": "add.line", "page": "$p3a", "points": [[405, 310], [300, 210]], "endArrow": true,
            "sourceKey": "lecture-A/seg-012/link-01", "groupKey": "lecture-A/seg-012" }
        ] }
        """;

    [Fact]
    public void ApplyIsAllOrNothingAndFollowsTheShrunkPage()
    {
        // A failing last operation: its error with the operation's index, and no output file.
        string output = Out("lecture.pdf");
        var bad = JsonNode.Parse(LecturePlan)!.AsObject();
        bad["ops"]!.AsArray().Add(new JsonObject { ["op"] = "add.mark", ["page"] = 1, ["find"] = ProbeSamplePdf.CliRepeatedTerm });
        var error = WriteFails("AMBIGUOUS_MATCH", ExitCodes.Unsatisfiable, "apply", sample, Plan("bad.json", bad.ToJsonString()), "-o", output);
        Assert.Equal(5, error["error"]!["details"]!["opIndex"]!.GetValue<int>());
        Assert.Equal("add.mark", error["error"]!["details"]!["op"]!.GetValue<string>());
        Assert.Equal(3, error["error"]!["details"]!["matches"]!.AsArray().Count);
        Assert.False(File.Exists(output));

        var applied = Apply(sample, Plan("lecture.json", LecturePlan), "-o", output);
        Assert.Equal(7, applied["pageCount"]!.GetValue<int>());
        var summary = applied["summary"]!;
        Assert.Equal(4, summary["pages"]!["created"]![0]!["page"]!.GetValue<int>());
        Assert.Equal(4, summary["pages"]!["transformed"]![0]!["page"]!.GetValue<int>());
        Assert.Equal(3, summary["annotations"]!["created"]!.AsArray().Count);
        Assert.All(summary["annotations"]!["created"]!.AsArray(), a => Assert.Equal(4, a!["page"]!.GetValue<int>()));
        Assert.Empty(summary["annotations"]!["replaced"]!.AsArray());

        // The mark given by characters lies on the shrunk text: find on the result gives the same quads.
        var mark = applied["ops"]![2]!;
        string phrase = mark["text"]!.GetValue<string>();
        Assert.Equal("Page", phrase);
        var found = CliRunner.Ok("find", output, "-p", "4", "--text", phrase)["matches"]!.AsArray().Single()!;
        Assert.True(JsonNode.DeepEquals(found["quads"], mark["annotation"]!["quads"]));
        var original = CliRunner.Ok("find", output, "-p", "3", "--text", phrase)["matches"]!.AsArray().Single()!;
        var q4 = Numbers(found["quads"]![0]![0]);
        var q3 = Numbers(original["quads"]![0]![0]);
        Assert.Equal(q3[0] * 0.8, q4[0], 1); // shrunk toward the top-left corner
        Assert.Equal(q3[1] * 0.8, q4[1], 1);
        Assert.Equal(410, Numbers(applied["ops"]![3]!["rect"])[0]);
    }

    [Fact]
    public void ReplacingOnlyTheTextKeepsTheRestAndRegeneratingGivesTheSame()
    {
        // Text, mark and connector of one paragraph; then only the text again with new words.
        string first = Out("para1.pdf"), second = Out("para2.pdf");
        Apply(sample, Plan("para.json", """
            { "batchId": "run-1", "ops": [
              { "op": "add.text", "page": 1, "rect": [380, 160, 180, 0], "text": "처음 설명", "sourceKey": "p/text", "groupKey": "p" },
              { "op": "add.mark", "page": 1, "find": "유선을 따라", "sourceKey": "p/mark", "groupKey": "p" },
              { "op": "add.line", "page": 1, "points": [[378, 170], [300, 170]], "endArrow": true, "sourceKey": "p/link", "groupKey": "p" }
            ] }
            """), "-o", first);
        var again = Apply(first, Plan("para-text.json", """
            { "batchId": "run-2", "ops": [
              { "op": "add.text", "page": 1, "rect": [380, 160, 180, 0], "text": "고친 설명: **유선을 따라**", "sourceKey": "p/text", "groupKey": "p" }
            ] }
            """), "-o", second);
        Assert.Empty(again["summary"]!["annotations"]!["created"]!.AsArray());
        Assert.Equal("p/text", again["summary"]!["annotations"]!["replaced"]!.AsArray().Single()!["sourceKey"]!.GetValue<string>());
        var before = CliRunner.Ok("annot", "list", first)["annotations"]!.AsArray();
        var after = CliRunner.Ok("annot", "list", second)["annotations"]!.AsArray();
        Assert.Equal(3, after.Count);
        Assert.True(JsonNode.DeepEquals(before[1], after[1])); // the mark, its ID and batch untouched
        Assert.True(JsonNode.DeepEquals(before[2], after[2])); // the connector
        Assert.Equal(before[0]!["id"]!.GetValue<string>(), after[0]!["id"]!.GetValue<string>());
        Assert.Equal("고친 설명: 유선을 따라", after[0]!["text"]!.GetValue<string>());
        Assert.NotEmpty(CliCompare.Differences(first, second)); // the comparison sees the change

        // The whole plan from the source twice gives the same document; again on the last output keeps the keyed
        // annotations (only the page duplicate runs again, cli_plan 8).
        string a = Out("regen-a.pdf"), b = Out("regen-b.pdf"), c = Out("regen-c.pdf");
        string plan = Plan("regen.json", LecturePlan);
        Apply(sample, plan, "-o", a, "--batch-id", "run-a");
        Apply(sample, plan, "-o", b, "--batch-id", "run-b");
        Assert.Empty(CliCompare.Differences(a, b));
        var onAgain = Apply(a, plan, "-o", c);
        Assert.Equal(3, onAgain["summary"]!["annotations"]!["replaced"]!.AsArray().Count);
        Assert.Equal(CliRunner.Ok("annot", "list", a, "--tag", "tagged")["count"]!.GetValue<int>(), CliRunner.Ok("annot", "list", c, "--tag", "tagged")["count"]!.GetValue<int>());
        Assert.Equal(8, onAgain["pageCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task ApplyRefusesWhatItCannotDoAndWritesNothing()
    {
        string output = Out("refused.pdf");
        void Refused(string code, int exit, int opIndex, string json)
        {
            var e = WriteFails(code, exit, "apply", sample, Plan("refused.json", json), "-o", output);
            if (opIndex >= 0) Assert.Equal(opIndex, e["error"]!["details"]!["opIndex"]!.GetValue<int>());
            Assert.False(File.Exists(output));
        }
        Refused("DUPLICATE_SOURCE_KEY", ExitCodes.Unsatisfiable, 1, """
            [ { "op": "add.note", "page": 1, "at": [10, 10], "text": "a", "sourceKey": "k" },
              { "op": "add.note", "page": 2, "at": [10, 10], "text": "b", "sourceKey": "k" } ]
            """);
        Refused("DUPLICATE_SOURCE_KEY", ExitCodes.Unsatisfiable, 1, """
            [ { "op": "add.note", "page": 1, "at": [10, 10], "text": "a", "sourceKey": "k" },
              { "op": "annot.copy", "fromKey": "k", "to": 2, "sourceKey": "k" } ]
            """);
        string noted = Out("noted.pdf");
        Write("add", "note", sample, "-p", "1", "--at", "10,10", "--text", "a", "--source-key", "k", "-o", noted);
        var mismatch = WriteFails("SOURCE_KEY_KIND_MISMATCH", ExitCodes.Unsatisfiable, "apply", noted,
            Plan("mismatch.json", """[ { "op": "add.ink", "page": 1, "points": [[0, 0], [5, 5]], "sourceKey": "k" } ]"""), "-o", output);
        Assert.Equal(0, mismatch["error"]!["details"]!["opIndex"]!.GetValue<int>());
        Refused("INVALID_ARGUMENT", ExitCodes.InvalidInput, 0, """[ { "op": "add.note", "page": 1, "at": [10, 10], "text": "a", "colour": "#FF0000" } ]""");
        Refused("INVALID_ARGUMENT", ExitCodes.InvalidInput, 0, """[ { "op": "add.sticker", "page": 1 } ]""");
        Refused("INVALID_ARGUMENT", ExitCodes.InvalidInput, 0, """[ { "op": "add.note", "page": 1, "at": [10, 10], "text": "a", "as": "n" } ]""");
        Refused("PAGE_NOT_FOUND", ExitCodes.InvalidInput, 0, """[ { "op": "add.note", "page": "$nope", "at": [10, 10], "text": "a" } ]""");
        Refused("PAGE_NOT_FOUND", ExitCodes.InvalidInput, 1, """
            [ { "op": "page.delete", "page": 2 }, { "op": "add.note", "page": 2, "at": [10, 10], "text": "a" } ]
            """);
        Refused("HASH_MISMATCH", ExitCodes.Locked, -1, """{ "expectHash": "sha256:00", "ops": [] }""");
        Refused("CHECK_FAILED", ExitCodes.Unsatisfiable, -1, """
            { "ops": [ { "op": "add.text", "page": 1, "rect": [60, 100, 200, 0], "text": "제목 위", "sourceKey": "t" } ],
              "guards": [ { "rule": "overlapRegions", "select": { "tagged": true }, "regions": [[0, 0, 595, 400]] } ] }
            """);
        var (user, inkId) = await WithUserInk();
        var forbidden = WriteFails("FORBIDDEN", ExitCodes.Unsatisfiable, "apply", user, Plan("forbidden.json", $$"""[ { "op": "annot.delete", "id": "{{inkId}}" } ]"""), "-o", output);
        Assert.Equal(0, forbidden["error"]!["details"]!["opIndex"]!.GetValue<int>());

        // Page numbers are the input's: after a page is put in front, page 1 is still the first input page. Defaults
        // fill fields (a style merges field by field); a warn guard reports without refusing.
        var applied = Apply(sample, Plan("numbers.json", """
            { "defaults": { "add.text": { "style": { "size": 20, "color": "#1E88E5" }, "groupKey": "d" } },
              "ops": [
                { "op": "page.insert-blank", "after": 0, "as": ["blank"] },
                { "op": "add.text", "page": 1, "rect": [320, 60, 180, 0], "text": "첫 쪽", "style": { "bold": true } },
                { "op": "add.note", "page": "$blank", "at": [20, 20], "text": "빈 쪽 메모" }
              ],
              "guards": [ { "rule": "overlapRegions", "level": "warn", "select": { "kinds": ["note"] }, "regions": [[0, 0, 50, 50]] } ] }
            """), "--dry-run", "--preview-dir", Out("previews"));
        var textOp = applied["ops"]![1]!["annotation"]!;
        Assert.Equal(2, textOp["page"]!.GetValue<int>());
        Assert.Equal(20, textOp["runs"]![0]!["size"]!.GetValue<double>());
        Assert.True(textOp["runs"]![0]!["bold"]!.GetValue<bool>());
        Assert.Equal("#1E88E5", textOp["runs"]![0]!["color"]!.GetValue<string>());
        Assert.Equal("d", textOp["tags"]!["groupKey"]!.GetValue<string>());
        Assert.Equal(1, applied["ops"]![2]!["annotation"]!["page"]!.GetValue<int>());
        Assert.Equal(1, applied["guards"]!["count"]!.GetValue<int>());
        Assert.Null(applied["output"]);
        var previews = applied["previews"]!.AsArray();
        Assert.Equal([1, 2], previews.Select(p => p!["page"]!.GetValue<int>()).Order().ToArray());
        Assert.All(previews, p => Assert.True(File.Exists(p!["path"]!.GetValue<string>())));
    }
}
