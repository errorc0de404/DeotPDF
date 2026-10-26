using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PdfNotes.Core.Geometry;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>layout &lt;pdf&gt; -p &lt;page&gt;</c> (cli_plan 4.3): the text layer's lines (numbered as in <c>text</c>) grouped into
/// blocks — paragraphs and list items — with their bounds, line and character ranges, list marker and indent level. A
/// line joins the block above it when it runs the same way, has a similar size (within 20%), starts within a line height
/// below it, does not start with a list marker and starts where the line above starts its text (after a list marker),
/// or — for the second line of a block — further left (a first-line indent). Indent levels rank the distinct left edges
/// of the page's blocks (6 pt apart). Geometry only: what a block means is the caller's call.
/// </summary>
public static partial class LayoutCommand
{
    private const double IndentTolerancePt = 6;

    [GeneratedRegex(@"^\s*([•·▪▫◦○●■□◆◇▶►‣⁃※\-–—*]|\(?\d{1,3}[.)]|\(?[A-Za-z][.)]|\(?[가-힣][.)]|[①-⑳㉠-㉻])(?=\s|$|[^\d.])")]
    private static partial Regex Marker();

    /// <summary>A visual line: one or more consecutive text lines (runs split by a wide gap, such as a list marker) on one baseline.</summary>
    private sealed record Line(int Index, int LastIndex, int Start, int End, string Text, Quad[] Quads, Point2 U, Point2 V, double Height,
        double Left, double Right, double Top, double Bottom)
    {
        /// <summary>Where the text starts after a list marker (the left edge when there is none).</summary>
        public double TextLeft { get; init; } = Left;
    }

    public static JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, ["--page"], []);
        using var doc = CliDocument.Open(args.InputPath());
        var page = doc.ResolvePage(args.Required("--page"));
        var glyphs = doc.Glyphs(page);
        var lines = new List<Line>();
        var runs = TextSelection.LineRuns(glyphs, 0, glyphs.Length);
        for (int i = 0; i < runs.Length; i++)
        {
            var (start, end, q) = runs[i];
            string text = TextSelection.TextOf(glyphs, start, end);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var u = Unit(Sub(q.UpperRight, q.UpperLeft));
            var v = Unit(Sub(q.LowerLeft, q.UpperLeft));
            var ring = TextSelection.Ring(q);
            var line = new Line(i, i, start, end, text.Trim(), [q], u, v, Length(Sub(q.LowerLeft, q.UpperLeft)),
                ring.Min(p => Dot(p, u)), ring.Max(p => Dot(p, u)), ring.Min(p => Dot(p, v)), ring.Max(p => Dot(p, v)));
            if (lines.Count > 0 && SameBaseline(lines[^1], line))
            {
                var a = lines[^1];
                lines[^1] = a with { LastIndex = i, End = end, Text = a.Text + " " + line.Text, Quads = [.. a.Quads, q], Height = Math.Max(a.Height, line.Height),
                    Right = Math.Max(a.Right, line.Right), Top = Math.Min(a.Top, line.Top), Bottom = Math.Max(a.Bottom, line.Bottom) };
            }
            else lines.Add(line);
        }
        for (int i = 0; i < lines.Count; i++) lines[i] = lines[i] with { TextLeft = TextLeft(glyphs, lines[i]) };

        var blocks = new List<List<Line>>();
        foreach (var line in lines)
        {
            var block = blocks.Count > 0 ? blocks[^1] : null;
            if (block is not null && Joins(block, line)) block.Add(line);
            else blocks.Add([line]);
        }

        // Indent levels per reading direction: rank of the block's left edge among the page's distinct left edges.
        var levels = new Dictionary<List<Line>, int>();
        foreach (var group in blocks.GroupBy(b => (Math.Round(b[0].U.X, 2), Math.Round(b[0].U.Y, 2))))
        {
            var edges = new List<double>();
            foreach (double left in group.Select(b => b.Min(l => l.Left)).Order())
                if (edges.Count == 0 || left - edges[^1] > IndentTolerancePt) edges.Add(left);
            foreach (var b in group)
            {
                double left = b.Min(l => l.Left);
                levels[b] = edges.FindLastIndex(e => e <= left + IndentTolerancePt);
            }
        }

        var result = CliJson.Result("layout", doc.SourceHash);
        result["page"] = doc.NumberOf(page);
        result["pageId"] = page.Id.ToString();
        result["blocks"] = CliJson.Array(blocks.Select((b, i) =>
        {
            var bounds = TextSelection.Bounds(b.SelectMany(l => l.Quads));
            var marker = Marker().Match(b[0].Text);
            return (JsonNode?)new JsonObject
            {
                ["index"] = i,
                ["rect"] = CliJson.Rect(bounds),
                ["lines"] = new JsonObject { ["start"] = b[0].Index, ["end"] = b[^1].LastIndex + 1 },
                ["chars"] = new JsonObject { ["start"] = b[0].Start, ["end"] = b[^1].End },
                ["text"] = string.Join("\n", b.Select(l => l.Text)),
                ["marker"] = marker.Success ? marker.Groups[1].Value : null,
                ["indent"] = levels[b],
                ["lineHeight"] = CliJson.Num(b.Average(l => l.Height)),
            };
        }));
        return result;
    }

    /// <summary>The next run continues the same visual line: same direction, overlapping height, further along.</summary>
    private static bool SameBaseline(Line a, Line b)
    {
        if (Dot(a.U, b.U) < 0.99) return false;
        double overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return overlap >= 0.5 * Math.Min(a.Height, b.Height) && b.Left >= a.Right - 0.5 * Math.Min(a.Height, b.Height);
    }

    private static bool Joins(List<Line> block, Line line)
    {
        var last = block[^1];
        if (Dot(last.U, line.U) < 0.99 || Marker().IsMatch(line.Text)) return false;
        double ratio = Math.Max(last.Height, line.Height) / Math.Max(1e-6, Math.Min(last.Height, line.Height));
        if (ratio > 1.2) return false;
        double height = (last.Height + line.Height) / 2;
        double gap = line.Top - last.Bottom;
        if (gap < -0.3 * height || gap > 0.9 * height) return false;
        double tolerance = 0.6 * height;
        if (Math.Abs(line.Left - last.TextLeft) <= tolerance) return true;
        // A paragraph whose first line is indented: its second line starts further left, by at most a few characters.
        return block.Count == 1 && line.Left < last.TextLeft && last.TextLeft - line.Left <= 4 * height && line.Right > last.Left;
    }

    /// <summary>The left edge of the first glyph after the line's list marker, or the line's left edge.</summary>
    private static double TextLeft(IReadOnlyList<TextGlyph> glyphs, Line line)
    {
        var marker = Marker().Match(line.Text);
        if (!marker.Success) return line.Left;
        int remaining = marker.Groups[1].Value.Length;
        for (int k = line.Start; k < line.End; k++)
        {
            var g = glyphs[k];
            if (g.IsEmpty || string.IsNullOrWhiteSpace(g.Text)) continue;
            if (remaining > 0) { remaining -= g.Text.Length; continue; }
            return TextSelection.Ring(g.Box).Min(p => Dot(p, line.U));
        }
        return line.Left;
    }

    private static Point2 Unit(Point2 v)
    {
        double length = Length(v);
        return length > 1e-9 ? new(v.X / length, v.Y / length) : new(1, 0);
    }

    private static Point2 Sub(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
    private static double Length(Point2 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);
    private static double Dot(Point2 a, Point2 b) => a.X * b.X + a.Y * b.Y;
}
