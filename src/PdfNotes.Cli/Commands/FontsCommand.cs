using System.Text.Json.Nodes;
using PdfNotes.Text;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>fonts</c> (cli_plan 4.4): the font family names <c>measure</c>, <c>add text</c> and rendering accept on this machine,
/// and the family used when a requested one is not installed.
/// </summary>
public static class FontsCommand
{
    public static JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, [], []);
        if (args.Positionals.Count > 0) throw CliException.Invalid($"Unexpected argument {args.Positionals[0]}.");
        var fonts = TextLayout.FontFamilies();
        var result = CliJson.Result("fonts");
        result["fallback"] = TextLayout.FallbackFont;
        result["count"] = fonts.Count;
        result["fonts"] = CliJson.Array(fonts.Select(f => (JsonNode?)f));
        return result;
    }
}
