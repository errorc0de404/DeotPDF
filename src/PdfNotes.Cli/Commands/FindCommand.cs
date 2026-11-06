using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Services;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>find &lt;pdf&gt; --text &lt;phrase&gt; [-p &lt;page&gt; | --pages &lt;list&gt;] [--within x,y,w,h] [--fuzzy [--min-score s]]
/// [--context n] [--include-annotations]</c> (cli_plan 4.3.1). Candidates in page order, then by first character; their
/// <c>chars</c> and <c>quads</c> go unchanged into <c>add.mark</c>. Nothing is chosen: an empty list is a valid answer.
/// </summary>
public static class FindCommand
{
    public static JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, ["--text", "--page", "--pages", "--within", "--min-score", "--context"], ["--fuzzy", "--include-annotations"]);
        string query = args.Required("--text");
        string normalized = TextMatch.Normalize(query);
        if (normalized.Length == 0) throw CliException.Invalid("--text has no searchable characters.");
        bool fuzzy = args.Flag("--fuzzy");
        if (!fuzzy && args.Has("--min-score")) throw CliException.Invalid("--min-score needs --fuzzy.");
        double minScore = args.Number("--min-score", 0.8, 0.5, 1);
        int context = args.Integer("--context", 20, 0, 200);
        var within = args.Value("--within") is { } w ? CliArgs.ParseRect(w, "--within") : (Rect2?)null;
        using var doc = CliDocument.Open(args.InputPath());
        var pages = doc.SelectPages(args);

        var matches = new JsonArray();
        var boxMatches = new JsonArray();
        foreach (var page in pages)
        {
            var glyphs = doc.Glyphs(page);
            foreach (var m in Matches(doc, page, normalized, fuzzy, minScore, context, within)) matches.Add(m.Json);
            if (!args.Flag("--include-annotations")) continue;
            foreach (var hit in DocumentSearch.SearchPage(page, glyphs, query).Where(h => h.BoxId is not null))
            {
                if (within is { } r && !Inside(TextSelection.Bounds(hit.Quads), r)) continue;
                var annotation = page.Annotations.First(a => a.Id == hit.BoxId);
                string text = annotation switch { TextBoxAnnotation t => t.Text, StickyNote n => n.Text, _ => "" };
                boxMatches.Add(new JsonObject
                {
                    ["page"] = doc.NumberOf(page),
                    ["pageId"] = page.Id.ToString(),
                    ["annotationId"] = annotation.Id.ToString(),
                    ["kind"] = AnnotListCommand.KindName(annotation),
                    ["range"] = new JsonObject { ["start"] = hit.Start, ["end"] = hit.End },
                    ["quads"] = CliJson.Quads(hit.Quads),
                    ["text"] = annotation is TextBoxAnnotation && hit.End <= text.Length ? text[hit.Start..hit.End] : text,
                });
            }
        }
        var result = CliJson.Result("find", doc.SourceHash);
        result["query"] = query;
        result["normalizedQuery"] = normalized;
        result["fuzzy"] = fuzzy;
        result["count"] = matches.Count;
        result["matches"] = matches;
        if (args.Flag("--include-annotations")) result["annotationMatches"] = boxMatches;
        return result;
    }

    /// <summary>
    /// The text layer candidates of one page in character order, each as its JSON (the <c>matches</c> item, also the
    /// candidate list of <c>add mark --find</c>) with its character range.
    /// </summary>
    public static List<(JsonObject Json, int Start, int End)> Matches(CliDocument doc, Page page, string normalized, bool fuzzy, double minScore,
        int context, Rect2? within)
    {
        var glyphs = doc.Glyphs(page);
        var list = new List<(JsonObject, int, int)>();
        foreach (var m in TextMatch.Find(TextMatch.Build(glyphs), normalized, fuzzy, minScore))
        {
            var quads = TextSelection.LineQuads(glyphs, m.Start, m.End);
            if (quads.IsEmpty || within is { } r && !Inside(TextSelection.Bounds(quads), r)) continue;
            list.Add((new JsonObject
            {
                ["page"] = doc.NumberOf(page),
                ["pageId"] = page.Id.ToString(),
                ["chars"] = new JsonObject { ["start"] = m.Start, ["end"] = m.End },
                ["quads"] = CliJson.Quads(quads),
                ["score"] = CliJson.Num(m.Score),
                ["text"] = TextSelection.TextOf(glyphs, m.Start, m.End),
                ["before"] = Context(glyphs, Math.Max(0, m.Start - context), m.Start),
                ["after"] = Context(glyphs, m.End, Math.Min(glyphs.Length, m.End + context)),
            }, m.Start, m.End));
        }
        return list;
    }

    private static bool Inside(Rect2 inner, Rect2 outer) =>
        inner.X >= outer.X - 1e-6 && inner.Y >= outer.Y - 1e-6 &&
        inner.X + inner.Width <= outer.X + outer.Width + 1e-6 && inner.Y + inner.Height <= outer.Y + outer.Height + 1e-6;

    private static string Context(IReadOnlyList<TextGlyph> glyphs, int start, int end) =>
        string.Concat(Enumerable.Range(start, Math.Max(0, end - start)).Select(i => glyphs[i].Text)).ReplaceLineEndings(" ");
}
