using System.Text.Json;
using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>check &lt;pdf&gt; --rules &lt;json|@file&gt; [-p &lt;page&gt; | --pages &lt;list&gt;]</c> (cli_plan 4.8): geometric checks of
/// annotations by the caller's rules, each with its own selection. It reports problems and judges nothing else: a
/// problem is not an error of the command (exit 0). <c>apply</c> runs the same rules as <c>guards</c> (5.2).
/// Overlaps and page edges use the page-aligned bounds <c>annot list</c> reports.
/// </summary>
public static class CheckCommand
{
    public static readonly string[] RuleNames = ["overlapRegions", "overlapAmong", "outsidePage", "textOverflow", "minFontSize", "missingGlyph"];

    /// <summary>Which annotations a rule looks at; every field narrows (null: no condition) and <see cref="Exclude"/> takes some out again.</summary>
    public sealed record Selection(HashSet<string>? Kinds, HashSet<Guid>? Ids, HashSet<string>? SourceKeys, HashSet<string>? GroupKeys,
        HashSet<string>? BatchIds, bool? Tagged, IReadOnlyList<string>? Pages, Selection? Exclude = null);

    /// <summary>One rule: its name, position in the list, level (<c>error</c> or <c>warn</c>), selection and parameters.</summary>
    public sealed record Rule(int Index, string Name, string Level, Selection Select, IReadOnlyList<Rect2> Regions, double Tolerance, double? MaxHeight, double? Size);

    public static JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, ["--page", "--pages", "--rules"], []);
        using var doc = CliDocument.Open(args.InputPath());
        var rules = ParseRules(AddCommand.Json(args.Required("--rules"), "--rules"));
        var problems = Evaluate(doc, doc.SelectPages(args), rules);
        var result = CliJson.Result("check", doc.SourceHash);
        result["rules"] = rules.Count;
        result["count"] = problems.Count;
        result["passed"] = problems.All(p => p["level"]!.GetValue<string>() != "error");
        result["problems"] = CliJson.Array(problems);
        return result;
    }

    /// <summary>A JSON list of rules (cli_plan 4.8): <c>{ "rule": …, "select": {…}, "level": "error"|"warn", …parameters }</c>.</summary>
    public static List<Rule> ParseRules(JsonNode? node)
    {
        if (node is not JsonArray list) throw CliException.Invalid("Rules must be a JSON list of objects with \"rule\".");
        var rules = new List<Rule>();
        foreach (var item in list)
        {
            int index = rules.Count;
            if (item is not JsonObject o) throw CliException.Invalid($"Rule {index} is not an object.");
            string name = Text(o, "rule", index) ?? throw CliException.Invalid($"Rule {index} has no \"rule\"; rules are {string.Join(", ", RuleNames)}.");
            if (!RuleNames.Contains(name)) throw CliException.Invalid($"Rule {index}: unknown rule \"{name}\"; rules are {string.Join(", ", RuleNames)}.");
            string[] parameters = name switch
            {
                "overlapRegions" => ["regions", "tolerance"],
                "overlapAmong" or "outsidePage" => ["tolerance"],
                "textOverflow" => ["maxHeight"],
                "minFontSize" => ["size"],
                _ => [],
            };
            foreach (var (key, _) in o)
                if (key is not ("rule" or "select" or "level") && !parameters.Contains(key))
                    throw CliException.Invalid($"Rule {index} ({name}) has no field \"{key}\".", new JsonObject { ["rule"] = index, ["field"] = key });
            string level = Text(o, "level", index) ?? "error";
            if (level is not ("error" or "warn")) throw CliException.Invalid($"Rule {index}: level is error or warn.");
            var regions = new List<Rect2>();
            if (name == "overlapRegions")
            {
                if (o["regions"] is not JsonArray rs || rs.Count == 0) throw CliException.Invalid($"Rule {index} (overlapRegions) needs \"regions\": [[x, y, w, h], ...].");
                foreach (var r in rs)
                {
                    var n = Numbers(r, index, "regions");
                    if (n.Length != 4 || n[2] <= 0 || n[3] <= 0) throw CliException.Invalid($"Rule {index}: each region is [x, y, w, h] with a positive size.");
                    regions.Add(new(n[0], n[1], n[2], n[3]));
                }
            }
            double? size = Number(o, "size", index);
            if (name == "minFontSize" && size is null) throw CliException.Invalid($"Rule {index} (minFontSize) needs \"size\" in pt.");
            rules.Add(new(index, name, level, ParseSelection(o["select"], index), regions, Number(o, "tolerance", index) ?? 0, Number(o, "maxHeight", index), size));
        }
        return rules;
    }

    private static Selection ParseSelection(JsonNode? node, int index)
    {
        if (node is null) return new(null, null, null, null, null, null, null);
        if (node is not JsonObject o) throw CliException.Invalid($"Rule {index}: \"select\" is an object.");
        foreach (var (key, _) in o)
            if (key is not ("kinds" or "ids" or "sourceKeys" or "groupKeys" or "batchIds" or "tagged" or "pages" or "exclude"))
                throw CliException.Invalid($"Rule {index}: \"select\" has no field \"{key}\" (kinds, ids, sourceKeys, groupKeys, batchIds, tagged, pages, exclude).");
        var kinds = Strings(o, "kinds", index);
        if (kinds?.FirstOrDefault(k => !AnnotListCommand.Kinds.Contains(k)) is { } bad)
            throw CliException.Invalid($"Rule {index}: kind \"{bad}\" is not one of {string.Join(", ", AnnotListCommand.Kinds)}.");
        var ids = Strings(o, "ids", index)?.Select(id => Guid.TryParse(id, out var g) ? g : throw CliException.Invalid($"Rule {index}: \"{id}\" is not an annotation ID.")).ToHashSet();
        bool? tagged = o["tagged"] switch
        {
            null => null,
            JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValue<bool>(),
            _ => throw CliException.Invalid($"Rule {index}: \"tagged\" is true or false."),
        };
        var pages = o["pages"] is JsonArray ps ? ps.Select(p => p is JsonValue v ? v.ToString() : throw CliException.Invalid($"Rule {index}: pages are numbers or page IDs.")).ToList() : null;
        if (o["pages"] is not null and not JsonArray) throw CliException.Invalid($"Rule {index}: \"pages\" is a list.");
        var exclude = o["exclude"] is { } e ? ParseSelection(e, index) : null;
        if (exclude?.Pages is not null) throw CliException.Invalid($"Rule {index}: \"exclude\" takes annotation fields, not pages.");
        return new(kinds, ids, Strings(o, "sourceKeys", index), Strings(o, "groupKeys", index), Strings(o, "batchIds", index), tagged, pages, exclude);
    }

    /// <summary>The problems <paramref name="rules"/> find on <paramref name="pages"/>, rule by rule, page by page, bottom to top.</summary>
    public static List<JsonObject> Evaluate(CliDocument doc, IReadOnlyList<Page> pages, IReadOnlyList<Rule> rules)
    {
        var problems = new List<JsonObject>();
        foreach (var rule in rules)
        {
            var only = rule.Select.Pages?.Select(p => doc.ResolvePage(p).Id).ToHashSet();
            foreach (var page in pages.Where(p => only is null || only.Contains(p.Id)))
            {
                var selected = page.Annotations.Where(a => Selected(doc.Model, a, rule.Select)).Select(a => (Annotation: a, Bounds: AnnotListCommand.Bounds(a))).ToList();
                JsonObject Problem(Annotation a, Rect2 bounds, JsonObject details)
                {
                    var tag = doc.Model.TagOf(a.Id);
                    return new()
                    {
                        ["rule"] = rule.Name, ["ruleIndex"] = rule.Index, ["level"] = rule.Level, ["page"] = doc.NumberOf(page), ["pageId"] = page.Id.ToString(),
                        ["id"] = a.Id.ToString(), ["kind"] = AnnotListCommand.KindName(a), ["sourceKey"] = tag?.SourceKey, ["groupKey"] = tag?.GroupKey,
                        ["bounds"] = CliJson.Rect(bounds), ["details"] = details,
                    };
                }
                switch (rule.Name)
                {
                    case "overlapRegions":
                        foreach (var (a, b) in selected)
                            foreach (var region in rule.Regions)
                                if (Overlap(b, region, rule.Tolerance) is { } o)
                                    problems.Add(Problem(a, b, new() { ["region"] = CliJson.Rect(region), ["overlap"] = CliJson.Rect(o) }));
                        break;
                    case "overlapAmong":
                        for (int i = 0; i < selected.Count; i++)
                            for (int j = i + 1; j < selected.Count; j++)
                                if (Overlap(selected[i].Bounds, selected[j].Bounds, rule.Tolerance) is { } o)
                                {
                                    var other = selected[j].Annotation;
                                    problems.Add(Problem(selected[i].Annotation, selected[i].Bounds, new()
                                    {
                                        ["otherId"] = other.Id.ToString(), ["otherKind"] = AnnotListCommand.KindName(other),
                                        ["otherSourceKey"] = doc.Model.TagOf(other.Id)?.SourceKey, ["otherBounds"] = CliJson.Rect(selected[j].Bounds), ["overlap"] = CliJson.Rect(o),
                                    }));
                                }
                        break;
                    case "outsidePage":
                    {
                        double w = page.Setup.WidthPt, h = page.Setup.HeightPt;
                        foreach (var (a, b) in selected)
                        {
                            double left = Math.Max(0, -b.X), top = Math.Max(0, -b.Y), right = Math.Max(0, b.X + b.Width - w), bottom = Math.Max(0, b.Y + b.Height - h);
                            if (Math.Max(Math.Max(left, top), Math.Max(right, bottom)) > rule.Tolerance + 1e-6)
                                problems.Add(Problem(a, b, new()
                                {
                                    ["page"] = CliJson.Rect(0, 0, w, h),
                                    ["overflow"] = new JsonObject { ["left"] = CliJson.Num(left), ["top"] = CliJson.Num(top), ["right"] = CliJson.Num(right), ["bottom"] = CliJson.Num(bottom) },
                                }));
                        }
                        break;
                    }
                    case "textOverflow":
                        foreach (var (a, b) in selected)
                            if (a is TextBoxAnnotation t && TextOverflow(t, rule.MaxHeight) is { } details) problems.Add(Problem(a, b, details));
                        break;
                    case "minFontSize":
                        foreach (var (a, b) in selected)
                        {
                            if (a is not TextBoxAnnotation t) continue;
                            var small = t.Runs.Where(r => r.SizePt < rule.Size!.Value - 1e-6).ToList();
                            if (small.Count > 0)
                                problems.Add(Problem(a, b, new()
                                {
                                    ["minSize"] = CliJson.Num(rule.Size!.Value), ["smallest"] = CliJson.Num(small.Min(r => r.SizePt)),
                                    ["ranges"] = CliJson.Array(small.Select(r => (JsonNode?)new JsonObject
                                    {
                                        ["start"] = r.Start, ["end"] = r.Start + r.Length, ["text"] = t.Text.Substring(r.Start, r.Length), ["size"] = CliJson.Num(r.SizePt),
                                    })),
                                }));
                        }
                        break;
                    case "missingGlyph":
                        foreach (var (a, b) in selected)
                        {
                            if (a is not TextBoxAnnotation t) continue;
                            var missing = (JsonArray)MeasureCommand.Report(t, null, RichText.StyleOf(t))["missingGlyphs"]!;
                            if (missing.Count > 0) problems.Add(Problem(a, b, new() { ["missingGlyphs"] = missing.DeepClone() }));
                        }
                        break;
                }
            }
        }
        return problems;
    }

    /// <summary>
    /// A text box whose laid-out glyphs reach past its inner width (text that cannot wrap), or that is taller than
    /// <paramref name="maxHeight"/>. Text box heights follow their text, so a box never cuts its text off.
    /// </summary>
    private static JsonObject? TextOverflow(TextBoxAnnotation t, double? maxHeight)
    {
        var layout = TextLayout.Measure(t);
        double inner = t.WidthPt - TextLayout.PaddingPt * t.LayoutScale;
        var visible = layout.Characters.Where(c => !string.IsNullOrWhiteSpace(c.Text)).ToList();
        double right = visible.Count == 0 ? 0 : visible.Max(c => c.Bounds.X + c.Bounds.Width);
        bool wide = right > inner + 0.01;
        bool tall = maxHeight is { } m && layout.Height > m + 1e-6;
        if (!wide && !tall) return null;
        var details = new JsonObject
        {
            ["width"] = CliJson.Num(t.WidthPt), ["height"] = CliJson.Num(layout.Height), ["wide"] = wide, ["tall"] = tall,
            ["textRight"] = CliJson.Num(right + TextLayout.PaddingPt * t.LayoutScale),
        };
        if (maxHeight is { } max) details["maxHeight"] = CliJson.Num(max);
        return details;
    }

    private static bool Selected(DocumentModel model, Annotation a, Selection s)
    {
        var tag = model.TagOf(a.Id);
        return (s.Kinds is null || s.Kinds.Contains(AnnotListCommand.KindName(a))) && (s.Ids is null || s.Ids.Contains(a.Id)) &&
            (s.Tagged is null || s.Tagged == (tag is not null)) &&
            (s.SourceKeys is null || tag?.SourceKey is { } k && s.SourceKeys.Contains(k)) &&
            (s.GroupKeys is null || tag?.GroupKey is { } g && s.GroupKeys.Contains(g)) &&
            (s.BatchIds is null || tag?.BatchId is { } b && s.BatchIds.Contains(b)) &&
            (s.Exclude is null || !Selected(model, a, s.Exclude));
    }

    /// <summary>The common part of two rectangles when it is more than <paramref name="tolerance"/> wide and high.</summary>
    private static Rect2? Overlap(Rect2 a, Rect2 b, double tolerance)
    {
        double x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
        double x1 = Math.Min(a.X + a.Width, b.X + b.Width), y1 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return x1 - x0 > tolerance + 1e-6 && y1 - y0 > tolerance + 1e-6 ? new(x0, y0, x1 - x0, y1 - y0) : null;
    }

    private static string? Text(JsonObject o, string key, int index) => o[key] switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => throw CliException.Invalid($"Rule {index}: \"{key}\" is a string."),
    };

    private static double? Number(JsonObject o, string key, int index)
    {
        if (o[key] is null) return null;
        double d = o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : double.NaN;
        return double.IsFinite(d) && d >= 0 ? d : throw CliException.Invalid($"Rule {index}: \"{key}\" is a number of 0 or more.");
    }

    private static double[] Numbers(JsonNode? node, int index, string key) =>
        node is JsonArray a && a.All(n => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
            ? a.Select(n => n!.GetValue<double>()).ToArray() : throw CliException.Invalid($"Rule {index}: \"{key}\" holds lists of numbers.");

    private static HashSet<string>? Strings(JsonObject o, string key, int index) => o[key] switch
    {
        null => null,
        JsonArray a when a.All(n => n is JsonValue v && v.GetValueKind() == JsonValueKind.String) => a.Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal),
        _ => throw CliException.Invalid($"Rule {index}: \"select.{key}\" is a list of strings."),
    };
}
