using System.IO;
using System.Text.Json.Nodes;
using PdfNotes.Cli;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Text;

namespace PdfNotes.Tests.Cli;

/// <summary>
/// CLI C6 (cli_plan 1.1, 7): a draft the CLI made is finished in the app — opened, edited, saved, reopened — and the
/// app-saved file still reads and edits in the CLI: tags kept on what the user moved, gone with what the user deleted,
/// the user's new writing untagged and protected, and the plan applied again on top.
/// <c>PDFNOTES_C6_DRAFTS</c> (draft|plan pairs separated by <c>;</c>) runs the same round trip on real drafts as well.
/// </summary>
public sealed class CliRoundTripTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    private readonly string appFolder;

    public CliRoundTripTests()
    {
        Directory.CreateDirectory(folder);
        appFolder = Path.Combine(folder, "app");
    }

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public async Task AppFinishesTheDraftAndTheCliReadsItBack()
    {
        string sample = Path.Combine(folder, "cli-sample.pdf"), plan = Path.Combine(folder, "plan.json"), draft = Path.Combine(folder, "draft.pdf");
        ProbeSamplePdf.CreateCli(sample);
        File.WriteAllText(plan, CliEditTests.LecturePlan);
        CliRunner.Ok("apply", sample, plan, "-o", draft, "--app-folder", appFolder);
        await RoundTrip(draft, plan, "sample");

        foreach (string pair in (Environment.GetEnvironmentVariable("PDFNOTES_C6_DRAFTS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('|');
            await RoundTrip(parts[0], parts[1], Path.GetFileNameWithoutExtension(parts[0]));
        }
    }

    private async Task RoundTrip(string draft, string plan, string name)
    {
        string edited = Path.Combine(folder, name + "-app.pdf"), again = Path.Combine(folder, name + "-again.pdf");
        int tagged = CliRunner.Ok("annot", "list", draft, "--tag", "tagged")["count"]!.GetValue<int>();
        Guid moved, deleted, user;
        string movedKey;
        Rect2 movedBefore;
        int total;
        using (var document = await OpenDocument.OpenPdfAsync(draft))
        {
            Assert.Equal(0, document.Source!.AppData.ReadOnlyCount);
            var model = document.Session.Current;
            total = model.AllAnnotations.Count();
            Assert.Equal(tagged, document.Source.AppData.RestoredCount);
            // The user's finishing touches: move and recolor a text box, delete a mark, write with the pen.
            var location = model.Pages.SelectMany(p => p.Annotations.Select(a => (Page: p, Annotation: a)))
                .First(x => x.Annotation is TextBoxAnnotation && model.TagOf(x.Annotation.Id) is not null);
            var box = (TextBoxAnnotation)location.Annotation;
            moved = box.Id;
            movedKey = model.TagOf(box.Id)!.SourceKey!;
            movedBefore = AnnotationGeometry.Bounds([box], TextLayout.Bounds);
            var changed = (TextBoxAnnotation)AnnotationGeometry.Similarity(box, default, move: new(5, 7));
            changed = changed with { Runs = [.. changed.Runs.Select(r => r with { Color = new RgbColor(0x1E, 0x88, 0xE5) })] };
            Assert.True(document.Session.Execute(DocumentEdits.UpdateAnnotations(model, [changed])));
            deleted = document.Session.Current.AllAnnotations.First(a => a is PdfTextHighlight or PdfTextMarkup).Id;
            Assert.True(document.Session.Execute(DocumentEdits.DeleteAnnotations(document.Session.Current, [deleted])));
            var ink = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(20, 20), new(60, 30), new(90, 20)], 2, RgbColor.Black, 1);
            user = ink.Id;
            Assert.True(document.Session.Execute(DocumentEdits.AddAnnotation(document.Session.Current, location.Page.Id, ink)));
            await document.SaveAsync(edited);
        }

        using (var reopened = await OpenDocument.OpenPdfAsync(edited))
        {
            Assert.Equal(total, reopened.Source!.AppData.RestoredCount); // one deleted, one added
            Assert.Equal(0, reopened.Source.AppData.ReadOnlyCount);
        }
        var list = CliRunner.Ok("annot", "list", edited)["annotations"]!.AsArray().ToDictionary(a => a!["id"]!.GetValue<string>(), a => a!);
        Assert.Equal(tagged - 1, list.Values.Count(a => a!["tags"] is not null));
        Assert.False(list.ContainsKey(deleted.ToString()));
        var movedNow = list[moved.ToString()];
        Assert.Equal(movedKey, movedNow["tags"]!["sourceKey"]!.GetValue<string>()); // moving keeps the tag
        Assert.Equal(movedBefore.X + 5, movedNow["bounds"]![0]!.GetValue<double>(), 2);
        Assert.Equal("#1E88E5", movedNow["runs"]![0]!["color"]!.GetValue<string>());
        Assert.Null(list[user.ToString()]["tags"]);

        // The CLI still edits its own annotations there and leaves the user's alone.
        string app = Path.Combine(folder, "app-settings");
        CliRunner.Ok("annot", "update", edited, "--source-key", movedKey, "--style", "{\"size\":13}", "--dry-run", "--app-folder", app);
        CliRunner.Fails("FORBIDDEN", ExitCodes.Unsatisfiable, "annot", "delete", edited, "--id", user.ToString(), "--dry-run", "--app-folder", app);

        // The plan again on top: the deleted mark comes back, the keyed ones are replaced (no duplicates), the user's stroke stays.
        var applied = CliRunner.Ok("apply", edited, plan, "-o", again, "--app-folder", app);
        Assert.Equal(tagged, CliRunner.Ok("annot", "list", again, "--tag", "tagged")["count"]!.GetValue<int>());
        Assert.Contains(applied["summary"]!["annotations"]!["replaced"]!.AsArray(), a => a!["id"]!.GetValue<string>() == moved.ToString());
        Assert.Contains(CliRunner.Ok("annot", "list", again, "--tag", "untagged")["annotations"]!.AsArray(), a => a!["id"]!.GetValue<string>() == user.ToString());
    }
}
