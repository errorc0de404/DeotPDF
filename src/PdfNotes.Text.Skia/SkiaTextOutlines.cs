using System.Collections.Immutable;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using SkiaSharp;

namespace PdfNotes.Text;

/// <summary>
/// The PDF drawing of a Skia layout as <see cref="TextPaint"/> (roadmap P2 S2): the scene's shapes as flattened
/// outlines (0.02 pt, D01), glyphs from <see cref="SKFont.GetGlyphPath"/> with fake bold and slant applied by Skia.
/// The tree has the WPF engine's shape: [scale group] → clip group → fill, border, highlights, glyphs and bars.
/// </summary>
internal static class SkiaTextOutlines
{
    /// <summary>Largest distance between a curve and its flattened polyline, in local points (D01).</summary>
    public const double Tolerance = .02;

    public static TextPaint Build(SkiaTextLayout layout)
    {
        var scene = SkiaTextScene.Of(layout.Unscaled);
        var children = ImmutableArray.CreateBuilder<TextPaint>();
        var style = scene.Style;
        if (style.BackgroundOpacity > 0)
            children.Add(new TextPaintShape([Rectangle(new(0, 0, scene.Width, scene.Height))], false, Color(style.BackgroundColor, style.BackgroundOpacity), null, 0));
        if (style.BorderEnabled)
        {
            double b = scene.BorderWidth;
            children.Add(new TextPaintShape([Rectangle(new(b / 2, b / 2, Math.Max(0, scene.Width - b), Math.Max(0, scene.Height - b)))], false,
                null, Color(style.BorderColor, style.BorderOpacity), b));
        }
        foreach (var (h, lines) in scene.Highlights)
            children.Add(new TextPaintShape([.. lines.Select(Rectangle)], false, Color(h.Color, h.Opacity), null, 0));
        foreach (var item in scene.Content)
            children.Add(item switch
            {
                SceneGlyphs run => Glyphs(run),
                SceneBar bar => new TextPaintShape([Rectangle(bar.Rect)], false, Color(bar.Color, 1), null, 0),
                _ => throw new InvalidOperationException(),
            });
        TextPaint root = new TextPaintGroup(Affine.Identity, [Rectangle(new(0, 0, scene.Width, scene.Height))], 1, children.ToImmutable());
        return layout.Scale == 1 ? root : new TextPaintGroup(Affine.Scale(layout.Scale), null, 1, [root]);
    }

    private static TextColor Color(RgbColor c, double alpha) => new(c.R, c.G, c.B, alpha);

    private static TextFigure Rectangle(Rect2 r) =>
        new([new(r.X, r.Y), new(r.X + r.Width, r.Y), new(r.X + r.Width, r.Y + r.Height), new(r.X, r.Y + r.Height)], true);

    private static TextPaintShape Glyphs(SceneGlyphs run)
    {
        using var font = SkiaTextScene.Font(run);
        var figures = ImmutableArray.CreateBuilder<TextFigure>();
        for (int i = 0; i < run.Glyphs.Length; i++)
        {
            using var path = font.GetGlyphPath(run.Glyphs[i]);
            if (path is null || path.IsEmpty) continue;
            Flatten(path, run.Positions[i].X, run.Positions[i].Y, figures);
        }
        return new TextPaintShape(figures.ToImmutable(), EvenOdd: false, Color(run.Color, 1), null, 0);
    }

    /// <summary>Flattens a path moved by (<paramref name="dx"/>, <paramref name="dy"/>) into closed or open polylines.</summary>
    public static void Flatten(SKPath path, double dx, double dy, ImmutableArray<TextFigure>.Builder figures)
    {
        using var iterator = path.CreateRawIterator();
        var p = new SKPoint[4];
        List<Point2>? points = null;
        void End(bool closed)
        {
            if (points is { Count: > 1 }) figures.Add(new([.. points], closed));
            points = null;
        }
        Point2 At(SKPoint q) => new(q.X + dx, q.Y + dy);
        SKPathVerb verb;
        while ((verb = iterator.Next(p)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    End(false);
                    points = [At(p[0])];
                    break;
                case SKPathVerb.Line:
                    (points ??= [At(p[0])]).Add(At(p[1]));
                    break;
                case SKPathVerb.Quad:
                    points ??= [At(p[0])];
                    Curve(points, At(p[0]), At(p[1]), At(p[2]), null, 1);
                    break;
                case SKPathVerb.Conic:
                    points ??= [At(p[0])];
                    Curve(points, At(p[0]), At(p[1]), At(p[2]), null, iterator.ConicWeight());
                    break;
                case SKPathVerb.Cubic:
                    points ??= [At(p[0])];
                    Curve(points, At(p[0]), At(p[1]), At(p[2]), At(p[3]), 1);
                    break;
                case SKPathVerb.Close:
                    End(true);
                    break;
            }
        }
        End(false);
    }

    /// <summary>
    /// Adds a quadratic (rational with weight <paramref name="w"/>) or cubic curve after its start point, in as many
    /// equal steps as keep the chord within <see cref="Tolerance"/> (second-difference bound).
    /// </summary>
    private static void Curve(List<Point2> points, Point2 a, Point2 b, Point2 c, Point2? d, double w)
    {
        double dd;
        if (d is { } e)
        {
            double d1 = Length(a.X - 2 * b.X + c.X, a.Y - 2 * b.Y + c.Y), d2 = Length(b.X - 2 * c.X + e.X, b.Y - 2 * c.Y + e.Y);
            dd = .75 * Math.Max(d1, d2);
        }
        else dd = .25 * Length(a.X - 2 * b.X + c.X, a.Y - 2 * b.Y + c.Y) * Math.Max(1, w);
        int n = Math.Clamp((int)Math.Ceiling(Math.Sqrt(dd / Tolerance)), 1, 1000);
        for (int i = 1; i <= n; i++)
        {
            double t = i / (double)n, u = 1 - t;
            if (d is { } f)
                points.Add(new(u * u * u * a.X + 3 * u * u * t * b.X + 3 * u * t * t * c.X + t * t * t * f.X,
                    u * u * u * a.Y + 3 * u * u * t * b.Y + 3 * u * t * t * c.Y + t * t * t * f.Y));
            else
            {
                double k0 = u * u, k1 = 2 * u * t * w, k2 = t * t, den = k0 + k1 + k2;
                points.Add(new((k0 * a.X + k1 * b.X + k2 * c.X) / den, (k0 * a.Y + k1 * b.Y + k2 * c.Y) / den));
            }
        }
    }

    private static double Length(double x, double y) => Math.Sqrt(x * x + y * y);
}
