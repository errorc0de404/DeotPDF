using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>render &lt;pdf&gt; -p &lt;page&gt; -o &lt;png&gt; [--region x,y,w,h] [--dpi n] [--annotations …] [--overlay …]</c> and
/// <c>render &lt;pdf&gt; --pages &lt;list&gt; --grid -o &lt;png&gt; [--dpi n] [--columns n]</c> (cli_plan 4.2). Every result carries
/// <c>pageToPixel</c> (canonical page points → pixels of the saved PNG, rotation, DPI, region offset and grid cell
/// included) and its inverse <c>pixelToPage</c>.
/// </summary>
public static class RenderCommand
{
    public const double DefaultDpi = 110;
    public const double DefaultGridDpi = 36;
    private const int GridMargin = 10, GridGap = 10, GridLabel = 20;

    public static JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, ["--page", "--pages", "--out", "--dpi", "--region", "--annotations", "--overlay", "--columns"], ["--grid"]);
        string input = args.InputPath();
        string output = Path.GetFullPath(args.Required("--out"));
        if (string.Equals(output, Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
            throw new CliException("SAME_AS_INPUT", "The output path is the input file.");
        var filter = AnnotationFilter(args.Value("--annotations") ?? "all");
        if (args.Flag("--grid"))
        {
            if (args.Has("--page") || args.Has("--region") || args.Has("--overlay")) throw CliException.Invalid("--grid takes --pages, not --page, --region or --overlay.");
            using var gridDoc = CliDocument.Open(input);
            return Grid(gridDoc, gridDoc.ResolvePages(args.Required("--pages")), args.Number("--dpi", DefaultGridDpi, 4, 300),
                args.Integer("--columns", 4, 1, 50), a => filter(gridDoc.Model.TagOf(a.Id)), args.Value("--annotations") ?? "all", output);
        }
        if (args.Has("--pages") || args.Has("--columns")) throw CliException.Invalid("--pages and --columns need --grid.");
        using var doc = CliDocument.Open(input);
        var page = doc.ResolvePage(args.Required("--page"));
        double dpi = args.Number("--dpi", DefaultDpi, 10, 600);
        double[]? region = args.Value("--region") is { } r ? CliArgs.ParseNumbers(r, 4, "--region") : null;
        var overlay = args.Value("--overlay") is { } o ? ReadOverlay(o) : [];
        return Single(doc, page, dpi, region, a => filter(doc.Model.TagOf(a.Id)), args.Value("--annotations") ?? "all", overlay, output);
    }

    /// <summary>
    /// <c>all | none | tagged | untagged | batch:&lt;id&gt; | group:&lt;key&gt;</c> over an annotation's tag (schema v7, null for
    /// the user's own writing and other apps' annotations).
    /// </summary>
    public static Func<AnnotationTag?, bool> AnnotationFilter(string spec) => spec switch
    {
        "all" => _ => true,
        "none" => _ => false,
        "tagged" => t => t is not null,
        "untagged" => t => t is null,
        _ when spec.StartsWith("batch:", StringComparison.Ordinal) && spec.Length > 6 => t => t?.BatchId == spec[6..],
        _ when spec.StartsWith("group:", StringComparison.Ordinal) && spec.Length > 6 => t => t?.GroupKey == spec[6..],
        _ => throw CliException.Invalid("--annotations takes all, none, tagged, untagged, batch:<id> or group:<key>."),
    };

    private static JsonObject Single(CliDocument doc, Page page, double dpi, double[]? region, Func<Annotation, bool> include, string annotations,
        IReadOnlyList<OverlayItem> overlay, string output)
    {
        double ppp = dpi / Units.PointsPerInch;
        var (fullWidth, fullHeight) = PageRaster.PixelSize(page.Setup, ppp);
        var toFull = PageRaster.PageToFullPixel(page.Setup, ppp);
        var area = new PixelRect(0, 0, fullWidth, fullHeight);
        if (region is not null)
        {
            if (region[2] <= 0 || region[3] <= 0) throw CliException.Invalid("--region needs a positive width and height.");
            var corners = AnnotationGeometry.Corners(new Rect2(region[0], region[1], region[2], region[3]), toFull);
            int x0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.X) + 1e-6)), y0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.Y) + 1e-6));
            int x1 = Math.Min(fullWidth, (int)Math.Ceiling(corners.Max(c => c.X) - 1e-6)), y1 = Math.Min(fullHeight, (int)Math.Ceiling(corners.Max(c => c.Y) - 1e-6));
            if (x1 <= x0 || y1 <= y0) throw new CliException("REGION_OUTSIDE_PAGE", "--region does not overlap the page.", ExitCodes.Unsatisfiable);
            area = new PixelRect(x0, y0, x1 - x0, y1 - y0);
        }
        CheckSize(area.Width, area.Height);
        var pageToPixel = toFull.Then(Affine.Translation(-area.X, -area.Y));
        var rendered = PageRaster.Render(doc, page, ppp, region is null ? null : area, include, out int drawn);
        var image = PageRaster.ToBitmap(rendered);
        if (overlay.Count > 0)
            image = PageRaster.Compose(area.Width, area.Height, dc =>
            {
                dc.DrawImage(image, new Rect(0, 0, area.Width, area.Height));
                foreach (var item in overlay)
                {
                    var fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(item.Opacity * 255), item.Color.R, item.Color.G, item.Color.B));
                    var pen = new Pen(new SolidColorBrush(Color.FromRgb(item.Color.R, item.Color.G, item.Color.B)), 1);
                    dc.DrawGeometry(fill, pen, PageRaster.Polygon(item.Ring.Select(pageToPixel.Transform).ToArray()));
                }
            });
        PageRaster.SavePng(image, output);

        var result = CliJson.Result("render", doc.SourceHash);
        result["output"] = output;
        result["width"] = area.Width;
        result["height"] = area.Height;
        result["dpi"] = CliJson.Num(dpi);
        result["page"] = doc.NumberOf(page);
        result["pageId"] = page.Id.ToString();
        result["region"] = region is null ? null : CliJson.Rect(region[0], region[1], region[2], region[3]);
        result["pixelRegion"] = new JsonArray(area.X, area.Y, area.Width, area.Height);
        result["pageToPixel"] = CliJson.Matrix(pageToPixel);
        result["pixelToPage"] = CliJson.Matrix(pageToPixel.Invert());
        result["annotations"] = annotations;
        result["annotationsDrawn"] = drawn;
        result["overlayCount"] = overlay.Count;
        return result;
    }

    private static JsonObject Grid(CliDocument doc, IReadOnlyList<Page> pages, double dpi, int columns, Func<Annotation, bool> include, string annotations, string output)
    {
        double ppp = dpi / Units.PointsPerInch;
        columns = Math.Min(columns, pages.Count);
        int rows = (pages.Count + columns - 1) / columns;
        var sizes = pages.Select(p => PageRaster.PixelSize(p.Setup, ppp)).ToArray();
        int cellWidth = sizes.Max(s => s.Width), cellHeight = sizes.Max(s => s.Height) + GridLabel;
        int width = GridMargin * 2 + columns * cellWidth + (columns - 1) * GridGap;
        int height = GridMargin * 2 + rows * cellHeight + (rows - 1) * GridGap;
        CheckSize(width, height);

        var cells = new JsonArray();
        var placed = new List<(System.Windows.Media.Imaging.BitmapSource Image, int X, int Y, string Label)>();
        int drawnTotal = 0;
        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            int x = GridMargin + i % columns * (cellWidth + GridGap), y = GridMargin + i / columns * (cellHeight + GridGap) + GridLabel;
            var rendered = PageRaster.Render(doc, page, ppp, null, include, out int drawn);
            drawnTotal += drawn;
            int number = doc.NumberOf(page);
            placed.Add((PageRaster.ToBitmap(rendered), x, y, number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var pageToPixel = PageRaster.PageToFullPixel(page.Setup, ppp).Then(Affine.Translation(x, y));
            cells.Add(new JsonObject
            {
                ["page"] = number,
                ["pageId"] = page.Id.ToString(),
                ["pixelRect"] = new JsonArray(x, y, sizes[i].Width, sizes[i].Height),
                ["pageToPixel"] = CliJson.Matrix(pageToPixel),
                ["pixelToPage"] = CliJson.Matrix(pageToPixel.Invert()),
            });
        }
        var image = PageRaster.Compose(width, height, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)), null, new Rect(0, 0, width, height));
            foreach (var (bitmap, x, y, label) in placed)
            {
                PageRaster.DrawLabel(dc, label, x, y - GridLabel + 2, 13);
                dc.DrawImage(bitmap, new Rect(x, y, bitmap.PixelWidth, bitmap.PixelHeight));
            }
        });
        PageRaster.SavePng(image, output);

        var result = CliJson.Result("render", doc.SourceHash);
        result["output"] = output;
        result["width"] = width;
        result["height"] = height;
        result["dpi"] = CliJson.Num(dpi);
        result["grid"] = new JsonObject { ["columns"] = columns, ["rows"] = rows };
        result["annotations"] = annotations;
        result["annotationsDrawn"] = drawnTotal;
        result["cells"] = cells;
        return result;
    }

    private static void CheckSize(int width, int height)
    {
        if ((long)width * height > PdfiumDocument.MaxBitmapPixels)
            throw new CliException("IMAGE_TOO_LARGE", $"The image would be {width}x{height} pixels; lower --dpi or use --region.", ExitCodes.InvalidInput,
                new JsonObject { ["width"] = width, ["height"] = height });
    }

    /// <summary>One translucent overlay shape (not saved to the PDF) as a closed ring in canonical points.</summary>
    public sealed record OverlayItem(Point2[] Ring, RgbColor Color, double Opacity);

    /// <summary>
    /// <c>--overlay</c>: inline JSON or a JSON file holding a list of <c>[x,y,w,h]</c> rectangles or objects
    /// <c>{ "rect": [x,y,w,h] | "quad": [[x,y]×4], "color": "#RRGGBB", "opacity": 0–1 }</c> (default red, 0.25).
    /// </summary>
    public static IReadOnlyList<OverlayItem> ReadOverlay(string value)
    {
        string text = value.TrimStart();
        if (!text.StartsWith('[') && !text.StartsWith('{'))
        {
            if (!File.Exists(value)) throw new CliException("FILE_NOT_FOUND", $"The overlay file {value} does not exist.");
            text = File.ReadAllText(value);
        }
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException e) { throw CliException.Invalid($"--overlay is not valid JSON: {e.Message}"); }
        if (root is not JsonArray items) throw CliException.Invalid("--overlay must be a JSON list.");
        var result = new List<OverlayItem>();
        foreach (var item in items)
        {
            var color = new RgbColor(255, 0, 0);
            double opacity = 0.25;
            Point2[] ring;
            if (item is JsonArray rect) ring = RectRing(rect);
            else if (item is JsonObject obj)
            {
                if (obj["rect"] is JsonArray r) ring = RectRing(r);
                else if (obj["quad"] is JsonArray q) ring = QuadRing(q);
                else throw CliException.Invalid("An --overlay item needs \"rect\" or \"quad\".");
                if (obj["color"] is { } c && !RgbColor.TryParse(c.GetValue<string>(), out color)) throw CliException.Invalid("An --overlay color must be #RRGGBB.");
                if (obj["opacity"] is { } a) opacity = Math.Clamp(a.GetValue<double>(), 0, 1);
            }
            else throw CliException.Invalid("An --overlay item must be a rectangle or an object.");
            result.Add(new OverlayItem(ring, color, opacity));
        }
        return result;
    }

    private static double[] Numbers(JsonArray array, int count)
    {
        if (array.Count != count || array.Any(n => n is not JsonValue)) throw CliException.Invalid($"Expected {count} numbers.");
        return array.Select(n => n!.GetValue<double>()).ToArray();
    }

    private static Point2[] RectRing(JsonArray rect)
    {
        var r = Numbers(rect, 4);
        return [new(r[0], r[1]), new(r[0] + r[2], r[1]), new(r[0] + r[2], r[1] + r[3]), new(r[0], r[1] + r[3])];
    }

    private static Point2[] QuadRing(JsonArray quad)
    {
        if (quad.Count != 4 || quad.Any(p => p is not JsonArray)) throw CliException.Invalid("A quad is four [x,y] points.");
        var p = quad.Select(n => Numbers((JsonArray)n!, 2)).Select(v => new Point2(v[0], v[1])).ToArray();
        return [p[0], p[1], p[3], p[2]]; // UL, UR, LR, LL
    }
}
