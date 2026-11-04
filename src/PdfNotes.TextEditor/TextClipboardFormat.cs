using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;

namespace PdfNotes.TextEditor;

/// <summary>
/// Formatted text on the clipboard between text boxes of this app (roadmap P2 S4): the characters with their runs and
/// box highlights, offsets from the copied text's start. Other programs get the plain text and paste only that (spec
/// 6.1); this format is only read back by the app. The name follows the app's other clipboard formats
/// (<c>PdfNotes.Annotations.v1</c>, <c>PdfNotes.Pages.v1</c>).
/// </summary>
public static class TextClipboardFormat
{
    public const string Name = "PdfNotes.Text.v1";

    private sealed record RunData(int Start, int Length, string Font, double Size, string Color, bool Bold, bool Italic, bool Underline, bool Strikethrough);
    private sealed record HighlightData(int Start, int Length, bool Manual, double? Height, string Color, double Opacity);
    private sealed record Data(string Text, RunData[] Runs, HighlightData[] Highlights);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The characters [<paramref name="start"/>, <paramref name="end"/>) of a box with their formatting.</summary>
    public static string Write(TextBoxAnnotation box, int start, int end)
    {
        var runs = box.Runs.Where(r => r.Start < end && r.Start + r.Length > start).Select(r =>
        {
            int a = Math.Max(start, r.Start), z = Math.Min(end, r.Start + r.Length);
            return new RunData(a - start, z - a, r.FontFamily, r.SizePt, Hex(r.Color), r.Bold, r.Italic, r.Underline, r.Strikethrough);
        }).ToArray();
        var highlights = box.Highlights.Where(h => h.Start < end && h.Start + h.Length > start).Select(h =>
        {
            int a = Math.Max(start, h.Start), z = Math.Min(end, h.Start + h.Length);
            return new HighlightData(a - start, z - a, h.HeightMode == HighlightHeightMode.Manual, h.ManualHeightPt, Hex(h.Color), h.Opacity);
        }).ToArray();
        return JsonSerializer.Serialize(new Data(box.Text[start..end], runs, highlights), Options);
    }

    /// <summary>
    /// Reads a formatted copy: its text, runs and highlights from offset 0, or null when it is not one. Runs are made
    /// to partition the text on grapheme boundaries (characters no run covers take <paramref name="fallback"/>).
    /// </summary>
    public static (string Text, ImmutableArray<TextRun> Runs, ImmutableArray<TextHighlightRange> Highlights)? Read(string? data, TextRun fallback)
    {
        if (string.IsNullOrEmpty(data)) return null;
        Data? parsed;
        try { parsed = JsonSerializer.Deserialize<Data>(data, Options); }
        catch (JsonException) { return null; }
        if (parsed?.Text is not { } text || parsed.Runs is null) return null;
        var styles = new TextRun[text.Length];
        foreach (var r in parsed.Runs)
        {
            if (r.Start < 0 || r.Length <= 0 || r.Start + r.Length > text.Length || string.IsNullOrWhiteSpace(r.Font) || !(r.Size > 0) || Color(r.Color) is not { } color) continue;
            var run = new TextRun(0, 0, r.Font, TextEditing.InputSize(r.Size), color, r.Bold, r.Italic, r.Underline, r.Strikethrough);
            for (int i = r.Start; i < r.Start + r.Length; i++) styles[i] = run;
        }
        var runs = new List<TextRun>();
        var starts = TextUnits.GraphemeStarts(text);
        for (int i = 0; i < text.Length;)
        {
            int next = i + 1;
            while (!starts[next]) next++;
            TextEditing.Add(runs, (styles[i] ?? fallback) with { Start = i, Length = next - i });
            i = next;
        }
        var highlights = new List<TextHighlightRange>();
        foreach (var h in parsed.Highlights ?? [])
        {
            if (h.Start < 0 || h.Length <= 0 || h.Start + h.Length > text.Length || Color(h.Color) is not { } color) continue;
            var (a, z) = TextEditing.Range(text, h.Start, h.Length);
            if (highlights.Count > 0 && highlights[^1].Start + highlights[^1].Length > a) continue;
            highlights.Add(new(a, z - a, h.Manual ? HighlightHeightMode.Manual : HighlightHeightMode.Auto, h.Manual ? h.Height : null, color, Math.Clamp(h.Opacity, 0, 1)));
        }
        return (text, [.. runs], [.. highlights]);
    }

    private static string Hex(RgbColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static RgbColor? Color(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)
            ? new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v) : null;
}
