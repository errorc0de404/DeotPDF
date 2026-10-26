using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Nodes;
using PdfNotes.Cli;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Tests.Cli;

/// <summary>
/// CLI C3 (cli_plan 3.5, 4.5, 6, 7): storage format v7 tags through the app's save path, the write safety rules and the
/// page commands with their <c>pageTransform</c>.
/// </summary>
public sealed class CliWriteTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    private readonly string sample;
    private readonly string appFolder;

    public CliWriteTests()
    {
        Directory.CreateDirectory(folder);
        sample = Path.Combine(folder, "cli-sample.pdf");
        appFolder = Path.Combine(folder, "app");
        ProbeSamplePdf.CreateCli(sample);
    }

    public void Dispose() => Directory.Delete(folder, true);

    private string Out(string name) => Path.Combine(folder, name);

    /// <summary>A writing command with a private app folder, so a real app's window markers never interfere.</summary>
    private JsonObject Write(params string[] args) => CliRunner.Ok([.. args, "--app-folder", appFolder]);

    private JsonObject WriteFails(string code, int exit, params string[] args) => CliRunner.Fails(code, exit, [.. args, "--app-folder", appFolder]);

    private static int Version(byte[] json) => JsonNode.Parse(json)!["schemaVersion"]!.GetValue<int>();

    private static ShapeAnnotation Shape(double x, double y) =>
        new(Guid.NewGuid(), false, Affine.Identity, ShapeKind.Rectangle, new Rect2(x, y, 40, 30), [], ShapeStyle.Default);

    [Fact]
    public void SchemaV7WritesTagsOnlyWhenThereAreAny()
    {
        var model = DocumentModel.Create([new PageSetup(595, 842, 0, PageSource.Generated, PageBackground.Blank)]);
        var a = Shape(10, 10);
        var b = Shape(100, 10);
        model = model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Annotations = [a, b] }) };
        Assert.Equal(6, Version(SchemaV7.Serialize(model).Header));

        var tag = new AnnotationTag("ai", "run-1", "lecture-A/seg-1/box", "lecture-A/seg-1");
        var tagged = model with { Tags = ImmutableDictionary<Guid, AnnotationTag>.Empty.Add(a.Id, tag).Add(Guid.NewGuid(), new AnnotationTag("ai")) };
        var data = SchemaV7.Serialize(tagged);
        Assert.Equal(7, Version(data.Header));
        Assert.All(data.Annotations, e => Assert.Equal(7, Version(e.Envelope)));
        var read = SchemaV7.Read(data.Header, data.Annotations.Select(e => e.Envelope).ToArray(), _ => null);
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus);
        Assert.Equal(tag, Assert.Single(read.Model!.Tags).Value); // the tag of a missing annotation is not written
        Assert.Equal(a.Id, read.Model.Tags.Keys.Single());
        Assert.Equal(data.Header, SchemaV7.Serialize(read.Model).Header);
        // Older readers keep a v7 file read-only instead of dropping its tags.
        Assert.Equal(HeaderStatus.Unsupported, SchemaV6.Read(data.Header, [], _ => null).HeaderStatus);
        // A malformed tag table is not guessed at.
        var header = JsonNode.Parse(data.Header)!;
        header["tags"]![0]!["author"] = "";
        var bad = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(header);
        Assert.Equal(HeaderStatus.Invalid, SchemaV7.Read(bad, data.Annotations.Select(e => e.Envelope).ToArray(), _ => null).HeaderStatus);
    }

    [Fact]
    public async Task TagsSurviveAppEditsAndLeaveWithDeletedOrCopiedAnnotations()
    {
        string first = Out("tagged.pdf"), second = Out("edited.pdf");
        Guid moved, deleted, plain;
        using (var document = await OpenDocument.OpenPdfAsync(sample))
        {
            var page = document.Session.Current.Pages[0];
            var one = Shape(100, 500); var two = Shape(200, 500); var three = Shape(300, 500);
            (moved, deleted, plain) = (one.Id, two.Id, three.Id);
            document.Session.Execute(new EditCommand("test", [
                new InsertAnnotationOperation(page.Id, 0, one), new InsertAnnotationOperation(page.Id, 1, two), new InsertAnnotationOperation(page.Id, 2, three),
                new SetTagOperation(one.Id, null, new AnnotationTag("ai", "run-1", "k/one", "g")),
                new SetTagOperation(two.Id, null, new AnnotationTag("ai", "run-1", "k/two", "g"))]));
            await document.SaveAsync(first);
        }
        var list = CliRunner.Ok("annot", "list", first)["annotations"]!.AsArray();
        Assert.Equal("k/one", list.Single(a => a!["id"]!.GetValue<string>() == moved.ToString())!["tags"]!["sourceKey"]!.GetValue<string>());
        Assert.Null(list.Single(a => a!["id"]!.GetValue<string>() == plain.ToString())!["tags"]);
        Assert.Equal(2, CliRunner.Ok("annot", "list", first, "--tag", "tagged")["count"]!.GetValue<int>());
        Assert.Equal(1, CliRunner.Ok("annot", "list", first, "--source-key", "k/two")["count"]!.GetValue<int>());
        Assert.Equal(2, CliRunner.Ok("annot", "list", first, "--group-key", "g")["count"]!.GetValue<int>());
        Assert.Equal(2, CliRunner.Ok("render", first, "-p", "1", "-o", Out("t.png"), "--annotations", "batch:run-1")["annotationsDrawn"]!.GetValue<int>());
        Assert.Equal(1, CliRunner.Ok("render", first, "-p", "1", "-o", Out("u.png"), "--annotations", "untagged")["annotationsDrawn"]!.GetValue<int>());

        // The app opens the file, moves one, deletes one and copies one: move keeps the tag, delete drops it, the copy has none.
        Guid copy;
        using (var document = await OpenDocument.OpenPdfAsync(first))
        {
            var model = document.Session.Current;
            Assert.Equal(2, model.Tags.Count);
            var shape = (ShapeAnnotation)model.FindAnnotation(moved)!.Annotation;
            document.Session.Execute(DocumentEdits.UpdateAnnotations(model, [shape with { Transform = Affine.Translation(15, 25) }]));
            document.Session.Execute(DocumentEdits.DeleteAnnotations(document.Session.Current, [deleted]));
            var clip = AnnotationClipboard.Capture(document.Session.Current, document.Session.Current.Pages[0].Id, [moved]);
            var paste = clip.Paste(document.Session.Current, document.Session.Current.Pages[0].Id, new Point2(10, 10));
            document.Session.Execute(paste.Command);
            copy = paste.Ids[0];
            await document.SaveAsync(second);
        }
        var after = CliRunner.Ok("annot", "list", second)["annotations"]!.AsArray();
        Assert.Equal(3, after.Count);
        Assert.Equal("k/one", after.Single(a => a!["id"]!.GetValue<string>() == moved.ToString())!["tags"]!["sourceKey"]!.GetValue<string>());
        Assert.Null(after.Single(a => a!["id"]!.GetValue<string>() == copy.ToString())!["tags"]);
        Assert.Equal(1, CliRunner.Ok("annot", "list", second, "--tag", "tagged")["count"]!.GetValue<int>());
    }

    [Fact]
    public void WritingCommandsGuardTheInputAndTheOutput()
    {
        string output = Out("rotated.pdf");
        WriteFails("INVALID_ARGUMENT", ExitCodes.InvalidInput, "page", "rotate", sample, "-p", "1", "--deg", "90");
        WriteFails("SAME_AS_INPUT", ExitCodes.InvalidInput, "page", "rotate", sample, "-p", "1", "--deg", "90", "-o", sample);
        WriteFails("HASH_MISMATCH", ExitCodes.Locked, "page", "rotate", sample, "-p", "1", "--deg", "90", "-o", output, "--expect-hash", "sha256:00");
        string hash = CliRunner.Ok("info", sample)["sourceHash"]!.GetValue<string>();
        var done = Write("page", "rotate", sample, "-p", "1", "--deg", "90", "-o", output, "--expect-hash", hash);
        Assert.True(File.Exists(output));
        Assert.Equal(done["outputHash"]!.GetValue<string>(), CliRunner.Ok("info", output)["sourceHash"]!.GetValue<string>());
        WriteFails("OUTPUT_EXISTS", ExitCodes.InvalidInput, "page", "rotate", sample, "-p", "1", "--deg", "90", "-o", output);
        Write("page", "rotate", sample, "-p", "1", "--deg", "180", "-o", output, "--overwrite");
        Assert.Equal(180, CliRunner.Ok("info", output)["pages"]![0]!["rotation"]!.GetValue<int>());
        // A dry run writes nothing but reports the same result.
        var dry = Write("page", "rotate", sample, "-p", "2", "--deg", "90", "--dry-run");
        Assert.Null(dry["output"]);
        Assert.Equal(90, dry["pageTransforms"]![0]!["before"]!["rotation"]!.GetValue<int>());
        Assert.Equal(180, dry["pageTransforms"]![0]!["after"]!["rotation"]!.GetValue<int>());

        // A file a running PdfNotes window shows is refused, as input or as output.
        var store = new BackupStore(appFolder);
        store.SetRecoverable(null);
        store.SetOpenDocument(sample, 4242);
        try
        {
            WriteFails("FILE_OPEN_IN_APP", ExitCodes.Locked, "page", "rotate", sample, "-p", "1", "--deg", "90", "-o", Out("x.pdf"));
            store.SetOpenDocument(output, 4242);
            WriteFails("FILE_OPEN_IN_APP", ExitCodes.Locked, "page", "rotate", sample, "-p", "1", "--deg", "90", "-o", output, "--overwrite");
        }
        finally { store.End(); }
        Write("page", "rotate", sample, "-p", "1", "--deg", "90", "-o", Out("x.pdf"));
    }

    [Fact]
    public void PageTransformsPutKnownPointsWhereTheTransformedPageShowsThem()
    {
        // Rotate page 2, shrink page 3 (crop origin ≠ 0) to 80% at the top-left, resize page 4 (turned 270°) with the content fitted.
        string rotated = Out("r.pdf"), shrunk = Out("s.pdf"), resized = Out("z.pdf");
        var cases = new (string Output, int Page, JsonObject Result)[]
        {
            (rotated, 2, Write("page", "rotate", sample, "-p", "2", "--deg", "90", "-o", rotated)),
            (shrunk, 3, Write("page", "shrink", sample, "-p", "3", "--scale", "0.8", "--anchor", "top-left", "-o", shrunk)),
            (resized, 4, Write("page", "resize", sample, "-p", "4", "--size", "420,500", "--mode", "fit", "-o", resized)),
        };
        foreach (var (output, number, result) in cases)
        {
            var transform = Assert.Single(result["pageTransforms"]!.AsArray())!;
            var matrix = CliRunner.Matrix(transform["matrix"]);
            var inverse = CliRunner.Matrix(transform["inverse"]);
            string png = Out($"t{number}.png");
            var render = CliRunner.Ok("render", output, "-p", number.ToString(), "-o", png, "--dpi", "144");
            var image = CliRunner.LoadPng(png);
            var toPixel = CliRunner.Matrix(render["pageToPixel"]);
            Assert.Equal(transform["after"]!["width"]!.GetValue<double>(), CliRunner.Ok("info", output)["pages"]![number - 1]!["width"]!.GetValue<double>(), 0.001);
            foreach (var (x, y) in ProbeSamplePdf.CliPages[number - 1].Fiducials)
            {
                var moved = matrix.Transform(new(x, y));
                var expected = toPixel.Transform(moved);
                var found = CliRunner.RedCentroid(image, expected, 14) ?? throw new Xunit.Sdk.XunitException($"page {number}: no cross near {expected}");
                Assert.True(found.DistanceTo(expected) <= 1, $"page {number} point ({x},{y}): expected {expected}, cross at {found}");
                Assert.True(inverse.Transform(moved).DistanceTo(new(x, y)) < 1e-6);
            }
        }
        Assert.Equal(0.8, CliRunner.Matrix(cases[1].Result["pageTransforms"]![0]!["matrix"]).M11, 1e-9);
        Assert.True(CliRunner.Matrix(cases[0].Result["pageTransforms"]![0]!["matrix"]).IsIdentity);
    }

    [Fact]
    public async Task PageStructureCommandsGiveNewPageIdsAndAppReadableFiles()
    {
        var info = CliRunner.Ok("info", sample);
        var ids = info["pages"]!.AsArray().Select(p => p!["pageId"]!.GetValue<string>()).ToArray();

        string duplicated = Out("dup.pdf");
        var dup = Write("page", "duplicate", sample, "-p", "1", "--count", "2", "-o", duplicated);
        var created = dup["created"]!.AsArray().Select(c => c!["page"]!.GetValue<int>()).ToArray();
        Assert.Equal([2, 3], created);
        var dupInfo = CliRunner.Ok("info", duplicated);
        Assert.Equal("file", dupInfo["pageIds"]!.GetValue<string>());
        Assert.Equal(8, dupInfo["pageCount"]!.GetValue<int>());
        Assert.Equal(ids[0], dupInfo["pages"]![0]!["pageId"]!.GetValue<string>()); // the derived IDs are kept in the file
        Assert.Equal(ids[1], dupInfo["pages"]![3]!["pageId"]!.GetValue<string>());

        string blank = Out("blank.pdf");
        var inserted = Write("page", "insert-blank", sample, "--after", "0", "--background", "grid", "-o", blank);
        var first = CliRunner.Ok("info", blank)["pages"]![0]!;
        Assert.Equal("generated", first["source"]!.GetValue<string>());
        Assert.Equal(595.28, first["width"]!.GetValue<double>(), 0.01);
        Assert.Equal(inserted["created"]![0]!["pageId"]!.GetValue<string>(), first["pageId"]!.GetValue<string>());

        WriteFails("LAST_PAGE", ExitCodes.Unsatisfiable, "page", "delete", sample, "--pages", "1-6", "-o", Out("none.pdf"));
        string deleted = Out("deleted.pdf");
        Write("page", "delete", sample, "--pages", "2,4", "-o", deleted);
        Assert.Equal([ids[0], ids[2], ids[4], ids[5]], CliRunner.Ok("info", deleted)["pages"]!.AsArray().Select(p => p!["pageId"]!.GetValue<string>()).ToArray());

        string moved = Out("moved.pdf");
        Write("page", "move", sample, "--pages", "5,6", "--to", "1", "-o", moved);
        Assert.Equal([ids[4], ids[5], ids[0], ids[1], ids[2], ids[3]], CliRunner.Ok("info", moved)["pages"]!.AsArray().Select(p => p!["pageId"]!.GetValue<string>()).ToArray());

        string imported = Out("imported.pdf");
        var import = Write("page", "import", sample, "--from", sample, "--range", "2-3", "--after", "6", "-o", imported);
        Assert.Equal([7, 8], import["created"]!.AsArray().Select(c => c!["page"]!.GetValue<int>()).ToArray());
        var importInfo = CliRunner.Ok("info", imported)["pages"]!.AsArray();
        Assert.Equal(90, importInfo[6]!["rotation"]!.GetValue<int>());
        Assert.Contains("한글 English 0123456789", CliRunner.Ok("text", imported, "-p", "8", "--fields", "lines")["lines"]!.AsArray().Select(l => l!["text"]!.GetValue<string>()));

        string exported = Out("exported.pdf");
        var export = Write("page", "export", sample, "--pages", "2-3", "-o", exported);
        Assert.Equal(2, export["pageCount"]!.GetValue<int>());
        var exportInfo = CliRunner.Ok("info", exported)["pages"]!.AsArray();
        Assert.Equal([90, 0], exportInfo.Select(p => p!["rotation"]!.GetValue<int>()).ToArray());

        // Every result opens in the app, takes an edit, saves and reopens.
        foreach (string file in new[] { duplicated, blank, deleted, moved, imported, exported })
        {
            string resaved = Path.ChangeExtension(file, ".resaved.pdf");
            int pages;
            using (var document = await OpenDocument.OpenPdfAsync(file))
            {
                var page = document.Session.Current.Pages[^1];
                document.Session.Execute(DocumentEdits.AddAnnotation(document.Session.Current, page.Id, Shape(50, 60)));
                pages = document.Session.Current.Pages.Count;
                await document.SaveAsync(resaved);
            }
            using var reopened = await OpenDocument.OpenPdfAsync(resaved);
            Assert.Equal(pages, reopened.Session.Current.Pages.Count);
            Assert.Single(reopened.Session.Current.Pages[^1].Annotations);
        }
    }

    [Fact]
    public async Task FlattenAndPreviewWriteWhatTheAppWould()
    {
        string annotated = Out("annotated.pdf");
        using (var document = await OpenDocument.OpenPdfAsync(sample))
        {
            document.Session.Execute(DocumentEdits.AddAnnotation(document.Session.Current, document.Session.Current.Pages[0].Id, Shape(300, 600)));
            await document.SaveAsync(annotated);
        }
        string flat = Out("flat.pdf"), previews = Out("previews");
        var result = Write("page", "shrink", annotated, "--pages", "1,3", "--scale", "0.5", "-o", flat, "--flatten", "--preview", previews);
        Assert.True(result["flatten"]!.GetValue<bool>());
        var flatInfo = CliRunner.Ok("info", flat);
        Assert.Equal(0, flatInfo["appData"]!["restoredAnnotations"]!.GetValue<int>());
        Assert.Equal(0, CliRunner.Ok("annot", "list", flat)["count"]!.GetValue<int>());
        var shown = result["previews"]!.AsArray();
        Assert.Equal([1, 3], shown.Select(p => p!["page"]!.GetValue<int>()).ToArray());
        Assert.All(shown, p => Assert.True(File.Exists(p!["path"]!.GetValue<string>())));
        // The preview shows the shrunken shape where the transform puts it.
        var transform = CliRunner.Matrix(result["pageTransforms"]![0]!["matrix"]);
        var image = CliRunner.LoadPng(shown[0]!["path"]!.GetValue<string>());
        var center = CliRunner.Matrix(shown[0]!["pageToPixel"]).Transform(transform.Transform(new(320, 615)));
        int k = ((int)center.Y * image.Width + (int)center.X) * 4;
        Assert.True(image.Bgra[k] > image.Bgra[k + 2] + 40, "the light blue shape fill is at the transformed place");
    }
}
