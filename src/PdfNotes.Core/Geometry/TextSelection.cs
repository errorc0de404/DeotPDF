using System.Collections.Immutable;
using System.Text;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Geometry;

/// <summary>
/// One extracted PDF glyph in canonical page points: its box and the unit reading direction of its baseline.
/// Glyphs keep the engine's reading order; generated characters (spaces, line ends) may have an empty box.
/// </summary>
public sealed record TextGlyph(string Text, Quad Box, Point2 Direction)
{
    public Point2 Center => new((Box.UpperLeft.X + Box.UpperRight.X + Box.LowerLeft.X + Box.LowerRight.X) / 4,
        (Box.UpperLeft.Y + Box.UpperRight.Y + Box.LowerLeft.Y + Box.LowerRight.Y) / 4);

    /// <summary>True when the box has no area (engine-generated characters); such glyphs carry text but no shape.</summary>
    public bool IsEmpty
    {
        get
        {
            var (s0, s1, t0, t1) = TextSelection.Extent([this], Direction);
            return s1 - s0 < 0.01 || t1 - t0 < 0.01;
        }
    }
}

/// <summary>
/// PDF glyph selection and text highlight geometry (spec 9.2–9.3). A selection is a caret range [start, end) over the
/// page's glyphs in reading order. Highlights store one quad per visual line run in the glyphs' own orientation
/// (upper-left, upper-right, lower-left, lower-right relative to the text), so rotated text and rotated pages stay
/// aligned; a manual height is applied around each quad's vertical center when painting.
/// </summary>
public static class TextSelection
{
    /// <summary>Glyphs whose direction differs more than this (degrees) never share a line.</summary>
    public const double SameLineAngleDegrees = 10;

    /// <summary>"Up" for a reading direction in the +Y-down page space.</summary>
    public static Point2 Up(Point2 direction) => new(direction.Y, -direction.X);

    /// <summary>Extent of the glyph boxes along <paramref name="direction"/> (s) and its up vector (t).</summary>
    public static (double S0, double S1, double T0, double T1) Extent(IEnumerable<TextGlyph> glyphs, Point2 direction)
    {
        var up = Up(direction);
        double s0 = double.MaxValue, s1 = double.MinValue, t0 = double.MaxValue, t1 = double.MinValue;
        foreach (var g in glyphs)
            foreach (var p in new[] { g.Box.UpperLeft, g.Box.UpperRight, g.Box.LowerLeft, g.Box.LowerRight })
            {
                double s = p.X * direction.X + p.Y * direction.Y, t = p.X * up.X + p.Y * up.Y;
                s0 = Math.Min(s0, s); s1 = Math.Max(s1, s); t0 = Math.Min(t0, t); t1 = Math.Max(t1, t);
            }
        return (s0, s1, t0, t1);
    }

    /// <summary>Distance from <paramref name="p"/> to the glyph box (0 inside).</summary>
    public static double Distance(TextGlyph glyph, Point2 p)
    {
        var (s0, s1, t0, t1) = Extent([glyph], glyph.Direction);
        var up = Up(glyph.Direction);
        double s = p.X * glyph.Direction.X + p.Y * glyph.Direction.Y, t = p.X * up.X + p.Y * up.Y;
        double ds = Math.Max(0, Math.Max(s0 - s, s - s1)), dt = Math.Max(0, Math.Max(t0 - t, t - t1));
        return Math.Sqrt(ds * ds + dt * dt);
    }

    /// <summary>
    /// The caret (0..Count) nearest to <paramref name="p"/>: before the closest glyph, or after it when the point is past
    /// its middle along the reading direction. Null when no glyph is within <paramref name="maxDistance"/>.
    /// </summary>
    public static int? CaretAt(IReadOnlyList<TextGlyph> glyphs, Point2 p, double maxDistance = double.PositiveInfinity)
    {
        int best = -1; double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < glyphs.Count; i++)
        {
            if (glyphs[i].IsEmpty) continue;
            double d = Distance(glyphs[i], p);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        if (best < 0 || bestDistance > maxDistance) return null;
        var g = glyphs[best];
        double along = (p.X - g.Center.X) * g.Direction.X + (p.Y - g.Center.Y) * g.Direction.Y;
        return along > 0 ? best + 1 : best;
    }

    /// <summary>Letters (any script, including Hangul), digits, combining marks and connectors form words.</summary>
    public static bool IsWordText(string text) => text.Length > 0 && text.EnumerateRunes().All(r =>
        Rune.IsLetterOrDigit(r) || Rune.GetUnicodeCategory(r) is System.Globalization.UnicodeCategory.NonSpacingMark or
            System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.ConnectorPunctuation);

    /// <summary>
    /// The word [start, end) around item <paramref name="index"/> of a sequence (double-click, spec 9.3): consecutive word
    /// items, stopping at spaces, punctuation and where <paramref name="joined"/> says two neighbours are not adjacent
    /// (another line, a gap). Null when the item itself is not part of a word.
    /// </summary>
    public static (int Start, int End)? Word(int count, int index, Func<int, string> text, Func<int, int, bool>? joined = null)
    {
        if (index < 0 || index >= count || !IsWordText(text(index))) return null;
        int start = index, end = index + 1;
        while (start > 0 && IsWordText(text(start - 1)) && (joined?.Invoke(start - 1, start) ?? true)) start--;
        while (end < count && IsWordText(text(end)) && (joined?.Invoke(end - 1, end) ?? true)) end++;
        return (start, end);
    }

    /// <summary>The PDF word under <paramref name="p"/> as a caret range, or null when no word glyph is within <paramref name="tolerance"/>.</summary>
    public static (int Start, int End)? WordAt(IReadOnlyList<TextGlyph> glyphs, Point2 p, double tolerance)
    {
        int best = -1; double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < glyphs.Count; i++)
        {
            if (glyphs[i].IsEmpty) continue;
            double d = Distance(glyphs[i], p);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        if (best < 0 || bestDistance > tolerance) return null;
        // Neighbours belong to the same word only on the same visual line.
        return Word(glyphs.Count, best, i => glyphs[i].Text, (a, b) => !glyphs[a].IsEmpty && !glyphs[b].IsEmpty && LineQuads(glyphs, a, b + 1).Length == 1);
    }

    /// <summary>True when a non-empty glyph is within <paramref name="tolerance"/> of <paramref name="p"/> (I-beam cursor).</summary>
    public static bool IsOverText(IReadOnlyList<TextGlyph> glyphs, Point2 p, double tolerance) =>
        glyphs.Any(g => !g.IsEmpty && Distance(g, p) <= tolerance);

    /// <summary>Original text of [start, end), as the engine reports it (including its line breaks).</summary>
    public static string TextOf(IReadOnlyList<TextGlyph> glyphs, int start, int end)
    {
        var text = new StringBuilder();
        for (int i = Math.Max(0, start); i < Math.Min(end, glyphs.Count); i++) text.Append(glyphs[i].Text);
        return text.ToString();
    }

    /// <summary>
    /// One quad per visual line of [start, end): consecutive non-empty glyphs with the same direction whose centers
    /// stay within half a line height of the line and do not jump back along it. Each quad bounds the glyph boxes in
    /// the line's own orientation.
    /// </summary>
    public static ImmutableArray<Quad> LineQuads(IReadOnlyList<TextGlyph> glyphs, int start, int end) =>
        LineRuns(glyphs, start, end).Select(r => r.Quad).ToImmutableArray();

    /// <summary>
    /// The visual lines of [start, end) as <see cref="LineQuads"/> forms them, each with the glyph range from its first to
    /// its last non-empty glyph (empty glyphs between lines belong to none).
    /// </summary>
    public static ImmutableArray<(int Start, int End, Quad Quad)> LineRuns(IReadOnlyList<TextGlyph> glyphs, int start, int end)
    {
        var runs = ImmutableArray.CreateBuilder<(int Start, int End, Quad Quad)>();
        var line = new List<TextGlyph>();
        int first = -1, last = -1;
        double cos = Math.Cos(SameLineAngleDegrees * Math.PI / 180);
        for (int i = Math.Max(0, start); i < Math.Min(end, glyphs.Count); i++)
        {
            var g = glyphs[i];
            if (g.IsEmpty) continue;
            if (line.Count > 0)
            {
                var u = line[0].Direction;
                var (s0, s1, t0, t1) = Extent(line, u);
                var up = Up(u);
                double s = g.Center.X * u.X + g.Center.Y * u.Y, t = g.Center.X * up.X + g.Center.Y * up.Y;
                double height = t1 - t0;
                bool same = g.Direction.X * u.X + g.Direction.Y * u.Y >= cos && Math.Abs(t - (t0 + t1) / 2) <= height / 2 && s >= s1 - height;
                if (!same) { runs.Add((first, last + 1, Bound(line))); line.Clear(); }
            }
            if (line.Count == 0) first = i;
            line.Add(g);
            last = i;
        }
        if (line.Count > 0) runs.Add((first, last + 1, Bound(line)));
        return runs.ToImmutable();
    }

    private static Quad Bound(List<TextGlyph> line)
    {
        var u = line[0].Direction; var up = Up(u);
        var (s0, s1, t0, t1) = Extent(line, u);
        Point2 At(double s, double t) => new(s * u.X + t * up.X, s * u.Y + t * up.Y);
        return new(At(s0, t1), At(s1, t1), At(s0, t0), At(s1, t0));
    }

    /// <summary>The quad as painted: the stored glyph height (auto) or the manual height around its vertical center.</summary>
    public static Quad Painted(Quad quad, HighlightHeightMode mode, double? manualHeightPt)
    {
        if (mode != HighlightHeightMode.Manual || manualHeightPt is not { } h) return quad;
        Point2 Mid(Point2 a, Point2 b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var left = Mid(quad.UpperLeft, quad.LowerLeft);
        var right = Mid(quad.UpperRight, quad.LowerRight);
        var side = new Point2(quad.UpperLeft.X - quad.LowerLeft.X, quad.UpperLeft.Y - quad.LowerLeft.Y);
        double length = Math.Sqrt(side.X * side.X + side.Y * side.Y);
        var unit = length > 1e-9 ? new Point2(side.X / length, side.Y / length) : Up(Normalize(new(right.X - left.X, right.Y - left.Y)));
        var up = new Point2(unit.X * h / 2, unit.Y * h / 2);
        return new(new(left.X + up.X, left.Y + up.Y), new(right.X + up.X, right.Y + up.Y), new(left.X - up.X, left.Y - up.Y), new(right.X - up.X, right.Y - up.Y));
    }

    public static IEnumerable<Quad> PaintedQuads(PdfTextHighlight highlight) =>
        highlight.Quads.Select(q => Painted(q, highlight.HeightMode, highlight.ManualHeightPt));

    /// <summary>
    /// The painted band of each line of an underline/strikeout (spec 24.8): <see cref="PdfTextMarkup.WidthPt"/> thick along
    /// the quad's direction, its lower side on the quad's lower edge (underline) or centered on the quad's middle (strikeout).
    /// </summary>
    public static IEnumerable<Quad> PaintedQuads(PdfTextMarkup markup) => markup.Quads.Select(q => MarkupBand(q, markup.MarkupKind, markup.WidthPt));

    public static Quad MarkupBand(Quad quad, TextMarkupKind kind, double width)
    {
        var side = new Point2(quad.UpperLeft.X - quad.LowerLeft.X, quad.UpperLeft.Y - quad.LowerLeft.Y);
        double length = Math.Sqrt(side.X * side.X + side.Y * side.Y);
        var up = length > 1e-9 ? new Point2(side.X / length, side.Y / length) : new Point2(0, -1);
        double from = kind == TextMarkupKind.Underline ? 0 : length / 2 - width / 2, to = from + width;
        Point2 At(Point2 p, double d) => new(p.X + up.X * d, p.Y + up.Y * d);
        return new(At(quad.LowerLeft, to), At(quad.LowerRight, to), At(quad.LowerLeft, from), At(quad.LowerRight, from));
    }

    /// <summary>Painted quads as closed rings in one winding (UL, UR, LR, LL) so a single nonzero fill paints their union once.</summary>
    public static Point2[] Ring(Quad q) => [q.UpperLeft, q.UpperRight, q.LowerRight, q.LowerLeft];

    public static Rect2 Bounds(IEnumerable<Quad> quads)
    {
        var points = quads.SelectMany(Ring).ToArray();
        if (points.Length == 0) return new(0, 0, 0, 0);
        double minX = points.Min(p => p.X), minY = points.Min(p => p.Y);
        return new(minX, minY, points.Max(p => p.X) - minX, points.Max(p => p.Y) - minY);
    }

    public static bool InQuad(Quad q, Point2 p)
    {
        var ring = Ring(q);
        bool positive = false, negative = false;
        for (int i = 0; i < 4; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % 4];
            double cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            if (cross > 0) positive = true; else if (cross < 0) negative = true;
        }
        return !(positive && negative);
    }

    public static double DistanceToQuad(Quad q, Point2 p)
    {
        if (InQuad(q, p)) return 0;
        var ring = Ring(q);
        return Enumerable.Range(0, 4).Min(i => StrokeGeometry.DistanceToSegment(p, ring[i], ring[(i + 1) % 4]));
    }

    private static Point2 Normalize(Point2 v)
    {
        double length = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        return length > 1e-12 ? new(v.X / length, v.Y / length) : new(1, 0);
    }
}
