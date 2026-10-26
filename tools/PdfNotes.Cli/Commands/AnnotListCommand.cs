using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>annot list &lt;pdf&gt; [-p &lt;page&gt;] [--kind &lt;kind&gt;] [--tag tagged|untagged] [--author a] [--batch-id id]
/// [--source-key key] [--group-key key]</c> (cli_plan 4.3): annotations in stacking order (z 0 = bottom) with their tags
/// (schema v7: author, batchId, sourceKey, groupKey; null for the user's own writing and other apps' annotations).
/// </summary>
public static class AnnotListCommand
{
    public static readonly string[] Kinds = ["text", "mark", "highlight-stroke", "line", "shape", "ink", "image", "note"];

    public static JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, ["--page", "--kind", "--tag", "--author", "--batch-id", "--source-key", "--group-key"], []);
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
            for (int z = 0; z < p.Annotations.Count; z++)
                if ((kind is null || KindName(p.Annotations[z]) == kind) && Wanted(doc.Model.TagOf(p.Annotations[z].Id)))
                    items.Add(Describe(doc, p, p.Annotations[z], z));
        var result = CliJson.Result("annot list", doc.SourceHash);
        result["count"] = items.Count;
        result["annotations"] = items;
        return result;
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
        _ => a.Kind.ToString(),
    };

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
        }
        return item;
    }
}
