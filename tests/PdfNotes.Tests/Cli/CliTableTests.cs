using System.IO;
using System.Text.Json.Nodes;
using PdfNotes.Cli;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Text;

namespace PdfNotes.Tests.Cli;

/// <summary>1.4 Task 11: tables in deot — <c>add table</c> (and <c>add.table</c> in apply, live), <c>annot list</c>, <c>text</c>, <c>find</c>.</summary>
public sealed class CliTableTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    private readonly string sample;
    private readonly string appFolder;

    public CliTableTests()
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

    private static string[][] Cells(JsonNode? node) => node!.AsArray().Select(r => r!.AsArray().Select(c => c!.GetValue<string>()).ToArray()).ToArray();

    private const string Vocabulary = """[["Word","Meaning"],["apple","사과"],["pear","배"]]""";

    [Fact]
    public void AddTableThenListShowsItsCells()
    {
        string output = Out("table.pdf");
        var added = Write("add", "table", sample, "-p", "1", "--x", "60", "--y", "80", "--cells", Vocabulary, "--column-widths", "90,140",
            "--source-key", "vocab", "-o", output);
        var annotation = added["annotation"]!;
        Assert.Equal("table", annotation["kind"]!.GetValue<string>());
        Assert.Equal("ai", annotation["tags"]!["author"]!.GetValue<string>());
        Assert.Equal(3, annotation["rows"]!.GetValue<int>());
        Assert.Equal(2, annotation["columns"]!.GetValue<int>());

        var list = CliRunner.Ok("annot", "list", output, "--kind", "table");
        var item = Assert.Single(list["annotations"]!.AsArray())!;
        Assert.Equal(new[] { new[] { "Word", "Meaning" }, ["apple", "사과"], ["pear", "배"] }, Cells(item["cells"]));
        Assert.Equal([90.0, 140.0], Numbers(item["columnWidths"]));
        Assert.Equal(3, Numbers(item["rowHeights"]).Length);
        Assert.True(Numbers(item["rowHeights"]).All(h => h >= TableAnnotation.DefaultRowHeight));
        Assert.True(item["header"]!.GetValue<bool>());
        Assert.Equal("#000000", item["lineColor"]!.GetValue<string>());
        var bounds = Numbers(item["bounds"]);
        Assert.Equal(60 - TableStyle.Default.LineWidthPt / 2, bounds[0], 0.01);
        Assert.Equal(230 + TableStyle.Default.LineWidthPt, bounds[2], 0.01);

        // The app reads it as its own table: header cells bold in the app's default text style.
        using (var doc = CliDocument.Open(output))
        {
            var table = Assert.Single(doc.Model.Pages[0].Annotations.OfType<TableAnnotation>());
            Assert.True(table.Cell(0, 0).Runs[0].Bold);
            Assert.False(table.Cell(1, 1).Runs[0].Bold);
            Assert.Equal(TextEditing.Default.FontFamily, table.Cell(1, 1).Runs[0].FontFamily);
            Assert.Equal(TextEditing.Default.SizePt, table.Cell(1, 1).Runs[0].SizePt);
            Assert.Equal("사과", table.Cell(1, 1).Text);
        }

        // text reads the page's tables with their cells.
        var text = CliRunner.Ok("text", output, "-p", "1", "--fields", "lines,tables");
        var t = Assert.Single(text["tables"]!.AsArray())!;
        Assert.Equal(annotation["id"]!.GetValue<string>(), t["id"]!.GetValue<string>());
        Assert.Equal("pear", Cells(t["cells"])[2][0]);
        Assert.Empty(CliRunner.Ok("text", output, "-p", "2", "--fields", "tables")["tables"]!.AsArray());

        // find names the cell.
        var found = CliRunner.Ok("find", output, "--text", "사과", "--include-annotations");
        var hit = Assert.Single(found["annotationMatches"]!.AsArray())!;
        Assert.Equal("table", hit["kind"]!.GetValue<string>());
        Assert.Equal(1, hit["row"]!.GetValue<int>());
        Assert.Equal(1, hit["column"]!.GetValue<int>());
        Assert.Equal("사과", hit["text"]!.GetValue<string>());

        // Same source key: replaced in place; the table moves like other annotations.
        string again = Out("again.pdf");
        var replaced = Write("add", "table", output, "-p", "1", "--x", "60", "--y", "80", "--rows", "2", "--columns", "3", "--header", "false",
            "--source-key", "vocab", "-o", again);
        Assert.True(replaced["replaced"]!.GetValue<bool>());
        Assert.False(replaced["annotation"]!["header"]!.GetValue<bool>());
        Assert.Equal(new[] { new[] { "", "", "" }, ["", "", ""] }, Cells(replaced["annotation"]!["cells"]));
        string moved = Out("moved.pdf");
        var update = Write("annot", "update", again, "--source-key", "vocab", "--move", "10,0", "-o", moved);
        Assert.Equal(70 - TableStyle.Default.LineWidthPt / 2, Numbers(update["annotation"]!["bounds"])[0], 0.01);
    }

    [Fact]
    public void AddTableRefusesShapesItCannotMake()
    {
        string[] At(params string[] extra) => ["add", "table", sample, "-p", "1", "--x", "60", "--y", "80", .. extra, "--dry-run"];
        WriteFails("INVALID_ARGUMENT", ExitCodes.InvalidInput, At("--rows", "0", "--columns", "2"));
        WriteFails("INVALID_ARGUMENT", ExitCodes.InvalidInput, At("--rows", "2"));
        WriteFails("INVALID_ARGUMENT", ExitCodes.InvalidInput, At("--rows", "101", "--columns", "2"));
        WriteFails("INVALID_ARGUMENT", ExitCodes.InvalidInput, At("--rows", "2", "--columns", "2", "--cells", """[["a","b"],["c"]]"""));
        WriteFails("INVALID_ARGUMENT", ExitCodes.InvalidInput, At("--rows", "3", "--columns", "2", "--cells", Vocabulary[..^1] + ""","x"]"""));
        WriteFails("INVALID_ARGUMENT", ExitCodes.InvalidInput, At("--rows", "2", "--cells", Vocabulary));
        WriteFails("INVALID_ARGUMENT", ExitCodes.InvalidInput, At("--cells", """[["a", 1]]"""));
        WriteFails("INVALID_ARGUMENT", ExitCodes.InvalidInput, At("--rows", "1", "--columns", "2", "--column-widths", "90,10"));
        WriteFails("INVALID_ARGUMENT", ExitCodes.InvalidInput, At("--rows", "1", "--columns", "2", "--column-widths", "90"));
        WriteFails("INVALID_ARGUMENT", ExitCodes.InvalidInput, At("--rows", "1", "--columns", "2", "--header", "maybe"));
        WriteFails("INVALID_ARGUMENT", ExitCodes.InvalidInput, ["add", "table", sample, "-p", "1", "--x", "60", "--rows", "1", "--columns", "1", "--dry-run"]);
        // Off the page: refused like a text box, or made with a warning.
        WriteFails("PAGE_OVERFLOW", ExitCodes.Unsatisfiable, At("--rows", "2", "--columns", "10"));
        var allowed = Write(At("--rows", "2", "--columns", "10", "--overflow", "allow"));
        Assert.Equal("PAGE_OVERFLOW", allowed["warnings"]![0]!["code"]!.GetValue<string>());
        // Past the app's 10 × 10 grid: the app grows tables by rows too, so the CLI takes more.
        var tall = Write(At("--rows", "12", "--columns", "2", "--overflow", "allow"));
        Assert.Equal(12, tall["annotation"]!["rows"]!.GetValue<int>());
    }

    [Fact]
    public async Task ApplyWithTableIsOneUndoInLive()
    {
        string plan = """
            { "ops": [
              { "op": "add.table", "page": 1, "x": 60, "y": 80, "cells": [["Q", "A"], ["1", "x"]], "columnWidths": [60, 120], "sourceKey": "t" },
              { "op": "add.text", "page": 1, "rect": [60, 200, 120, 0], "text": "below" }
            ] }
            """;
        var ops = (JsonArray)JsonNode.Parse(plan)!["ops"]!;
        Assert.Equal(LiveLevel.AiWrite, CliLive.Needs("apply", ["apply", CliLive.Input, "-"], ops));
        Assert.Empty(CliLive.UnsupportedOps(ops));

        using var document = await OpenDocument.OpenPdfAsync(sample);
        var session = document.Session;
        var run = CliLive.Run(document, "doc", ["apply", CliLive.Input, "-"], plan);
        Assert.True(run.ExitCode == 0, run.Json);
        // The window executes the operations as one Undo unit (LiveLinkViewModel).
        int undo = session.UndoCount;
        session.Execute(new EditCommand(Authorship.AiLabel, run.Operations));
        Assert.Equal(undo + 1, session.UndoCount);
        var model = session.Current;
        var table = Assert.Single(model.Pages[0].Annotations.OfType<TableAnnotation>());
        Assert.Equal(AnnotationTag.AiAuthor, model.TagOf(table.Id)!.Author);
        Assert.Equal("x", table.Cell(1, 1).Text);
        Assert.Single(model.Pages[0].Annotations.OfType<TextBoxAnnotation>());
        Assert.True(session.Undo());
        Assert.Empty(session.Current.Pages[0].Annotations);
    }

    [Fact]
    public void SchemaCommandListsAddTable()
    {
        var names = CliRunner.Ok("schema")["commands"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("add table", names);
        var input = CliRunner.Ok("schema", "add", "table")["schema"]!["input"]!;
        foreach (string field in new[] { "page", "x", "y", "rows", "columns", "cells", "columnWidths", "header" })
            Assert.NotNull(input["properties"]![field]);
        Assert.True(CliApp.Ops.ContainsKey("add.table"));
        Assert.Equal(LiveLevel.AiWrite, CliLive.Needs("add table", ["add", "table", CliLive.Input], null));
        Assert.DoesNotContain("add table", CliLive.Unsupported);
        Assert.Contains("table", PdfNotes.Cli.Commands.AnnotListCommand.Kinds);
    }
}
