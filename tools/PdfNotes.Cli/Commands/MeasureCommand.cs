using System.Globalization;
using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>measure --text &lt;formatted text&gt; [--width pt] [--height pt] [--style json]</c> (cli_plan 4.4): the size the app's
/// text engine gives this text box, its lines (UTF-16 ranges and rectangles in box coordinates), the fonts that actually
/// drew it, missing glyphs, the fit against <c>--height</c>, and the effective style with every default filled in. No
/// width: the narrowest box that keeps each written line on one line. Reads no PDF and changes nothing.
/// </summary>
public static class MeasureCommand
{
    public static JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, ["--text", "--width", "--height", "--style"], []);
        if (args.Positionals.Count > 0) throw CliException.Invalid($"Unexpected argument {args.Positionals[0]}: measure reads no PDF.");
        var spans = RichText.ParseText(args.Required("--text"));
        var style = RichText.ParseStyle(args.Value("--style"), RichText.BoxStyle.Default);
        double width = args.Has("--width") ? args.Number("--width", 0, 16, ModelLimits.MaxLengthPt) : RichText.OneLineWidth(spans, style);
        double? height = args.Has("--height") ? args.Number("--height", 0, 0, ModelLimits.MaxLengthPt) : null;
        var box = RichText.Build(spans, style, width, Affine.Identity);
        var result = CliJson.Result("measure");
        foreach (var (key, value) in Report(box, height, style)) result[key] = value?.DeepClone();
        return result;
    }

    /// <summary>
    /// The measurement fields shared with <c>add text</c>: width, height, lines, fonts, substitutions, missing glyphs,
    /// warnings, the fit against <paramref name="boxHeight"/> and the effective style.
    /// </summary>
    public static JsonObject Report(TextBoxAnnotation box, double? boxHeight, RichText.BoxStyle style)
    {
        var layout = TextLayout.Measure(box);
        var report = new JsonObject
        {
            ["width"] = CliJson.Num(box.WidthPt),
            ["height"] = CliJson.Num(layout.Height),
        };
        var lines = layout.Characters.GroupBy(c => c.Line).Select(g =>
        {
            int start = g.Min(c => c.Offset), end = g.Max(c => c.Offset + c.Text.Length);
            double x0 = g.Min(c => c.Bounds.X), y0 = g.Min(c => c.Bounds.Y);
            return new JsonObject
            {
                ["start"] = start,
                ["end"] = end,
                ["text"] = box.Text[start..end],
                ["rect"] = CliJson.Rect(x0, y0, g.Max(c => c.Bounds.X + c.Bounds.Width) - x0, g.Max(c => c.Bounds.Y + c.Bounds.Height) - y0),
            };
        }).ToList();
        report["lineCount"] = lines.Count;
        report["lines"] = CliJson.Array(lines);

        var uses = TextLayout.Fonts(box);
        var warnings = new JsonArray();
        report["fonts"] = CliJson.Array(Merge(uses, u => (u.Requested, u.Font)).Select(g => (JsonNode?)new JsonObject
        {
            ["start"] = g.Start, ["end"] = g.End, ["requested"] = g.Key.Requested, ["font"] = g.Key.Font,
        }));
        foreach (string font in box.Runs.Select(r => r.FontFamily).Distinct().Where(f => !TextLayout.FontAvailable(f)))
            warnings.Add(Warning("FONT_NOT_INSTALLED", $"Font '{font}' is not installed; {TextLayout.FallbackFont} is used.",
                new JsonObject { ["requested"] = font, ["font"] = TextLayout.FallbackFont }));
        var substituted = Merge(uses.Where(u => !u.Missing && u.Font != Effective(u.Requested)).ToList(), u => (u.Requested, u.Font)).ToList();
        report["substitutions"] = CliJson.Array(substituted.Select(g => (JsonNode?)new JsonObject
        {
            ["start"] = g.Start, ["end"] = g.End, ["text"] = box.Text[g.Start..g.End], ["requested"] = Effective(g.Key.Requested), ["font"] = g.Key.Font,
        }));
        if (substituted.Count > 0)
            warnings.Add(Warning("FONT_FALLBACK", "Some characters are drawn with another font than requested.", new JsonObject { ["count"] = substituted.Count }));
        var missing = Merge(uses.Where(u => u.Missing).ToList(), _ => 0).Select(g => new JsonObject
        {
            ["start"] = g.Start, ["end"] = g.End, ["text"] = box.Text[g.Start..g.End],
            ["codepoints"] = CliJson.Array(box.Text[g.Start..g.End].EnumerateRunes().Select(r => (JsonNode?)("U+" + r.Value.ToString("X4", CultureInfo.InvariantCulture)))),
        }).ToList();
        report["missingGlyphs"] = CliJson.Array(missing);
        foreach (var m in missing)
            warnings.Add(Warning("MISSING_GLYPH", $"No installed font has {m["text"]}; an empty box is drawn.", (JsonObject)m.DeepClone()));
        if (boxHeight is { } h)
        {
            report["fits"] = layout.Height <= h + 1e-6;
            report["requiredHeight"] = CliJson.Num(layout.Height);
            report["overflowHeight"] = CliJson.Num(Math.Max(0, layout.Height - h));
        }
        report["warnings"] = warnings;
        report["effectiveStyle"] = new JsonObject
        {
            ["font"] = style.Font,
            ["fontUsed"] = Effective(style.Font),
            ["size"] = CliJson.Num(style.Size),
            ["color"] = style.Color.ToHex(),
            ["bold"] = style.Bold,
            ["italic"] = style.Italic,
            ["alignment"] = style.Alignment.ToString().ToLowerInvariant(),
            ["lineSpacing"] = "auto",
            ["padding"] = CliJson.Num(TextLayout.PaddingPt * box.LayoutScale),
            ["borderWidth"] = CliJson.Num(box.LayoutScale),
            ["minHeight"] = CliJson.Num(20 * box.LayoutScale),
            ["background"] = style.Background.ToHex(),
            ["backgroundOpacity"] = CliJson.Num(style.BackgroundOpacity),
            ["border"] = style.Border,
            ["borderColor"] = style.BorderColor.ToHex(),
            ["borderOpacity"] = CliJson.Num(style.BorderOpacity),
            ["highlightColor"] = style.HighlightColor.ToHex(),
            ["highlightOpacity"] = CliJson.Num(style.HighlightOpacity),
        };
        return report;
    }

    private static string Effective(string font) => TextLayout.FontAvailable(font) ? font : TextLayout.FallbackFont;

    private static JsonObject Warning(string code, string message, JsonObject details) =>
        new() { ["code"] = code, ["message"] = message, ["details"] = details };

    /// <summary>Consecutive characters with the same key as UTF-16 ranges [Start, End).</summary>
    private static IEnumerable<(int Start, int End, TKey Key)> Merge<TKey>(IReadOnlyList<TextFontUse> uses, Func<TextFontUse, TKey> key)
    {
        for (int i = 0; i < uses.Count;)
        {
            int j = i;
            while (j + 1 < uses.Count && uses[j + 1].Offset == uses[j].Offset + uses[j].Length && EqualityComparer<TKey>.Default.Equals(key(uses[j + 1]), key(uses[i]))) j++;
            yield return (uses[i].Offset, uses[j].Offset + uses[j].Length, key(uses[i]));
            i = j + 1;
        }
    }
}
