using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Text;

/// <summary>
/// The text box layout contract shared by the editor, the screen, hit tests and PDF outlines (D06). Units are page
/// points. The layout itself comes from a platform <see cref="ITextLayoutEngine"/> (D14a: the WPF FlowDocument engine in
/// PdfNotes.Text.Wpf); everything here is platform independent.
/// </summary>
public static class TextLayout
{
    /// <summary>The fixed inner padding of every text box (D06), in points at layout scale 1.</summary>
    public const double PaddingPt = 4;

    /// <summary>A laid-out character: text, box in local points, line key and its UTF-16 offset in the box text.</summary>
    public sealed record Character(string Text, Rect2 Bounds, int Line, int Offset);

    /// <summary>
    /// A laid-out box. <see cref="Paint"/> is the box as filled/stroked outlines (fill, border, highlights, glyphs) in
    /// local points — what the PDF appearance writes. Engines may carry their own screen drawing besides it.
    /// </summary>
    public abstract class Layout(double height, ImmutableArray<Character> characters)
    {
        public double Height { get; } = height;
        public ImmutableArray<Character> Characters { get; } = characters;
        public abstract TextPaint Paint { get; }
    }

    private static readonly ConditionalWeakTable<TextBoxAnnotation, Layout> Cache = new();
    private static ITextLayoutEngine? engine;

    /// <summary>The platform layout engine. Set once at startup (App, tests and the Probe register the WPF engine).</summary>
    public static ITextLayoutEngine Engine
    {
        get => engine ?? throw new InvalidOperationException("No text layout engine is registered (PdfNotes.Text.Wpf: WpfText.Register()).");
        set => engine = value;
    }

    public static bool HasEngine => engine is not null;

    public static Layout Measure(TextBoxAnnotation box) => Cache.GetValue(box, Engine.Build);
    public static Rect2 Bounds(TextBoxAnnotation box) => new(0, 0, box.WidthPt, Measure(box).Height);
    public static bool FontAvailable(string name) => Engine.FontAvailable(name);

    /// <summary>The font that actually drew each character of <paramref name="box"/> (substitutions, missing glyphs).</summary>
    public static IReadOnlyList<TextFontUse> Fonts(TextBoxAnnotation box) => Engine.Fonts(box);

    /// <summary>Font family names the engine accepts in a run (<see cref="FontAvailable"/> is true for each).</summary>
    public static IReadOnlyList<string> FontFamilies() => Engine.FontFamilies();

    /// <summary>The family a run asks for that the engine uses when the asked one is not installed.</summary>
    public const string FallbackFont = "Malgun Gothic";

    /// <summary>The same box at layout scale 1: width, font sizes and manual highlight heights divided by the scale.</summary>
    public static TextBoxAnnotation Normalized(TextBoxAnnotation box) => box with { LayoutScale = 1, WidthPt = box.WidthPt / box.LayoutScale,
        Runs = box.Runs.Select(r => r with { SizePt = r.SizePt / box.LayoutScale }).ToImmutableArray(),
        Highlights = box.Highlights.Select(h => h with { ManualHeightPt = h.ManualHeightPt / box.LayoutScale }).ToImmutableArray() };

    /// <summary>
    /// One rectangle per line of each highlight's characters (spec 9.4): their laid-out character boxes (auto) or the
    /// manual height around their vertical center. A highlight's rectangles are one nonzero fill, painted once.
    /// </summary>
    public static IEnumerable<(TextHighlightRange Highlight, Rect2[] Lines)> HighlightLines(TextBoxAnnotation box, IEnumerable<Character> characters)
    {
        var all = characters.ToArray();
        foreach (var h in box.Highlights)
        {
            var lines = all.Where(c => c.Offset >= h.Start && c.Offset < h.Start + h.Length).GroupBy(c => c.Line).Select(g =>
            {
                double x0 = g.Min(c => c.Bounds.X), x1 = g.Max(c => c.Bounds.X + c.Bounds.Width);
                double y0 = g.Min(c => c.Bounds.Y), y1 = g.Max(c => c.Bounds.Y + c.Bounds.Height);
                if (h.HeightMode == HighlightHeightMode.Manual && h.ManualHeightPt is { } m) { double mid = (y0 + y1) / 2; y0 = mid - m / 2; y1 = mid + m / 2; }
                return new Rect2(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
            }).Where(r => r.Width > 0 && r.Height > 0).ToArray();
            if (lines.Length > 0) yield return (h, lines);
        }
    }
}

/// <summary>A platform text layout engine (D14a). Builds one box at its own layout scale; results are cached by <see cref="TextLayout"/>.</summary>
public interface ITextLayoutEngine
{
    TextLayout.Layout Build(TextBoxAnnotation box);
    bool FontAvailable(string name);
    IReadOnlyList<TextFontUse> Fonts(TextBoxAnnotation box);
    IReadOnlyList<string> FontFamilies();
}

/// <summary>
/// The font that drew one character (a whole surrogate pair) at UTF-16 <see cref="Offset"/> of a box text:
/// <see cref="Requested"/> is the run's family, <see cref="Font"/> the family whose glyph was drawn (another family
/// when the requested one is not installed or lacks the character). <see cref="Missing"/> means no installed font had
/// the character and an empty-box glyph was drawn. Line breaks are not reported.
/// </summary>
public sealed record TextFontUse(int Offset, int Length, string Text, string Requested, string Font, bool Missing);

/// <summary>A flattened outline figure in local points (0.02pt flattening, D01).</summary>
public sealed record TextFigure(ImmutableArray<Point2> Points, bool Closed);

/// <summary>A paint color: RGB and alpha (0–1, already including the brush opacity).</summary>
public readonly record struct TextColor(byte R, byte G, byte B, double Alpha);

/// <summary>The outline drawing of a laid-out box, in painting order — a tree of groups and filled/stroked shapes.</summary>
public abstract record TextPaint;

/// <summary>A group: transform, optional clip outline, opacity, children painted in order.</summary>
public sealed record TextPaintGroup(Affine Transform, ImmutableArray<TextFigure>? Clip, double Opacity, ImmutableArray<TextPaint> Children) : TextPaint;

/// <summary>A shape: its figures, filled (nonzero or even-odd) and/or stroked with a width.</summary>
public sealed record TextPaintShape(ImmutableArray<TextFigure> Figures, bool EvenOdd, TextColor? Fill, TextColor? Stroke, double StrokeWidth) : TextPaint;
