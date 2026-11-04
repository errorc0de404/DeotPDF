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
    /// A laid-out line (P2 S4, for carets and selections): the UTF-16 range [<see cref="Start"/>, <see cref="End"/>) of
    /// its characters (the hard line break that ends it left out; a soft-wrapped line keeps its trailing spaces), and in
    /// local points its top, height, baseline and the left edge and width of its shown text. An empty line has
    /// <see cref="Start"/> = <see cref="End"/>.
    /// </summary>
    public sealed record Line(int Start, int End, double Top, double Height, double Baseline, double Left, double Width);

    /// <summary>
    /// A laid-out box. <see cref="Paint"/> is the box as filled/stroked outlines (fill, border, highlights, glyphs) in
    /// local points — what the PDF appearance writes. Engines may carry their own screen drawing besides it.
    /// </summary>
    public abstract class Layout(double height, ImmutableArray<Character> characters)
    {
        private ImmutableArray<Line> lines;

        public double Height { get; } = height;
        public ImmutableArray<Character> Characters { get; } = characters;
        public abstract TextPaint Paint { get; }

        /// <summary>The lines from top to bottom (built once).</summary>
        public ImmutableArray<Line> Lines => lines.IsDefault ? lines = BuildLines() : lines;

        /// <summary>
        /// Lines as far as the characters tell them: each group of characters with one line key. An engine that knows
        /// its lines (empty ones, baselines) overrides this; this fallback has no empty lines and puts the baseline at
        /// four fifths of the line.
        /// </summary>
        protected virtual ImmutableArray<Line> BuildLines() => [.. Characters.GroupBy(c => c.Line).Select(g =>
        {
            double top = g.Min(c => c.Bounds.Y), bottom = g.Max(c => c.Bounds.Y + c.Bounds.Height);
            double left = g.Min(c => c.Bounds.X), right = g.Max(c => c.Bounds.X + c.Bounds.Width);
            var last = g.Last();
            return new Line(g.First().Offset, last.Offset + last.Text.Length, top, bottom - top, top + .8 * (bottom - top), left, right - left);
        })];
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

    public static Layout Measure(TextBoxAnnotation box) => Cache.GetValue(box, Lookup);

    // The layout depends only on the content, not on the ID, lock or position. A moved, dragged or re-read box is a new
    // record, so without this second cache every drag frame and every edit laid the whole box out again (100-700 ms
    // for a long box). Most recently used first; the instance cache above keeps layouts of live boxes anyway.
    private const int ContentCacheSize = 256;
    private static readonly Dictionary<ContentKey, LinkedListNode<(ContentKey Key, Layout Layout)>> ByContent = new();
    private static readonly LinkedList<(ContentKey Key, Layout Layout)> Recent = new();

    // Layouts being built in the background; a caller that needs one of them now waits for it instead of building twice.
    private static readonly Dictionary<ContentKey, (TextBoxAnnotation Box, Task<Layout> Task)> Pending = new();

    private static Layout Lookup(TextBoxAnnotation box)
    {
        var key = new ContentKey(box);
        Task<Layout>? pending;
        lock (ByContent)
        {
            if (Cached(key) is { } hit) return hit;
            if (Pending.TryGetValue(key, out var p)) { Engine.BuildAsync(p.Box, urgent: true); pending = p.Task; }
            else pending = null;
        }
        if (pending is not null) return pending.GetAwaiter().GetResult();
        var layout = Engine.Build(box);
        lock (ByContent) return Cached(key) ?? Store(key, layout);
    }

    /// <summary>The layout when it is already known; never lays out.</summary>
    public static bool TryMeasure(TextBoxAnnotation box, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Layout? layout)
    {
        if (Cache.TryGetValue(box, out layout)) return true;
        lock (ByContent) layout = Cached(new ContentKey(box));
        if (layout is null) return false;
        var known = layout;
        Cache.GetValue(box, _ => known);
        return true;
    }

    /// <summary>
    /// The layout without blocking the caller: built on the engine's workers. <paramref name="urgent"/> requests (the box
    /// is on screen) go before background ones (thumbnails); an urgent request for a queued box moves it forward.
    /// </summary>
    public static Task<Layout> MeasureAsync(TextBoxAnnotation box, bool urgent = true)
    {
        if (TryMeasure(box, out var known)) return Task.FromResult(known);
        var key = new ContentKey(box);
        var done = new TaskCompletionSource<Layout>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Layout> build;
        lock (ByContent)
        {
            if (Cached(key) is { } hit) return Task.FromResult(hit);
            if (Pending.TryGetValue(key, out var p))
            {
                if (urgent) Engine.BuildAsync(p.Box, urgent: true);
                return p.Task;
            }
            Pending[key] = (box, done.Task);
            build = Engine.BuildAsync(box, urgent);
        }
        build.ContinueWith(t =>
        {
            lock (ByContent)
            {
                Pending.Remove(key);
                if (t.IsCompletedSuccessfully) Store(key, t.Result);
            }
            if (t.IsCompletedSuccessfully) done.SetResult(t.Result);
            else done.SetException(t.Exception?.InnerExceptions ?? [new TaskCanceledException()]);
        }, TaskContinuationOptions.ExecuteSynchronously);
        return done.Task;
    }

    // Call under the ByContent lock.
    private static Layout? Cached(ContentKey key)
    {
        if (!ByContent.TryGetValue(key, out var hit)) return null;
        Recent.Remove(hit); Recent.AddFirst(hit);
        return hit.Value.Layout;
    }

    private static Layout Store(ContentKey key, Layout layout)
    {
        if (ByContent.TryGetValue(key, out var raced)) return raced.Value.Layout;
        ByContent[key] = Recent.AddFirst((key, layout));
        if (Recent.Count > ContentCacheSize) { ByContent.Remove(Recent.Last!.Value.Key); Recent.RemoveLast(); }
        return layout;
    }

    /// <summary>Everything a layout depends on: text, runs, alignment, width, box style, highlights and layout scale.</summary>
    private sealed class ContentKey : IEquatable<ContentKey>
    {
        private readonly TextBoxAnnotation box;
        private readonly int hash;

        public ContentKey(TextBoxAnnotation box)
        {
            this.box = box;
            var h = new HashCode();
            h.Add(box.Text); h.Add(box.Alignment); h.Add(box.WidthPt); h.Add(box.BoxStyle); h.Add(box.LayoutScale);
            foreach (var r in box.Runs) h.Add(r);
            foreach (var x in box.Highlights) h.Add(x);
            hash = h.ToHashCode();
        }

        public bool Equals(ContentKey? other) => other is not null && (ReferenceEquals(box, other.box) || hash == other.hash &&
            box.Text == other.box.Text && box.Alignment == other.box.Alignment && box.WidthPt.Equals(other.box.WidthPt) &&
            box.BoxStyle == other.box.BoxStyle && box.LayoutScale.Equals(other.box.LayoutScale) &&
            box.Runs.SequenceEqual(other.box.Runs) && box.Highlights.SequenceEqual(other.box.Highlights));

        public override bool Equals(object? obj) => Equals(obj as ContentKey);
        public override int GetHashCode() => hash;
    }
    public static Rect2 Bounds(TextBoxAnnotation box) => new(0, 0, box.WidthPt, Measure(box).Height);
    public static bool FontAvailable(string name) => Engine.FontAvailable(name);

    /// <summary>The font that actually drew each character of <paramref name="box"/> (substitutions, missing glyphs).</summary>
    public static IReadOnlyList<TextFontUse> Fonts(TextBoxAnnotation box) => Engine.Fonts(box);

    /// <summary>Font family names the engine accepts in a run (<see cref="FontAvailable"/> is true for each).</summary>
    public static IReadOnlyList<string> FontFamilies() => Engine.FontFamilies();

    /// <summary>The family the engine uses for a run whose family is not installed.</summary>
    public static string FallbackFont => Engine.FallbackFont;

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

    /// <summary>Builds without blocking the caller; <paramref name="urgent"/> work goes first. Called again for a queued box, it may raise its priority.</summary>
    Task<TextLayout.Layout> BuildAsync(TextBoxAnnotation box, bool urgent) => Task.Run(() => Build(box));

    bool FontAvailable(string name);

    /// <summary>The family used for a run whose family is not installed (P2 S1: chosen by the engine, per platform).</summary>
    string FallbackFont { get; }

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
