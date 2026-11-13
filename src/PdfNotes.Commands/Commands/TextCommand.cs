using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>text &lt;pdf&gt; -p &lt;page&gt; [--fields chars,words,lines]</c> (cli_plan 4.3, 3.4): the PDF text layer of one page.
/// Character numbers start at 0 and ranges are <c>{start, end}</c> with the end excluded. No OCR.
/// </summary>
public static class TextCommand
{
    private static readonly string[] AllFields = ["chars", "words", "lines"];

    public static JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, ["--page", "--fields"], []);
        var fields = (args.Value("--fields") ?? string.Join(',', AllFields)).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0 || fields.Except(AllFields).Any())
            throw CliException.Invalid($"--fields takes {string.Join(", ", AllFields)}.");
        using var doc = CliDocument.Open(args.InputPath());
        var page = doc.ResolvePage(args.Required("--page"));
        var glyphs = doc.Glyphs(page);

        var result = CliJson.Result("text", doc.SourceHash);
        result["page"] = doc.NumberOf(page);
        result["pageId"] = page.Id.ToString();
        result["hasText"] = glyphs.Any(g => !g.IsEmpty);
        result["charCount"] = glyphs.Length;
        if (fields.Contains("chars"))
            result["chars"] = CliJson.Array(glyphs.Select((g, i) => (JsonNode?)new JsonObject
            {
                ["index"] = i,
                ["text"] = g.Text,
                ["quad"] = g.IsEmpty ? null : CliJson.Quad(g.Box),
            }));
        if (fields.Contains("words"))
            result["words"] = CliJson.Array(Words(glyphs).Select(w => (JsonNode?)new JsonObject
            {
                ["start"] = w.Start,
                ["end"] = w.End,
                ["text"] = TextSelection.TextOf(glyphs, w.Start, w.End),
                ["quads"] = CliJson.Quads(TextSelection.LineQuads(glyphs, w.Start, w.End)),
            }));
        if (fields.Contains("lines"))
            result["lines"] = CliJson.Array(TextSelection.LineRuns(glyphs, 0, glyphs.Length).Select(l => (JsonNode?)new JsonObject
            {
                ["start"] = l.Start,
                ["end"] = l.End,
                ["text"] = TextSelection.TextOf(glyphs, l.Start, l.End),
                ["quad"] = CliJson.Quad(l.Quad),
            }));
        return result;
    }

    /// <summary>
    /// Words by the app's double-click rule (spec 9.3, <see cref="TextSelection.WordAt"/>): word characters joined on the
    /// same visual line.
    /// </summary>
    public static IEnumerable<(int Start, int End)> Words(IReadOnlyList<TextGlyph> glyphs)
    {
        bool Joined(int a, int b) => !glyphs[a].IsEmpty && !glyphs[b].IsEmpty && TextSelection.LineQuads(glyphs, a, b + 1).Length == 1;
        for (int i = 0; i < glyphs.Count;)
        {
            if (!glyphs[i].IsEmpty && TextSelection.Word(glyphs.Count, i, k => glyphs[k].Text, Joined) is { } word)
            {
                yield return word;
                i = word.End;
            }
            else i++;
        }
    }
}
