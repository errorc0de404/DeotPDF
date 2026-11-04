using System.Collections.Immutable;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using SkiaSharp;

namespace PdfNotes.Text;

/// <summary>Glyphs of one face, size, style and color, positioned at their pens on the baseline (local points).</summary>
internal sealed record SceneGlyphs(SkiaFontFace Face, double Size, bool FakeBold, bool FakeItalic, RgbColor Color, ushort[] Glyphs, SKPoint[] Positions);

/// <summary>An underline or strikethrough bar.</summary>
internal sealed record SceneBar(Rect2 Rect, RgbColor Color);

/// <summary>
/// What a laid-out box draws, in painting order and local points at layout scale 1 (roadmap P2 S2): fill, border,
/// highlights behind the glyphs, then per line its glyphs and decoration bars. The PDF outlines
/// (<see cref="SkiaTextLayout.Paint"/>) and the screen (<see cref="SkiaTextRenderer"/>) both draw from it, so they
/// cannot drift apart.
/// </summary>
internal sealed class SkiaTextScene
{
    /// <summary>Fake italic slant: x moves by a quarter of the height above the baseline (Skia's own value).</summary>
    public const float FakeItalicSkew = -0.25f;

    public required double Width { get; init; }
    public required double Height { get; init; }
    public required TextBoxStyle Style { get; init; }
    /// <summary>Border width at layout scale 1 (the WPF engine's value: one point).</summary>
    public double BorderWidth => 1;
    public required ImmutableArray<(TextHighlightRange Highlight, Rect2[] Lines)> Highlights { get; init; }
    /// <summary><see cref="SceneGlyphs"/> and <see cref="SceneBar"/> items in drawing order.</summary>
    public required ImmutableArray<object> Content { get; init; }

    public static SkiaTextScene Of(SkiaTextLayout unscaled)
    {
        var box = unscaled.Box;
        var content = ImmutableArray.CreateBuilder<object>();
        var placed = unscaled.Placed;
        int i = 0;
        while (i < placed.Length)
        {
            int line = placed[i].Line, end = i;
            while (end < placed.Length && placed[end].Line == line) end++;
            AddGlyphs(placed, i, end, content);
            AddBars(placed, i, end, unscaled.SkiaLines[line], content);
            i = end;
        }
        return new SkiaTextScene
        {
            Width = box.WidthPt, Height = unscaled.Height, Style = box.BoxStyle,
            Highlights = [.. TextLayout.HighlightLines(box, unscaled.Characters)],
            Content = content.ToImmutable(),
        };
    }

    /// <summary>Consecutive glyphs of the same face, size, fake styles and color become one run.</summary>
    private static void AddGlyphs(ImmutableArray<SkiaPlacedItem> placed, int start, int end, ImmutableArray<object>.Builder content)
    {
        var glyphs = new List<ushort>();
        var positions = new List<SKPoint>();
        ShapedItem? key = null;
        void Flush()
        {
            if (key is not null && glyphs.Count > 0)
                content.Add(new SceneGlyphs(key.Face, key.Size, key.FakeBold, key.FakeItalic, key.Run.Color, [.. glyphs], [.. positions]));
            glyphs.Clear(); positions.Clear();
        }
        for (int k = start; k < end; k++)
        {
            var p = placed[k];
            if (p.Item.Glyphs.Length == 0) continue;
            if (key is null || key.Face != p.Item.Face || key.Size != p.Item.Size || key.FakeBold != p.Item.FakeBold ||
                key.FakeItalic != p.Item.FakeItalic || key.Run.Color != p.Item.Run.Color) { Flush(); key = p.Item; }
            foreach (var g in p.Item.Glyphs)
            {
                glyphs.Add(g.Id);
                positions.Add(new((float)(p.X + g.X), (float)(p.Baseline + g.Y)));
            }
        }
        Flush();
    }

    /// <summary>
    /// Underline and strikethrough bars per run on the line, from the run's own font (post and OS/2 tables via Skia):
    /// over its characters except the white space hanging past the line end.
    /// </summary>
    private static void AddBars(ImmutableArray<SkiaPlacedItem> placed, int start, int end, SkiaTextLine line, ImmutableArray<object>.Builder content)
    {
        double shownEnd = line.Left + line.Width;
        int k = start;
        while (k < end)
        {
            var run = placed[k].Item.Run;
            int stop = k;
            double x0 = placed[k].X, x1 = x0;
            while (stop < end && ReferenceEquals(placed[stop].Item.Run, run))
            {
                var p = placed[stop];
                if (!(p.Item.Hangs && p.X >= shownEnd - 1e-9)) x1 = Math.Max(x1, p.X + p.Advance);
                stop++;
            }
            if ((run.Underline || run.Strikethrough) && x1 > x0)
            {
                var (underline, underThickness, strike, strikeThickness) = Decorations(run);
                double baseline = placed[k].Baseline;
                if (run.Underline) content.Add(new SceneBar(new(x0, baseline + underline, x1 - x0, underThickness), run.Color));
                if (run.Strikethrough) content.Add(new SceneBar(new(x0, baseline + strike - strikeThickness, x1 - x0, strikeThickness), run.Color));
            }
            k = stop;
        }
    }

    /// <summary>Underline top and strikethrough bottom relative to the baseline (down positive) and their thicknesses.</summary>
    private static (double Underline, double UnderThickness, double Strike, double StrikeThickness) Decorations(TextRun run)
    {
        var face = SkiaFonts.Face(run.FontFamily, run.Bold, run.Italic);
        using var font = new SKFont(face.Typeface, (float)run.SizePt);
        var m = font.Metrics;
        double size = run.SizePt;
        double underThickness = m.UnderlineThickness is float ut && ut > 0 ? ut : size / 14;
        double underline = m.UnderlinePosition is float up && up > 0 ? up : size / 10;
        double strikeThickness = m.StrikeoutThickness is float st && st > 0 ? st : underThickness;
        double strike = m.StrikeoutPosition is float sp && sp < 0 ? sp : -face.Ascent * size * .3;
        return (underline, underThickness, strike, strikeThickness);
    }

    /// <summary>A font for drawing or outlining these glyphs: unhinted, subpixel, fake bold and slant as asked.</summary>
    public static SKFont Font(SceneGlyphs run) => new(run.Face.DrawingTypeface, (float)run.Size)
    {
        Embolden = run.FakeBold, SkewX = run.FakeItalic ? FakeItalicSkew : 0, Subpixel = true, LinearMetrics = true,
        Hinting = SKFontHinting.None, Edging = SKFontEdging.Antialias, BaselineSnap = false, EmbeddedBitmaps = false,
    };
}
