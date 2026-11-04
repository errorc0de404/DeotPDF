using System.Collections.Immutable;
using PdfNotes.Core.Models;
using PdfNotes.Text.LineBreaking;

namespace PdfNotes.Text;

/// <summary>
/// The SkiaSharp + HarfBuzz text engine (roadmap P2). Its rules are its own, not the WPF engine's (decision B): one
/// paragraph per hard line break, the default UAX #14 break opportunities filled greedily (a word longer than the line
/// breaks between graphemes), white space hanging past the line end, tab stops every <see cref="TabStop"/> points, line
/// height from the fonts' hhea metrics (largest ascent plus largest descent and gap on the line, glyphs on one baseline),
/// and the fonts' default OpenType features, kerning included.
/// </summary>
public sealed class SkiaTextEngine : ITextLayoutEngine
{
    public const double TabStop = 48;
    private const double MinimumWidth = 16, MinimumHeight = 20;

    public string FallbackFont => SkiaFonts.FallbackFamily;

    public TextLayout.Layout Build(TextBoxAnnotation box)
    {
        // Lay out in the original units and scale the whole result, so a uniformly reduced page keeps its line breaks.
        return Layout(box);
    }

    /// <summary>The Skia layout of <paramref name="box"/>, uncached (callers go through <see cref="TextLayout.Measure"/>).</summary>
    public static SkiaTextLayout Layout(TextBoxAnnotation box) => box.LayoutScale != 1
        ? Layout(TextLayout.Normalized(box)).Scaled(box.LayoutScale)
        : Arrange(box, SkiaShaper.Items(box));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBoxAnnotation, Task<TextLayout.Layout>> Building = new();

    /// <summary>
    /// Builds on a worker thread (no STA). A box already being built gets the same task, so <see cref="TextLayout"/>
    /// waiting for a queued box does not start a second build. Layouts take about 0.1 ms, so there is no priority queue.
    /// </summary>
    public Task<TextLayout.Layout> BuildAsync(TextBoxAnnotation box, bool urgent)
    {
        lock (Building)
        {
            if (Building.TryGetValue(box, out var running)) return running;
            var task = Task.Run(() => Build(box));
            Building.AddOrUpdate(box, task);
            task.ContinueWith(_ => { lock (Building) Building.Remove(box); }, TaskContinuationOptions.ExecuteSynchronously);
            return task;
        }
    }

    public bool FontAvailable(string name) => SkiaFonts.Available(name);
    public IReadOnlyList<TextFontUse> Fonts(TextBoxAnnotation box) => FontUses(box);

    /// <summary>
    /// The face that draws each character of the box (line feeds left out): the run's family, the fallback family or a
    /// font Skia found for the character. A character is missing when its cluster drew glyph 0 (.notdef), or, inside a
    /// cluster, when the face has no glyph for it (white space and invisible format characters never are).
    /// </summary>
    public static IReadOnlyList<TextFontUse> FontUses(TextBoxAnnotation box)
    {
        var result = new List<TextFontUse>();
        foreach (var item in SkiaShaper.Items(TextLayout.Normalized(box)))
        {
            if (item.Text == "\n") continue;
            var (rune, _) = TextRunes.At(item.Text, 0);
            bool missing = item.Glyphs.Length > 0 ? item.Glyphs.Any(g => g.Id == 0) && !System.Text.Rune.IsWhiteSpace(rune)
                : !System.Text.Rune.IsWhiteSpace(rune) && !System.Text.Rune.IsControl(rune) && !Invisible(rune) && !item.Face.HasGlyph(rune.Value);
            result.Add(new(item.Offset, item.Text.Length, item.Text, item.Run.FontFamily, item.Face.Family, missing));
        }
        return result;
    }

    /// <summary>Format characters (joiners, marks of direction), variation selectors and tags: drawn as nothing.</summary>
    private static bool Invisible(System.Text.Rune r) => System.Text.Rune.GetUnicodeCategory(r) == System.Globalization.UnicodeCategory.Format ||
        r.Value is >= 0xFE00 and <= 0xFE0F or >= 0xE0000 and <= 0xE0FFF or 0x034F or >= 0x180B and <= 0x180F;
    public IReadOnlyList<string> FontFamilies() => SkiaFonts.Families();

    /// <summary>A line: its items [<see cref="Start"/>, <see cref="End"/>) and the width they show (without hanging space).</summary>
    internal readonly record struct LineSpan(int Start, int End, double Width);

    private static SkiaTextLayout Arrange(TextBoxAnnotation box, List<ShapedItem> items)
    {
        double padding = TextLayout.PaddingPt;
        double content = Math.Max(MinimumWidth, box.WidthPt) - 2 * padding;
        var spans = Wrap(items, content);

        var empty = SkiaFonts.Face(EmptyStyle(box).FontFamily, EmptyStyle(box).Bold, EmptyStyle(box).Italic);
        var emptySize = EmptyStyle(box).SizePt;
        var lines = ImmutableArray.CreateBuilder<SkiaTextLine>(spans.Count);
        var characters = ImmutableArray.CreateBuilder<TextLayout.Character>(items.Count);
        var placed = ImmutableArray.CreateBuilder<SkiaPlacedItem>(items.Count);
        double y = padding;
        for (int n = 0; n < spans.Count; n++)
        {
            var span = spans[n];
            double ascent = 0, below = 0;
            for (int k = span.Start; k < span.End; k++) { ascent = Math.Max(ascent, items[k].Ascent); below = Math.Max(below, items[k].Below); }
            if (span.End == span.Start)
            {
                // An empty line takes the font of the line break before it (the last one), or the box's typing style.
                var source = span.Start > 0 ? items[span.Start - 1] : null;
                ascent = source?.Ascent ?? empty.Ascent * emptySize;
                below = source?.Below ?? (empty.Descent + empty.LineGap) * emptySize;
            }
            double left = padding + box.Alignment switch
            {
                TextAlignment.Center => Math.Max(0, (content - span.Width) / 2),
                TextAlignment.Right => Math.Max(0, content - span.Width),
                _ => 0,
            };
            double x = 0, baseline = y + ascent;
            for (int k = span.Start; k < span.End; k++)
            {
                var item = items[k];
                double advance = item.Tab ? TabAdvance(x) : item.Advance;
                if (!item.HardBreak)
                {
                    characters.Add(new(item.Text, new(left + x, baseline - item.Ascent, Math.Max(.001, advance), item.Ascent + item.Below), n, item.Offset));
                    placed.Add(new(item, left + x, baseline, advance, n));
                }
                x += advance;
            }
            int start = span.Start < items.Count ? items[span.Start].Offset : box.Text.Length;
            int end = span.Start;
            while (end < span.End && !items[end].HardBreak) end++;
            int endOffset = end < items.Count ? items[end].Offset : box.Text.Length;
            lines.Add(new(start, endOffset, y, ascent + below, baseline, left, span.Width));
            y += ascent + below;
        }
        return new SkiaTextLayout(Math.Max(MinimumHeight, y + padding), characters.ToImmutable(), lines.ToImmutable(), placed.ToImmutable(), box, 1, null);
    }

    private static TextRun EmptyStyle(TextBoxAnnotation box) => box.Runs.Length > 0 ? box.Runs[^1] : PdfNotes.Core.Commands.TextEditing.Default;

    private static double TabAdvance(double x) => TabStop - x % TabStop;

    /// <summary>
    /// Greedy line filling. A hard break ends its line. When a character that does not hang would pass the right edge,
    /// the line ends at its last break opportunity; without one, before that character's grapheme (a line always keeps
    /// at least one grapheme). Ends with an empty line after a final hard break, and one empty line for empty text.
    /// </summary>
    internal static List<LineSpan> Wrap(List<ShapedItem> items, double width)
    {
        var lines = new List<LineSpan>();
        int start = 0;
        while (start < items.Count)
        {
            double x = 0;
            int lastBreak = -1, end = items.Count;
            for (int k = start; k < items.Count; k++)
            {
                var item = items[k];
                if (k > start && item.BreakBefore == LineBreak.Mandatory) { end = k; break; }
                if (k > start && item.BreakBefore == LineBreak.Allowed) lastBreak = k;
                double advance = item.Tab ? TabAdvance(x) : item.Advance;
                if (!item.Hangs && k > start && x + advance > width + 1e-7)
                {
                    if (lastBreak > start) end = lastBreak;
                    else
                    {
                        int cut = k;
                        while (cut > start && !items[cut].GraphemeStart) cut--;
                        if (cut == start) { cut = k + 1; while (cut < items.Count && !items[cut].GraphemeStart) cut++; }
                        end = cut;
                    }
                    break;
                }
                x += advance;
            }
            lines.Add(new(start, end, Shown(items, start, end)));
            start = end;
        }
        if (items.Count == 0 || items[^1].HardBreak) lines.Add(new(items.Count, items.Count, 0));
        return lines;
    }

    /// <summary>The width of a line without its hanging white space at the end.</summary>
    private static double Shown(List<ShapedItem> items, int start, int end)
    {
        double x = 0, shown = 0;
        for (int k = start; k < end; k++)
        {
            x += items[k].Tab ? TabAdvance(x) : items[k].Advance;
            if (!items[k].Hangs) shown = x;
        }
        return shown;
    }
}

/// <summary>
/// A laid-out line at layout scale 1: its UTF-16 range without the hard break that ends it, top, height and baseline in
/// local points, the x where it starts and its shown width.
/// </summary>
public readonly record struct SkiaTextLine(int Start, int End, double Top, double Height, double Baseline, double Left, double Width);

/// <summary>A drawn character: the shaped item, its pen x and baseline in local points, its advance and line.</summary>
internal readonly record struct SkiaPlacedItem(ShapedItem Item, double X, double Baseline, double Advance, int Line);

/// <summary>
/// A Skia layout: the neutral result plus the lines and placed glyphs that the PDF outlines, the screen renderer
/// (<see cref="SkiaTextRenderer"/>) and the Avalonia editor (S6) draw from. A layout at another scale keeps the layout
/// at scale 1 it was made from (<see cref="Unscaled"/>); <see cref="Scale"/> multiplies it.
/// </summary>
public sealed class SkiaTextLayout : TextLayout.Layout
{
    private readonly Lazy<TextPaint> paint;

    internal SkiaTextLayout(double height, ImmutableArray<TextLayout.Character> characters, ImmutableArray<SkiaTextLine> lines,
        ImmutableArray<SkiaPlacedItem> placed, TextBoxAnnotation box, double scale, SkiaTextLayout? unscaled) : base(height, characters)
    {
        SkiaLines = lines; Placed = placed; Box = box; Scale = scale; Unscaled = unscaled ?? this;
        paint = new(() => SkiaTextOutlines.Build(this));
    }

    /// <summary>Lines at layout scale 1 (multiply by <see cref="Scale"/>); <see cref="TextLayout.Layout.Lines"/> has them scaled.</summary>
    public ImmutableArray<SkiaTextLine> SkiaLines { get; }

    protected override ImmutableArray<TextLayout.Line> BuildLines() => [.. SkiaLines.Select(l => new TextLayout.Line(l.Start, l.End,
        l.Top * Scale, l.Height * Scale, l.Baseline * Scale, l.Left * Scale, l.Width * Scale))];
    internal ImmutableArray<SkiaPlacedItem> Placed { get; }
    /// <summary>The box at layout scale 1 that was laid out.</summary>
    public TextBoxAnnotation Box { get; }
    public double Scale { get; }
    /// <summary>This layout at scale 1 (itself when <see cref="Scale"/> is 1).</summary>
    public SkiaTextLayout Unscaled { get; }

    public override TextPaint Paint => paint.Value;

    internal SkiaTextLayout Scaled(double s) => new(Height * s, [.. Characters.Select(c => c with
        { Bounds = new(c.Bounds.X * s, c.Bounds.Y * s, c.Bounds.Width * s, c.Bounds.Height * s) })], SkiaLines, Placed, Box, s, this);
}
