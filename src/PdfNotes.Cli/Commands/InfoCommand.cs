using System.Text.Json.Nodes;
using PdfNotes.Core.Models;

namespace PdfNotes.Cli.Commands;

/// <summary><c>info &lt;pdf&gt;</c> (cli_plan 4.1).</summary>
public static class InfoCommand
{
    public static JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, [], []);
        using var doc = CliDocument.Open(args.InputPath());
        var model = doc.Model;
        var appData = doc.Document.Source!.AppData;
        var result = CliJson.Result("info", doc.SourceHash);
        result["path"] = doc.Path;
        result["pageCount"] = model.Pages.Count;
        result["pageIds"] = doc.DerivedPageIds ? "derived" : "file";
        result["appData"] = new JsonObject
        {
            ["restoredAnnotations"] = appData.RestoredCount,
            ["readOnlyAnnotations"] = appData.ReadOnlyCount,
            ["warnings"] = CliJson.Array(appData.Warnings.Select(w => (JsonNode?)w)),
        };
        result["pages"] = CliJson.Array(model.Pages.Select((page, i) => (JsonNode?)Page(doc, page, i + 1)));
        return result;
    }

    private static JsonObject Page(CliDocument doc, Page page, int number)
    {
        var setup = page.Setup;
        var frame = setup.Frame;
        return new JsonObject
        {
            ["page"] = number,
            ["pageId"] = page.Id.ToString(),
            ["width"] = CliJson.Num(setup.WidthPt),
            ["height"] = CliJson.Num(setup.HeightPt),
            ["rotation"] = setup.Rotation,
            ["displayWidth"] = CliJson.Num(frame.DisplayWidthPt),
            ["displayHeight"] = CliJson.Num(frame.DisplayHeightPt),
            ["source"] = setup.Source.Kind == PageSourceKind.Pdf ? "pdf" : "generated",
            ["hasText"] = doc.Glyphs(page).Any(g => !g.IsEmpty),
            ["annotationCount"] = page.Annotations.Count,
        };
    }
}
