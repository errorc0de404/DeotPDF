using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;

namespace PdfNotes.Cli;

/// <summary>
/// Formatted text and box style input shared by <c>measure</c> and <c>add text</c> (cli_plan 4.4, 4.6), so the same text,
/// width and style give the same size in both. Defaults are the app's built-in ones (<see cref="TextEditing.Default"/>,
/// <see cref="TextBoxStyle.Default"/>), never a style taken from a PDF.
/// </summary>
public static class RichText
{
    /// <summary>One piece of text with the formatting it overrides (null: the box style's value).</summary>
    public sealed record Span(string Text, bool? Bold = null, bool? Italic = null, bool? Underline = null, bool? Strikeout = null,
        RgbColor? Color = null, double? Size = null, string? Font = null, RgbColor? Highlight = null, double? HighlightOpacity = null);

    /// <summary>Box style: the run defaults plus box fill, border and alignment (cli_plan 4.4 effective style).</summary>
    public sealed record BoxStyle(string Font, double Size, RgbColor Color, bool Bold, bool Italic, TextAlignment Alignment,
        RgbColor Background, double BackgroundOpacity, bool Border, RgbColor BorderColor, double BorderOpacity,
        RgbColor HighlightColor, double HighlightOpacity)
    {
        public static BoxStyle Default { get; } = new(TextEditing.Default.FontFamily, TextEditing.Default.SizePt, TextEditing.Default.Color, false, false,
            TextAlignment.Left, TextBoxStyle.Default.BackgroundColor, TextBoxStyle.Default.BackgroundOpacity, TextBoxStyle.Default.BorderEnabled,
            TextBoxStyle.Default.BorderColor, TextBoxStyle.Default.BorderOpacity, new(0xFF, 0xF5, 0x9D), 1);
    }

    /// <summary>
    /// <c>--text</c>: a JSON list of spans (<c>[{ "text": "…", "bold": true, "color": "#C00000", "highlight": "#FFF59D" }]</c>),
    /// <c>@file</c> holding such a list, or plain text with the short marks <c>**bold**</c>, <c>*italic*</c>,
    /// <c>__underline__</c>, <c>~~strikeout~~</c> and <c>==highlight==</c> (a backslash takes the next character literally).
    /// </summary>
    public static List<Span> ParseText(string value)
    {
        if (value.StartsWith('@'))
        {
            string path = CliPaths.Full(value[1..]);
            if (!File.Exists(path)) throw new CliException("FILE_NOT_FOUND", $"The text file {path} does not exist.");
            value = File.ReadAllText(path);
        }
        string trimmed = value.TrimStart();
        if (trimmed.StartsWith('[')) return ParseSpans(trimmed);
        return ParseMarks(value);
    }

    private static List<Span> ParseSpans(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException e) { throw CliException.Invalid($"The formatted text is not valid JSON: {e.Message}"); }
        var spans = new List<Span>();
        foreach (var node in root as JsonArray ?? throw CliException.Invalid("Formatted text must be a JSON list of spans."))
        {
            if (node is not JsonObject o) throw CliException.Invalid("Each formatted text span is an object with \"text\".");
            foreach (var (key, _) in o)
                if (key is not ("text" or "bold" or "italic" or "underline" or "strikeout" or "color" or "size" or "font" or "highlight" or "highlightOpacity"))
                    throw CliException.Invalid($"Unknown formatted text field \"{key}\".", new JsonObject { ["field"] = key });
            string text = String(o, "text") ?? throw CliException.Invalid("Each formatted text span needs \"text\".");
            RgbColor? highlight = o["highlight"] switch
            {
                null => null,
                JsonValue v when v.GetValueKind() == JsonValueKind.True => BoxStyle.Default.HighlightColor,
                JsonValue v when v.GetValueKind() == JsonValueKind.False => null,
                var v => Color(v, "highlight"),
            };
            spans.Add(new(text, Bool(o, "bold"), Bool(o, "italic"), Bool(o, "underline"), Bool(o, "strikeout"),
                o["color"] is { } c ? Color(c, "color") : null, Number(o, "size", InputLimits.MinFontSizePt, InputLimits.MaxFontSizePt),
                String(o, "font"), highlight, Number(o, "highlightOpacity", 0, 1)));
        }
        return spans;
    }

    private static List<Span> ParseMarks(string text)
    {
        var spans = new List<Span>();
        var current = new StringBuilder();
        bool bold = false, italic = false, underline = false, strike = false, highlight = false;
        void Flush()
        {
            if (current.Length == 0) return;
            spans.Add(new(current.ToString(), bold ? true : null, italic ? true : null, underline ? true : null, strike ? true : null,
                Highlight: highlight ? BoxStyle.Default.HighlightColor : null));
            current.Clear();
        }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            string two = i + 1 < text.Length ? text.Substring(i, 2) : "";
            if (c == '\\' && i + 1 < text.Length) { current.Append(text[++i]); continue; }
            if (two == "**") { Flush(); bold = !bold; i++; }
            else if (two == "__") { Flush(); underline = !underline; i++; }
            else if (two == "~~") { Flush(); strike = !strike; i++; }
            else if (two == "==") { Flush(); highlight = !highlight; i++; }
            else if (c == '*') { Flush(); italic = !italic; }
            else current.Append(c);
        }
        Flush();
        return spans;
    }

    /// <summary><c>--style</c>: inline JSON or a JSON file; fields not given keep <paramref name="defaults"/>.</summary>
    public static BoxStyle ParseStyle(string? value, BoxStyle defaults) => value is null ? defaults : ParseStyle(StyleObject(value), defaults);

    /// <summary>The JSON object of a <c>--style</c> value (inline or a file).</summary>
    public static JsonObject StyleObject(string value)
    {
        string text = value.TrimStart();
        if (!text.StartsWith('{'))
        {
            string path = CliPaths.Full(value);
            if (!File.Exists(path)) throw new CliException("FILE_NOT_FOUND", $"The style file {path} does not exist.");
            text = File.ReadAllText(path);
        }
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException e) { throw CliException.Invalid($"--style is not valid JSON: {e.Message}"); }
        return root as JsonObject ?? throw CliException.Invalid("--style must be a JSON object.");
    }

    /// <summary>The style an existing box shows: its first run's character format, its alignment, fill, border and first highlight.</summary>
    public static BoxStyle StyleOf(TextBoxAnnotation box)
    {
        var d = BoxStyle.Default;
        var run = box.Runs.IsDefaultOrEmpty ? null : box.Runs[0];
        var highlight = box.Highlights.IsDefaultOrEmpty ? null : box.Highlights[0];
        return new(run?.FontFamily ?? d.Font, run?.SizePt ?? d.Size, run?.Color ?? d.Color, run?.Bold ?? d.Bold, run?.Italic ?? d.Italic, box.Alignment,
            box.BoxStyle.BackgroundColor, box.BoxStyle.BackgroundOpacity, box.BoxStyle.BorderEnabled, box.BoxStyle.BorderColor, box.BoxStyle.BorderOpacity,
            highlight?.Color ?? d.HighlightColor, highlight?.Opacity ?? d.HighlightOpacity);
    }

    /// <summary>
    /// <paramref name="box"/> with the style fields of <paramref name="o"/> applied and its text kept: character fields
    /// (font, size, color, bold, italic) go to every run, highlight color and opacity to every highlight range, the others
    /// to the box.
    /// </summary>
    public static TextBoxAnnotation Restyle(TextBoxAnnotation box, JsonObject o)
    {
        var s = ParseStyle(o, StyleOf(box));
        var runs = box.Runs.Select(r => r with
        {
            FontFamily = o.ContainsKey("font") ? s.Font : r.FontFamily,
            SizePt = o.ContainsKey("size") ? s.Size : r.SizePt,
            Color = o.ContainsKey("color") ? s.Color : r.Color,
            Bold = o.ContainsKey("bold") ? s.Bold : r.Bold,
            Italic = o.ContainsKey("italic") ? s.Italic : r.Italic,
        }).ToImmutableArray();
        var highlights = box.Highlights.Select(h => h with
        {
            Color = o.ContainsKey("highlightColor") ? s.HighlightColor : h.Color,
            Opacity = o.ContainsKey("highlightOpacity") ? s.HighlightOpacity : h.Opacity,
        }).ToImmutableArray();
        return box with
        {
            Runs = runs, Highlights = highlights, Alignment = s.Alignment,
            BoxStyle = new TextBoxStyle(s.Background, s.BackgroundOpacity, s.Border, s.BorderColor, s.BorderOpacity),
        };
    }

    public static BoxStyle ParseStyle(JsonObject o, BoxStyle defaults)
    {
        foreach (var (key, _) in o)
            if (key is not ("font" or "size" or "color" or "bold" or "italic" or "alignment" or "background" or "backgroundOpacity" or "border" or
                "borderColor" or "borderOpacity" or "highlightColor" or "highlightOpacity"))
                throw CliException.Invalid($"Unknown style field \"{key}\".", new JsonObject { ["field"] = key });
        var alignment = String(o, "alignment") switch
        {
            null => defaults.Alignment,
            "left" => TextAlignment.Left,
            "center" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            _ => throw CliException.Invalid("Style alignment is left, center or right."),
        };
        return defaults with
        {
            Font = String(o, "font") ?? defaults.Font,
            Size = Number(o, "size", InputLimits.MinFontSizePt, InputLimits.MaxFontSizePt) ?? defaults.Size,
            Color = o["color"] is { } c ? Color(c, "color") : defaults.Color,
            Bold = Bool(o, "bold") ?? defaults.Bold,
            Italic = Bool(o, "italic") ?? defaults.Italic,
            Alignment = alignment,
            Background = o["background"] is { } b ? Color(b, "background") : defaults.Background,
            BackgroundOpacity = Number(o, "backgroundOpacity", 0, 1) ?? (o["background"] is not null && defaults.BackgroundOpacity == 0 ? 1 : defaults.BackgroundOpacity),
            Border = Bool(o, "border") ?? defaults.Border,
            BorderColor = o["borderColor"] is { } bc ? Color(bc, "borderColor") : defaults.BorderColor,
            BorderOpacity = Number(o, "borderOpacity", 0, 1) ?? defaults.BorderOpacity,
            HighlightColor = o["highlightColor"] is { } hc ? Color(hc, "highlightColor") : defaults.HighlightColor,
            HighlightOpacity = Number(o, "highlightOpacity", 0, 1) ?? defaults.HighlightOpacity,
        };
    }

    /// <summary>The text box these spans and style make at <paramref name="width"/> (local origin at its top-left).</summary>
    public static TextBoxAnnotation Build(IReadOnlyList<Span> spans, BoxStyle style, double width, Affine transform, Guid? id = null)
    {
        var text = new StringBuilder();
        var runs = new List<TextRun>();
        var highlights = new List<TextHighlightRange>();
        foreach (var span in spans)
        {
            string piece = span.Text.ReplaceLineEndings("\n");
            if (piece.Length == 0) continue;
            var run = new TextRun(text.Length, piece.Length, span.Font ?? style.Font, span.Size ?? style.Size, span.Color ?? style.Color,
                span.Bold ?? style.Bold, span.Italic ?? style.Italic, span.Underline ?? false, span.Strikeout ?? false);
            if (runs.Count > 0 && runs[^1] with { Start = 0, Length = 0 } == run with { Start = 0, Length = 0 })
                runs[^1] = runs[^1] with { Length = runs[^1].Length + piece.Length };
            else runs.Add(run);
            if (span.Highlight is { } h)
            {
                var range = new TextHighlightRange(text.Length, piece.Length, HighlightHeightMode.Auto, null, h, span.HighlightOpacity ?? style.HighlightOpacity);
                if (highlights.Count > 0 && highlights[^1].Start + highlights[^1].Length == range.Start && highlights[^1] with { Start = 0, Length = 0 } == range with { Start = 0, Length = 0 })
                    highlights[^1] = highlights[^1] with { Length = highlights[^1].Length + range.Length };
                else highlights.Add(range);
            }
            text.Append(piece);
        }
        if (text.Length == 0 || string.IsNullOrWhiteSpace(text.ToString())) throw CliException.Invalid("The text is empty.");
        var box = new TextBoxAnnotation(id ?? Guid.NewGuid(), false, transform, text.ToString(), [.. runs], style.Alignment, width,
            new TextBoxStyle(style.Background, style.BackgroundOpacity, style.Border, style.BorderColor, style.BorderOpacity), [.. highlights]);
        if (ModelValidator.ValidateAnnotation(box, _ => true) is { } error) throw CliException.Invalid($"The formatted text is not valid: {error}");
        return box;
    }

    /// <summary>Smallest width (to 0.25 pt) that keeps every written line on one line.</summary>
    public static double OneLineWidth(IReadOnlyList<Span> spans, BoxStyle style)
    {
        var wide = TextLayout.Measure(Build(spans, style, 100_000, Affine.Identity));
        int written = wide.Characters.Select(c => c.Line).Distinct().Count();
        double right = wide.Characters.Length == 0 ? 0 : wide.Characters.Max(c => c.Bounds.X + c.Bounds.Width);
        double width = Math.Max(16, Math.Ceiling((right + TextLayout.PaddingPt) * 4) / 4);
        for (int i = 0; i < 80; i++, width += 0.25)
            if (TextLayout.Measure(Build(spans, style, width, Affine.Identity)).Characters.Select(c => c.Line).Distinct().Count() <= written) break;
        return width;
    }

    private static string? String(JsonObject o, string key) => o[key] switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => throw CliException.Invalid($"\"{key}\" must be a string."),
    };

    private static bool? Bool(JsonObject o, string key) => o[key] switch
    {
        null => null,
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValue<bool>(),
        _ => throw CliException.Invalid($"\"{key}\" must be true or false."),
    };

    private static double? Number(JsonObject o, string key, double min, double max)
    {
        if (o[key] is null) return null;
        double d = o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : double.NaN;
        if (!double.IsFinite(d) || d < min || d > max) throw CliException.Invalid($"\"{key}\" must be a number from {min} to {max}.");
        return d;
    }

    private static RgbColor Color(JsonNode node, string key) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String && RgbColor.TryParse(v.GetValue<string>().ToUpperInvariant(), out var c)
            ? c : throw CliException.Invalid($"\"{key}\" must be a color #RRGGBB.");
}
