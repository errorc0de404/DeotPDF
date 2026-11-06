using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Cli;

/// <summary>
/// Ink of one rendered page for <c>ink</c> and <c>space</c> (cli_plan 4.3): a pixel is ink when its color differs from
/// the paper color — the most common color of the image border — by more than a threshold. Works the same for text
/// PDFs and scans; no OCR. Page coordinates are canonical points.
/// </summary>
public sealed class InkMap
{
    private readonly bool[] ink;
    private int[]? prefix;

    private InkMap(int width, int height, bool[] ink, Affine pageToPixel, RgbColor paper, double pixelsPerPoint, PageSetup setup)
    {
        Width = width;
        Height = height;
        this.ink = ink;
        PageToPixel = pageToPixel;
        PixelToPage = pageToPixel.Invert();
        Paper = paper;
        PixelsPerPoint = pixelsPerPoint;
        Setup = setup;
    }

    public int Width { get; }
    public int Height { get; }
    public Affine PageToPixel { get; }
    public Affine PixelToPage { get; }
    public RgbColor Paper { get; }
    public double PixelsPerPoint { get; }
    public PageSetup Setup { get; }

    public bool IsInk(int x, int y) => ink[y * Width + x];

    /// <param name="annotations">Which annotations are drawn before measuring (none: the page content alone).</param>
    /// <param name="threshold">Sum of the absolute R, G, B differences from the paper color above which a pixel is ink.</param>
    public static InkMap Build(CliDocument doc, Page page, double dpi, Func<Annotation, bool> annotations, int threshold)
    {
        double ppp = dpi / Units.PointsPerInch;
        var rendered = PageRaster.Render(doc, page, ppp, null, annotations, out _);
        int w = rendered.Width, h = rendered.Height;
        var bgra = rendered.Bgra;
        int stride = rendered.Stride;
        // Paper: the most common 4-bit-per-channel color of the outer two pixel rings, averaged within that bucket.
        var buckets = new Dictionary<int, (long Count, long R, long G, long B)>();
        void Sample(int x, int y)
        {
            int k = y * stride + x * 4;
            byte b = bgra[k], g = bgra[k + 1], r = bgra[k + 2];
            int key = r >> 4 << 8 | g >> 4 << 4 | b >> 4;
            var e = buckets.GetValueOrDefault(key);
            buckets[key] = (e.Count + 1, e.R + r, e.G + g, e.B + b);
        }
        for (int x = 0; x < w; x++) for (int y = 0; y < Math.Min(2, h); y++) { Sample(x, y); Sample(x, h - 1 - y); }
        for (int y = 0; y < h; y++) for (int x = 0; x < Math.Min(2, w); x++) { Sample(x, y); Sample(w - 1 - x, y); }
        var top = buckets.Values.MaxBy(v => v.Count);
        var paper = new RgbColor((byte)(top.R / top.Count), (byte)(top.G / top.Count), (byte)(top.B / top.Count));
        var mask = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int k = y * stride + x * 4;
                mask[y * w + x] = Math.Abs(bgra[k + 2] - paper.R) + Math.Abs(bgra[k + 1] - paper.G) + Math.Abs(bgra[k] - paper.B) > threshold;
            }
        return new InkMap(w, h, mask, PageRaster.PageToFullPixel(page.Setup, ppp), paper, ppp, page.Setup);
    }

    /// <summary>
    /// Ink fraction (0–1) of each <paramref name="cell"/>-point square of the canonical page, rows top to bottom. A
    /// pixel counts for the cell its center falls in.
    /// </summary>
    public double[,] Grid(double cell)
    {
        int columns = (int)Math.Ceiling(Setup.WidthPt / cell - 1e-9), rows = (int)Math.Ceiling(Setup.HeightPt / cell - 1e-9);
        var inked = new int[rows, columns];
        var total = new int[rows, columns];
        var m = PixelToPage;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                var p = m.Transform(new(x + 0.5, y + 0.5));
                int c = (int)Math.Floor(p.X / cell), r = (int)Math.Floor(p.Y / cell);
                if (c < 0 || r < 0 || c >= columns || r >= rows) continue;
                total[r, c]++;
                if (ink[y * Width + x]) inked[r, c]++;
            }
        var result = new double[rows, columns];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++) result[r, c] = total[r, c] == 0 ? 0 : (double)inked[r, c] / total[r, c];
        return result;
    }

    /// <summary>The pixel rectangle [x0, y0, x1, y1) covering a canonical rectangle, clipped to the image.</summary>
    public (int X0, int Y0, int X1, int Y1) PixelBox(Rect2 rect)
    {
        var corners = AnnotationGeometry.Corners(rect, PageToPixel);
        int x0 = Math.Clamp((int)Math.Floor(corners.Min(c => c.X) + 1e-6), 0, Width), y0 = Math.Clamp((int)Math.Floor(corners.Min(c => c.Y) + 1e-6), 0, Height);
        int x1 = Math.Clamp((int)Math.Ceiling(corners.Max(c => c.X) - 1e-6), 0, Width), y1 = Math.Clamp((int)Math.Ceiling(corners.Max(c => c.Y) - 1e-6), 0, Height);
        return (x0, y0, Math.Max(x0, x1), Math.Max(y0, y1));
    }

    /// <summary>Ink fraction of the pixels a canonical rectangle covers (0 when it covers none).</summary>
    public double RatioIn(Rect2 rect)
    {
        var (x0, y0, x1, y1) = PixelBox(rect);
        long area = (long)(x1 - x0) * (y1 - y0);
        return area == 0 ? 0 : (double)Count(x0, y0, x1, y1) / area;
    }

    /// <summary>Ink pixels in [x0, x1) × [y0, y1), from a summed-area table.</summary>
    public int Count(int x0, int y0, int x1, int y1)
    {
        if (prefix is null)
        {
            prefix = new int[(Width + 1) * (Height + 1)];
            for (int y = 0; y < Height; y++)
            {
                int row = 0;
                for (int x = 0; x < Width; x++)
                {
                    row += ink[y * Width + x] ? 1 : 0;
                    prefix[(y + 1) * (Width + 1) + x + 1] = prefix[y * (Width + 1) + x + 1] + row;
                }
            }
        }
        int S(int x, int y) => prefix[y * (Width + 1) + x];
        return S(x1, y1) - S(x0, y1) - S(x1, y0) + S(x0, y0);
    }

    /// <summary>
    /// Connected pieces of ink after joining pixels closer than <paramref name="mergePt"/> (so the letters of a paragraph
    /// or the strokes of a figure become one piece). Pieces whose larger side is below <paramref name="minPt"/> (scan
    /// specks) are dropped. Each piece: its canonical bounds of the ink itself and the ink fraction inside them.
    /// </summary>
    public List<(Rect2 Rect, double Ratio)> Blocks(double mergePt, double minPt)
    {
        int radius = (int)Math.Round(mergePt * PixelsPerPoint / 2);
        var grown = Dilate(ink, Width, Height, radius);
        var label = new int[Width * Height];
        var blocks = new List<(Rect2, double)>();
        var stack = new Stack<int>();
        int next = 0;
        for (int start = 0; start < label.Length; start++)
        {
            if (!grown[start] || label[start] != 0) continue;
            next++;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            stack.Push(start);
            label[start] = next;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % Width, y = i / Width;
                if (ink[i]) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
                if (x > 0) Visit(i - 1);
                if (x < Width - 1) Visit(i + 1);
                if (y > 0) Visit(i - Width);
                if (y < Height - 1) Visit(i + Width);
            }
            if (x1 < 0) continue;
            var corners = new[] { new Point2(x0, y0), new Point2(x1 + 1, y1 + 1) }.Select(PixelToPage.Transform).ToArray();
            var rect = new Rect2(corners.Min(c => c.X), corners.Min(c => c.Y), Math.Abs(corners[1].X - corners[0].X), Math.Abs(corners[1].Y - corners[0].Y));
            if (Math.Max(rect.Width, rect.Height) < minPt) continue;
            blocks.Add((rect, (double)Count(x0, y0, x1 + 1, y1 + 1) / ((long)(x1 + 1 - x0) * (y1 + 1 - y0))));
        }
        return blocks;

        void Visit(int i)
        {
            if (!grown[i] || label[i] != 0) return;
            label[i] = next;
            stack.Push(i);
        }
    }

    /// <summary>Square dilation by <paramref name="radius"/> pixels (separable running maximum).</summary>
    private static bool[] Dilate(bool[] mask, int width, int height, int radius)
    {
        if (radius <= 0) return mask;
        var rows = new bool[mask.Length];
        for (int y = 0; y < height; y++)
        {
            int last = int.MinValue / 2;
            for (int x = 0; x < width; x++) { if (mask[y * width + x]) last = x; rows[y * width + x] = x - last <= radius; }
            last = int.MaxValue / 2;
            for (int x = width - 1; x >= 0; x--) { if (mask[y * width + x]) last = x; if (last - x <= radius) rows[y * width + x] = true; }
        }
        var result = new bool[mask.Length];
        for (int x = 0; x < width; x++)
        {
            int last = int.MinValue / 2;
            for (int y = 0; y < height; y++) { if (rows[y * width + x]) last = y; result[y * width + x] = y - last <= radius; }
            last = int.MaxValue / 2;
            for (int y = height - 1; y >= 0; y--) { if (rows[y * width + x]) last = y; if (last - y <= radius) result[y * width + x] = true; }
        }
        return result;
    }
}
