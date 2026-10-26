using System.Text.Json.Nodes;
using PdfNotes.Core.Models;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>ink &lt;pdf&gt; -p &lt;page&gt; [--cell pt] [--blocks [--merge pt] [--min-size pt]] [--dpi n] [--threshold n]
/// [--include-annotations]</c> (cli_plan 4.3): how much of each grid cell of the rendered page is ink (0–1), and with
/// <c>--blocks</c> the connected pieces of ink (paragraphs, figures). Measured on pixels, so scans work the same way.
/// </summary>
public static class InkCommand
{
    public const double DefaultDpi = 72;
    public const int DefaultThreshold = 60;

    public static JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, ["--page", "--cell", "--merge", "--min-size", "--dpi", "--threshold"], ["--blocks", "--include-annotations"]);
        double cell = args.Number("--cell", 12, 1, 500);
        double merge = args.Number("--merge", 6, 0, 200), minSize = args.Number("--min-size", 4, 0, 1000);
        if (!args.Flag("--blocks") && (args.Has("--merge") || args.Has("--min-size"))) throw CliException.Invalid("--merge and --min-size need --blocks.");
        using var doc = CliDocument.Open(args.InputPath());
        var page = doc.ResolvePage(args.Required("--page"));
        bool annotations = args.Flag("--include-annotations");
        var map = InkMap.Build(doc, page, args.Number("--dpi", DefaultDpi, 18, 300), _ => annotations, args.Integer("--threshold", DefaultThreshold, 1, 765));
        var grid = map.Grid(cell);

        var result = CliJson.Result("ink", doc.SourceHash);
        result["page"] = doc.NumberOf(page);
        result["pageId"] = page.Id.ToString();
        result["paper"] = map.Paper.ToHex();
        result["annotationsIncluded"] = annotations;
        result["cell"] = CliJson.Num(cell);
        result["columns"] = grid.GetLength(1);
        result["rows"] = grid.GetLength(0);
        result["cells"] = CliJson.Array(Enumerable.Range(0, grid.GetLength(0)).Select(r =>
            (JsonNode?)CliJson.Array(Enumerable.Range(0, grid.GetLength(1)).Select(c => (JsonNode?)CliJson.Num(grid[r, c])))));
        if (args.Flag("--blocks"))
            result["blocks"] = CliJson.Array(map.Blocks(merge, minSize)
                .OrderBy(b => b.Rect.Y).ThenBy(b => b.Rect.X)
                .Select(b => (JsonNode?)new JsonObject { ["rect"] = CliJson.Rect(b.Rect), ["inkRatio"] = CliJson.Num(b.Ratio) }));
        return result;
    }
}
