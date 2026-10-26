using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>space &lt;pdf&gt; -p &lt;page&gt; (--size w,h [--limit n] | --largest n) [--avoid rects] [--gap pt] [--within x,y,w,h]
/// [--margin pt] [--cell pt] [--ink-threshold f] [--include-annotations]</c> (cli_plan 4.3): empty rectangles of the page.
/// The page is cut into <c>--cell</c> squares; a square is taken when its ink fraction is above <c>--ink-threshold</c>,
/// when it is within <c>--gap</c> of such ink, or when it touches an <c>--avoid</c> rectangle (or, with
/// <c>--include-annotations</c>, an annotation) grown by <c>--gap</c>, or lies outside <c>--within</c> / the page margin.
/// Candidates use free squares only, so they never overlap what is to be avoided. They are listed, not chosen: with
/// <c>--size</c>, non-overlapping places of that size in reading order; with <c>--largest</c>, the largest free
/// rectangles, each taken out before the next is found. None: <c>NO_SPACE</c> (exit 2).
/// </summary>
public static class SpaceCommand
{
    public static JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, ["--page", "--size", "--largest", "--limit", "--avoid", "--gap", "--within", "--margin", "--cell",
            "--ink-threshold", "--dpi", "--threshold"], ["--include-annotations"]);
        double[]? size = args.Value("--size") is { } s ? CliArgs.ParseNumbers(s, 2, "--size") : null;
        if (size is not null && (size[0] <= 0 || size[1] <= 0)) throw CliException.Invalid("--size needs a positive width and height.");
        if (size is not null == args.Has("--largest")) throw CliException.Invalid("Give exactly one of --size w,h or --largest n.");
        if (size is null && args.Has("--limit")) throw CliException.Invalid("--limit goes with --size.");
        int largest = args.Integer("--largest", 1, 1, 100), limit = args.Integer("--limit", 10, 1, 1000);
        double gap = args.Number("--gap", 6, 0, 500), cell = args.Number("--cell", 4, 0.5, 100), margin = args.Number("--margin", 0, 0, 1000);
        double inkThreshold = args.Number("--ink-threshold", 0.01, 0, 1);
        var avoid = args.Value("--avoid") is { } a ? ParseRects(a, "--avoid") : [];
        var within = args.Value("--within") is { } w ? CliArgs.ParseRect(w, "--within") : (Rect2?)null;
        using var doc = CliDocument.Open(args.InputPath());
        var page = doc.ResolvePage(args.Required("--page"));
        bool annotations = args.Flag("--include-annotations");
        var map = InkMap.Build(doc, page, args.Number("--dpi", InkCommand.DefaultDpi, 18, 300), _ => false, args.Integer("--threshold", InkCommand.DefaultThreshold, 1, 765));
        var blocked = new List<Rect2>(avoid);
        if (annotations)
            foreach (var annotation in page.Annotations)
            {
                var corners = AnnotationGeometry.Corners(AnnotationGeometry.LocalBounds(annotation, TextLayout.Bounds), annotation.Transform);
                double x0 = corners.Min(c => c.X), y0 = corners.Min(c => c.Y);
                blocked.Add(new(x0, y0, corners.Max(c => c.X) - x0, corners.Max(c => c.Y) - y0));
            }

        double width = page.Setup.WidthPt, height = page.Setup.HeightPt;
        int columns = (int)Math.Floor(width / cell + 1e-9), rows = (int)Math.Floor(height / cell + 1e-9); // whole squares inside the page
        var taken = new bool[rows, columns];
        var ink = map.Grid(cell);
        int grow = (int)Math.Ceiling(gap / cell - 1e-9);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                if (ink[r, c] <= inkThreshold) continue;
                for (int rr = Math.Max(0, r - grow); rr <= Math.Min(rows - 1, r + grow); rr++)
                    for (int cc = Math.Max(0, c - grow); cc <= Math.Min(columns - 1, c + grow); cc++) taken[rr, cc] = true;
            }
        var area = within ?? new Rect2(0, 0, width, height);
        var inner = new Rect2(Math.Max(area.X, margin), Math.Max(area.Y, margin),
            Math.Min(area.X + area.Width, width - margin) - Math.Max(area.X, margin), Math.Min(area.Y + area.Height, height - margin) - Math.Max(area.Y, margin));
        var grown = blocked.Select(b => new Rect2(b.X - gap, b.Y - gap, b.Width + 2 * gap, b.Height + 2 * gap)).ToArray();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                var square = new Rect2(c * cell, r * cell, cell, cell);
                if (!Contains(inner, square) || grown.Any(g => Overlaps(g, square))) taken[r, c] = true;
            }

        var candidates = size is not null ? Placements(taken, cell, size[0], size[1], limit, page.Setup) : Largest(taken, cell, largest);
        if (candidates.Count == 0)
            throw new CliException("NO_SPACE", "No empty rectangle satisfies the request.", ExitCodes.Unsatisfiable,
                new JsonObject { ["page"] = doc.NumberOf(page), ["pageId"] = page.Id.ToString() });

        var result = CliJson.Result("space", doc.SourceHash);
        result["page"] = doc.NumberOf(page);
        result["pageId"] = page.Id.ToString();
        result["cell"] = CliJson.Num(cell);
        result["gap"] = CliJson.Num(gap);
        result["avoidCount"] = blocked.Count;
        result["candidates"] = CliJson.Array(candidates.Select(rect => (JsonNode?)new JsonObject
        {
            ["rect"] = CliJson.Rect(rect),
            ["area"] = CliJson.Num(rect.Width * rect.Height),
            ["inkRatio"] = CliJson.Num(map.RatioIn(rect)),
        }));
        return result;
    }

    /// <summary>Non-overlapping w×h places on free squares, found row by row, then listed in the page's reading order.</summary>
    private static List<Rect2> Placements(bool[,] taken, double cell, double w, double h, int limit, PageSetup setup)
    {
        int rows = taken.GetLength(0), columns = taken.GetLength(1);
        int cw = (int)Math.Ceiling(w / cell - 1e-9), ch = (int)Math.Ceiling(h / cell - 1e-9);
        var used = (bool[,])taken.Clone();
        var found = new List<Rect2>();
        for (int r = 0; r + ch <= rows && found.Count < limit; r++)
            for (int c = 0; c + cw <= columns && found.Count < limit; c++)
            {
                if (!Free(used, r, c, ch, cw)) continue;
                found.Add(new(c * cell, r * cell, w, h));
                for (int rr = r; rr < r + ch; rr++) for (int cc = c; cc < c + cw; cc++) used[rr, cc] = true;
            }
        var display = setup.Frame.CanonicalToDisplay;
        return [.. found.OrderBy(f => Math.Round(AnnotationGeometry.Corners(f, display).Min(p => p.Y), 3))
            .ThenBy(f => AnnotationGeometry.Corners(f, display).Min(p => p.X))];
    }

    private static bool Free(bool[,] taken, int r, int c, int ch, int cw)
    {
        for (int rr = r; rr < r + ch; rr++)
            for (int cc = c; cc < c + cw; cc++)
                if (taken[rr, cc]) return false;
        return true;
    }

    /// <summary>The largest free rectangles (maximal rectangle under each row's histogram), each removed before the next.</summary>
    private static List<Rect2> Largest(bool[,] taken, double cell, int count)
    {
        int rows = taken.GetLength(0), columns = taken.GetLength(1);
        var used = (bool[,])taken.Clone();
        var found = new List<Rect2>();
        for (int n = 0; n < count; n++)
        {
            var heights = new int[columns];
            (int Area, int R, int C, int H, int W) best = default;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < columns; c++) heights[c] = used[r, c] ? 0 : heights[c] + 1;
                var stack = new Stack<int>();
                for (int c = 0; c <= columns; c++)
                {
                    int hgt = c == columns ? 0 : heights[c];
                    while (stack.Count > 0 && heights[stack.Peek()] >= hgt)
                    {
                        int top = stack.Pop();
                        int left = stack.Count == 0 ? 0 : stack.Peek() + 1;
                        int area = heights[top] * (c - left);
                        if (area > best.Area) best = (area, r - heights[top] + 1, left, heights[top], c - left);
                    }
                    stack.Push(c);
                }
            }
            if (best.Area == 0) break;
            found.Add(new(best.C * cell, best.R * cell, best.W * cell, best.H * cell));
            for (int r = best.R; r < best.R + best.H; r++) for (int c = best.C; c < best.C + best.W; c++) used[r, c] = true;
        }
        return found;
    }

    private static bool Contains(Rect2 outer, Rect2 inner) =>
        inner.X >= outer.X - 1e-9 && inner.Y >= outer.Y - 1e-9 && inner.X + inner.Width <= outer.X + outer.Width + 1e-9 && inner.Y + inner.Height <= outer.Y + outer.Height + 1e-9;

    private static bool Overlaps(Rect2 a, Rect2 b) =>
        a.X < b.X + b.Width - 1e-9 && b.X < a.X + a.Width - 1e-9 && a.Y < b.Y + b.Height - 1e-9 && b.Y < a.Y + a.Height - 1e-9;

    /// <summary>
    /// Rectangles as JSON (<c>[[x,y,w,h], …]</c> or objects with <c>rect</c>), a JSON file holding that, or
    /// <c>x,y,w,h;x,y,w,h</c>.
    /// </summary>
    public static List<Rect2> ParseRects(string value, string option)
    {
        string text = value.Trim();
        if (!text.StartsWith('[') && File.Exists(text)) text = File.ReadAllText(text).Trim();
        if (!text.StartsWith('['))
            return [.. text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => CliArgs.ParseRect(p, option))];
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException e) { throw CliException.Invalid($"{option} is not valid JSON: {e.Message}"); }
        var result = new List<Rect2>();
        foreach (var item in root as JsonArray ?? throw CliException.Invalid($"{option} must be a list of rectangles."))
        {
            var rect = item is JsonObject o ? o["rect"] as JsonArray : item as JsonArray;
            if (rect is not { Count: 4 } || rect.Any(n => n is not JsonValue)) throw CliException.Invalid($"{option} items are [x,y,w,h].");
            var n = rect.Select(v => v!.GetValue<double>()).ToArray();
            if (n.Any(v => !double.IsFinite(v)) || n[2] < 0 || n[3] < 0) throw CliException.Invalid($"{option} has an invalid rectangle.");
            result.Add(new(n[0], n[1], n[2], n[3]));
        }
        return result;
    }
}
