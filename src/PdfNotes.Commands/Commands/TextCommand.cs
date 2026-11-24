using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>text &lt;pdf&gt; -p &lt;page&gt; [--fields chars,words,lines] [--region x,y,w,h]</c> (cli_plan 4.3, 3.4): the PDF text
/// layer of one page, or of the part of it that touches a rectangle. Character numbers start at 0 (of the whole page also
/// with <c>--region</c>) and ranges are <c>{start, end}</c> with the end excluded. No OCR. <c>tables</c> are the page's table
/// annotations (1.4) with their cell texts by row and column: their text is not in the text layer.
/// </summary>
public static class TextCommand
{
    private static readonly string[] AllFields = ["chars", "words", "lines", "tables"];

    public static JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, ["--page", "--fields", "--region"], []);
        Rect2? region = args.Value("--region") is { } r ? CliArgs.ParseRect(r, "--region") : null;
        bool Inside(Quad q)
        {
            if (region is not { } box) return true;
            Point2[] c = [q.UpperLeft, q.UpperRight, q.LowerLeft, q.LowerRight];
            return c.Max(p => p.X) > box.X && c.Min(p => p.X) < box.X + box.Width && c.Max(p => p.Y) > box.Y && c.Min(p => p.Y) < box.Y + box.Height;
        }
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
            result["chars"] = CliJson.Array(glyphs.Select((g, i) => (g, i)).Where(c => region is null || (!c.g.IsEmpty && Inside(c.g.Box))).Select(c => (JsonNode?)new JsonObject
            {
                ["index"] = c.i,
                ["text"] = c.g.Text,
                ["quad"] = c.g.IsEmpty ? null : CliJson.Quad(c.g.Box),
            }));
        if (fields.Contains("words"))
            result["words"] = CliJson.Array(Words(glyphs).Where(w => region is null || TextSelection.LineQuads(glyphs, w.Start, w.End).Any(Inside)).Select(w => (JsonNode?)new JsonObject
            {
                ["start"] = w.Start,
                ["end"] = w.End,
                ["text"] = TextSelection.TextOf(glyphs, w.Start, w.End),
                ["quads"] = CliJson.Quads(TextSelection.LineQuads(glyphs, w.Start, w.End)),
            }));
        if (fields.Contains("lines"))
            result["lines"] = CliJson.Array(TextSelection.LineRuns(glyphs, 0, glyphs.Length).Where(l => Inside(l.Quad)).Select(l => (JsonNode?)new JsonObject
            {
                ["start"] = l.Start,
                ["end"] = l.End,
                ["text"] = TextSelection.TextOf(glyphs, l.Start, l.End),
                ["quad"] = CliJson.Quad(l.Quad),
            }));
        if (fields.Contains("tables"))
            result["tables"] = CliJson.Array(page.Annotations.OfType<TableAnnotation>().Select(t => (t, Bounds: AnnotListCommand.Bounds(t)))
                .Where(x => region is not { } box || (x.Bounds.X < box.X + box.Width && x.Bounds.X + x.Bounds.Width > box.X && x.Bounds.Y < box.Y + box.Height && x.Bounds.Y + x.Bounds.Height > box.Y))
                .Select(x => (JsonNode?)new JsonObject
                {
                    ["id"] = x.t.Id.ToString(),
                    ["bounds"] = CliJson.Rect(x.Bounds),
                    ["rows"] = x.t.Rows,
                    ["columns"] = x.t.Columns,
                    ["cells"] = AnnotListCommand.Cells(x.t),
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
