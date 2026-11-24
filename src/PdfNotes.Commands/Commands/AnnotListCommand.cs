using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>annot list &lt;pdf&gt; [-p &lt;page&gt;] [--kind &lt;kind&gt;] [--tag tagged|untagged] [--author a] [--batch-id id]
/// [--source-key key] [--group-key key] [--near-text]</c> (cli_plan 4.3): annotations in stacking order (z 0 = bottom) with their tags
/// (schema v7: author, batchId, sourceKey, groupKey; null for the user's own writing and other apps' annotations).
/// <c>--near-text</c> adds the PDF text line each one lies on or nearest to (which answer a mark is on, which question a
/// note sits beside).
/// </summary>
public static class AnnotListCommand
{
    public static readonly string[] Kinds = ["text", "mark", "highlight-stroke", "line", "shape", "ink", "image", "note", "table"];

    public static JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, ["--page", "--kind", "--tag", "--author", "--batch-id", "--source-key", "--group-key"], ["--near-text"]);
        bool near = args.Flag("--near-text");
        string? kind = args.Value("--kind");
        if (kind is not null && !Kinds.Contains(kind)) throw CliException.Invalid($"--kind takes {string.Join(", ", Kinds)}.");
        var tagged = args.Value("--tag") switch
        {
            null => (bool?)null,
            "tagged" => true,
            "untagged" => false,
            _ => throw CliException.Invalid("--tag takes tagged or untagged."),
        };
        string? author = args.Value("--author"), batch = args.Value("--batch-id"), sourceKey = args.Value("--source-key"), groupKey = args.Value("--group-key");
        bool Wanted(AnnotationTag? t) => (tagged is null || tagged == (t is not null)) && (author is null || t?.Author == author) &&
            (batch is null || t?.BatchId == batch) && (sourceKey is null || t?.SourceKey == sourceKey) && (groupKey is null || t?.GroupKey == groupKey);
        using var doc = CliDocument.Open(args.InputPath());
        var pages = args.Value("--page") is { } page ? [doc.ResolvePage(page)] : doc.Model.Pages.ToList();
        var items = new JsonArray();
        foreach (var p in pages)
        {
            TextGlyph[]? glyphs = null;
            ImmutableArray<(int Start, int End, Quad Quad)> lines = default;
            for (int z = 0; z < p.Annotations.Count; z++)
            {
                if ((kind is not null && KindName(p.Annotations[z]) != kind) || !Wanted(doc.Model.TagOf(p.Annotations[z].Id))) continue;
                var item = Describe(doc, p, p.Annotations[z], z);
                if (near)
                {
                    if (glyphs is null)
                    {
                        glyphs = doc.Glyphs(p);
                        lines = TextSelection.LineRuns(glyphs, 0, glyphs.Length);
                    }
                    item["nearText"] = NearText(glyphs, lines, Bounds(p.Annotations[z]));
                }
                items.Add(item);
            }
        }
        var result = CliJson.Result("annot list", doc.SourceHash);
        result["count"] = items.Count;
        result["annotations"] = items;
        return result;
    }

    /// <summary>
    /// The text line of the page nearest to <paramref name="bounds"/> (distance 0 when they overlap; of several the one
    /// overlapping most, then the first), or null on a page without text.
    /// </summary>
    private static JsonObject? NearText(IReadOnlyList<TextGlyph> glyphs, ImmutableArray<(int Start, int End, Quad Quad)> lines, Rect2 bounds)
    {
        (int Start, int End, Quad Quad)? best = null;
        double bestDistance = double.MaxValue, bestOverlap = -1;
        foreach (var line in lines)
        {
            Point2[] c = [line.Quad.UpperLeft, line.Quad.UpperRight, line.Quad.LowerLeft, line.Quad.LowerRight];
            double left = c.Min(p => p.X), right = c.Max(p => p.X), top = c.Min(p => p.Y), bottom = c.Max(p => p.Y);
            double dx = Math.Max(0, Math.Max(left - (bounds.X + bounds.Width), bounds.X - right));
            double dy = Math.Max(0, Math.Max(top - (bounds.Y + bounds.Height), bounds.Y - bottom));
            double distance = Math.Sqrt(dx * dx + dy * dy);
            double overlap = distance > 0 ? 0
                : Math.Max(0, Math.Min(right, bounds.X + bounds.Width) - Math.Max(left, bounds.X)) * Math.Max(0, Math.Min(bottom, bounds.Y + bounds.Height) - Math.Max(top, bounds.Y));
            if (distance < bestDistance - 1e-9 || (Math.Abs(distance - bestDistance) <= 1e-9 && overlap > bestOverlap + 1e-9))
                (best, bestDistance, bestOverlap) = (line, distance, overlap);
        }
        return best is not { } found ? null : new JsonObject
        {
            ["start"] = found.Start,
            ["end"] = found.End,
            ["text"] = TextSelection.TextOf(glyphs, found.Start, found.End),
            ["quad"] = CliJson.Quad(found.Quad),
            ["distance"] = CliJson.Num(bestDistance),
        };
    }

    public static JsonObject? Tags(AnnotationTag? tag) => tag is null ? null : new JsonObject
    {
        ["author"] = tag.Author, ["batchId"] = tag.BatchId, ["sourceKey"] = tag.SourceKey, ["groupKey"] = tag.GroupKey,
    };

    public static string KindName(Annotation a) => a switch
    {
        TextBoxAnnotation => "text",
        PdfTextHighlight or PdfTextMarkup => "mark",
        FreeHighlightStroke => "highlight-stroke",
        LineAnnotation => "line",
        ShapeAnnotation => "shape",
        InkStroke => "ink",
        ImageAnnotation => "image",
        StickyNote => "note",
        TableAnnotation => "table",
        _ => a.Kind.ToString(),
    };

    /// <summary>A table's cell texts as rows of columns (<c>cells[row][column]</c>).</summary>
    public static JsonArray Cells(TableAnnotation table) => CliJson.Array(Enumerable.Range(0, table.Rows).Select(r =>
        (JsonNode?)CliJson.Array(Enumerable.Range(0, table.Columns).Select(c => (JsonNode?)table.Cell(r, c).Text))));

    /// <summary>The page-aligned bounds of what <paramref name="a"/> paints (the <c>bounds</c> of <c>annot list</c>).</summary>
    public static Rect2 Bounds(Annotation a) => AnnotationGeometry.Bounds([a], TextLayout.Bounds);

    public static JsonObject Describe(CliDocument doc, Page page, Annotation a, int z)
    {
        var corners = AnnotationGeometry.Corners(AnnotationGeometry.LocalBounds(a, TextLayout.Bounds), a.Transform);
        double x0 = corners.Min(c => c.X), y0 = corners.Min(c => c.Y);
        var item = new JsonObject
        {
            ["id"] = a.Id.ToString(),
            ["page"] = doc.NumberOf(page),
            ["pageId"] = page.Id.ToString(),
            ["kind"] = KindName(a),
            ["z"] = z,
            ["locked"] = a.Locked,
            ["bounds"] = CliJson.Rect(x0, y0, corners.Max(c => c.X) - x0, corners.Max(c => c.Y) - y0),
            ["corners"] = CliJson.Array(corners.Select(c => (JsonNode?)CliJson.Point(c))),
            ["transform"] = CliJson.Matrix(a.Transform),
            ["tags"] = Tags(doc.Model.TagOf(a.Id)),
        };
        switch (a)
        {
            case TextBoxAnnotation t:
                item["text"] = t.Text;
                item["width"] = CliJson.Num(t.WidthPt);
                item["alignment"] = t.Alignment.ToString().ToLowerInvariant();
                item["runs"] = CliJson.Array(t.Runs.Select(r => (JsonNode?)new JsonObject
                {
                    ["start"] = r.Start, ["end"] = r.Start + r.Length, ["font"] = r.FontFamily, ["size"] = CliJson.Num(r.SizePt),
                    ["color"] = r.Color.ToHex(), ["bold"] = r.Bold, ["italic"] = r.Italic, ["underline"] = r.Underline, ["strikeout"] = r.Strikethrough,
                }));
                item["highlights"] = CliJson.Array(t.Highlights.Select(h => (JsonNode?)new JsonObject
                {
                    ["start"] = h.Start, ["end"] = h.Start + h.Length, ["color"] = h.Color.ToHex(), ["opacity"] = CliJson.Num(h.Opacity),
                }));
                item["box"] = new JsonObject
                {
                    ["background"] = t.BoxStyle.BackgroundColor.ToHex(), ["backgroundOpacity"] = CliJson.Num(t.BoxStyle.BackgroundOpacity),
                    ["border"] = t.BoxStyle.BorderEnabled, ["borderColor"] = t.BoxStyle.BorderColor.ToHex(), ["borderOpacity"] = CliJson.Num(t.BoxStyle.BorderOpacity),
                };
                break;
            case PdfTextHighlight h:
                item["markKind"] = "highlight";
                item["quads"] = CliJson.Quads(h.Quads);
                item["sourceText"] = h.SourceTextHint;
                item["color"] = h.Color.ToHex();
                item["opacity"] = CliJson.Num(h.Opacity);
                item["height"] = h.HeightMode == HighlightHeightMode.Auto ? "auto" : CliJson.Num(h.ManualHeightPt ?? 0);
                break;
            case PdfTextMarkup m:
                item["markKind"] = m.MarkupKind == TextMarkupKind.Underline ? "underline" : "strikeout";
                item["quads"] = CliJson.Quads(m.Quads);
                item["sourceText"] = m.SourceTextHint;
                item["color"] = m.Color.ToHex();
                item["opacity"] = CliJson.Num(m.Opacity);
                item["lineWidth"] = CliJson.Num(m.WidthPt);
                break;
            case StrokeAnnotation s:
                item["color"] = s.Color.ToHex();
                item["opacity"] = CliJson.Num(s.Opacity);
                item["lineWidth"] = CliJson.Num(s.WidthPt);
                item["pointCount"] = s.Points.Length;
                if (s is InkStroke { HasPressure: true }) item["pressure"] = true; // width follows pen pressure (schema v8)
                break;
            case LineAnnotation l:
                item["lineKind"] = l.LineKind.ToString().ToLowerInvariant();
                item["points"] = CliJson.Array(new[] { l.Start }.Concat(l.Controls).Append(l.End).Select(p => (JsonNode?)CliJson.Point(l.Transform.Transform(p))));
                item["color"] = l.Color.ToHex();
                item["opacity"] = CliJson.Num(l.Opacity);
                item["lineWidth"] = CliJson.Num(l.WidthPt);
                item["dash"] = l.Dash.ToString().ToLowerInvariant();
                item["startArrow"] = l.StartArrow;
                item["endArrow"] = l.EndArrow;
                break;
            case ShapeAnnotation sh:
                item["shapeKind"] = sh.ShapeKind.ToString().ToLowerInvariant();
                item["fill"] = sh.Style.FillColor.ToHex();
                item["fillOpacity"] = CliJson.Num(sh.Style.FillOpacity);
                item["border"] = sh.Style.BorderEnabled;
                item["borderColor"] = sh.Style.BorderColor.ToHex();
                item["borderWidth"] = CliJson.Num(sh.Style.BorderWidthPt);
                item["borderOpacity"] = CliJson.Num(sh.Style.BorderOpacity);
                break;
            case ImageAnnotation img:
                item["mediaType"] = doc.Model.Resources.TryGetValue(img.ResourceId, out var resource) ? resource.MediaType : null;
                break;
            case StickyNote note:
                item["text"] = note.Text;
                item["color"] = note.Color.ToHex();
                break;
            case TableAnnotation table:
                var layout = TableLayout.Measure(table);
                item["rows"] = table.Rows;
                item["columns"] = table.Columns;
                item["cells"] = Cells(table);
                item["columnWidths"] = CliJson.Array(table.ColumnWidths.Select(w => (JsonNode?)CliJson.Num(w)));
                item["rowHeights"] = CliJson.Array(Enumerable.Range(0, table.Rows).Select(r => (JsonNode?)CliJson.Num(layout.RowY[r + 1] - layout.RowY[r])));
                item["minRowHeights"] = CliJson.Array(table.RowHeights.Select(h => (JsonNode?)CliJson.Num(h)));
                item["header"] = table.Style.HeaderRow;
                item["lineColor"] = table.Style.LineColor.ToHex();
                item["lineWidth"] = CliJson.Num(table.Style.LineWidthPt);
                break;
        }
        return item;
    }
}
