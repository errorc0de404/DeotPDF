using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;
using SkiaSharp;

namespace PdfNotes.Tests.TextEngine;

/// <summary>
/// Roadmap P2 S2: the Skia engine's PDF outlines (<see cref="TextLayout.Layout.Paint"/>), its screen renderer, fake bold
/// and italic, decorations and font reports. The engine is called directly; the registered engine does not matter.
/// </summary>
public class SkiaPaintTests
{
    private static TextRun Run(int start, int length, string font = "Malgun Gothic", double size = 12) =>
        new(start, length, font, size, RgbColor.Black, false, false, false, false);

    private static TextBoxAnnotation Box(string text, double width, params TextRun[] runs) =>
        new(Guid.NewGuid(), false, Affine.Identity, text, runs.Length > 0 ? [.. runs] : [Run(0, text.Length)], TextAlignment.Left, width, TextBoxStyle.Default, []);

    /// <summary>The PDF test sample: Korean, English and emoji, a bold italic underlined struck red run, fill and border.</summary>
    private static TextBoxAnnotation Styled() => TextEditing.Format(Box("한글 English 😀\n밑줄 취소선", 170), 3, 7,
        r => r with { Color = new(190, 30, 60), SizePt = 22, Bold = true, Italic = true, Underline = true, Strikethrough = true })
        with { BoxStyle = new(new(240, 200, 100), .35, true, new(20, 70, 170), .7),
            Highlights = [new(0, 2, HighlightHeightMode.Auto, null, new(255, 235, 59), .6), new(13, 4, HighlightHeightMode.Manual, 8, new(129, 199, 132), .5)] };

    // ----- rasterizing -----

    private static SKBitmap Canvas(TextBoxAnnotation box, double zoom, Action<SKCanvas> draw)
    {
        var layout = SkiaTextEngine.Layout(box);
        var bitmap = new SKBitmap((int)Math.Ceiling(box.WidthPt * zoom) + 2, (int)Math.Ceiling(layout.Height * zoom) + 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Scale((float)zoom);
        draw(canvas);
        return bitmap;
    }

    private static SKBitmap Screen(TextBoxAnnotation box, double zoom) => Canvas(box, zoom, c => SkiaTextRenderer.Draw(c, SkiaTextEngine.Layout(box)));

    /// <summary>The outline tree drawn as plain filled and stroked paths, the way a PDF viewer draws the appearance.</summary>
    private static SKBitmap Outlines(TextBoxAnnotation box, double zoom) => Canvas(box, zoom, c => Draw(c, SkiaTextEngine.Layout(box).Paint, 1));

    private static void Draw(SKCanvas canvas, TextPaint paint, double opacity)
    {
        if (paint is TextPaintGroup group)
        {
            canvas.Save();
            var t = group.Transform;
            canvas.Concat(new SKMatrix((float)t.M11, (float)t.M21, (float)t.OffsetX, (float)t.M12, (float)t.M22, (float)t.OffsetY, 0, 0, 1));
            if (group.Clip is { } clip) canvas.ClipPath(Path(clip, false), antialias: true);
            foreach (var child in group.Children) Draw(canvas, child, opacity * group.Opacity);
            canvas.Restore();
            return;
        }
        var shape = (TextPaintShape)paint;
        using var path = Path(shape.Figures, shape.EvenOdd);
        if (shape.Fill is { } f)
        {
            using var fill = new SKPaint { IsAntialias = true, Color = new(f.R, f.G, f.B, (byte)Math.Round(f.Alpha * opacity * 255)) };
            canvas.DrawPath(path, fill);
        }
        if (shape.Stroke is { } s)
        {
            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)shape.StrokeWidth,
                Color = new(s.R, s.G, s.B, (byte)Math.Round(s.Alpha * opacity * 255)) };
            canvas.DrawPath(path, stroke);
        }
    }

    private static SKPath Path(IEnumerable<TextFigure> figures, bool evenOdd)
    {
        var path = new SKPath { FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
        foreach (var f in figures)
        {
            path.MoveTo((float)f.Points[0].X, (float)f.Points[0].Y);
            foreach (var p in f.Points.Skip(1)) path.LineTo((float)p.X, (float)p.Y);
            if (f.Closed) path.Close();
        }
        return path;
    }

    /// <summary>Mean RGB difference over pixels that are painted in either image, and how many there are.</summary>
    private static (double Mean, int Painted) Difference(SKBitmap a, SKBitmap b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        long error = 0; int painted = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
                if (p.Red > 250 && p.Green > 250 && p.Blue > 250 && q.Red > 250 && q.Green > 250 && q.Blue > 250) continue;
                painted++;
                error += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
            }
        return (painted == 0 ? 0 : error / (3.0 * painted), painted);
    }

    /// <summary>
    /// Pixels clearly inked in one image (darker than 64) and clearly blank in the other (lighter than 192), over the
    /// pixels inked in either. Glyph rasterizers differ at the anti-aliased edges (on Windows Skia's text goes through
    /// DirectWrite), which this ignores; a shape in another place or slant does not get through.
    /// </summary>
    private static double Mismatch(SKBitmap a, SKBitmap b)
    {
        int either = 0, clear = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                byte p = a.GetPixel(x, y).Green, q = b.GetPixel(x, y).Green;
                if (p < 128 || q < 128) either++;
                if (p < 64 && q > 192 || q < 64 && p > 192) clear++;
            }
        return either == 0 ? 0 : clear / (double)either;
    }

    /// <summary>Ink (darkness) of a bitmap and the horizontal ink centroid of its top and bottom halves.</summary>
    private static (double Ink, double TopX, double BottomX) Ink(SKBitmap bitmap)
    {
        double ink = 0, topInk = 0, topX = 0, bottomInk = 0, bottomX = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                double v = 255 - bitmap.GetPixel(x, y).Green;
                ink += v;
                if (y < bitmap.Height / 2) { topInk += v; topX += v * x; } else { bottomInk += v; bottomX += v * x; }
            }
        return (ink, topX / Math.Max(1, topInk), bottomX / Math.Max(1, bottomInk));
    }

    // ----- tests -----

    [Fact]
    public void TheOutlinesDrawLikeTheScreen()
    {
        var boxes = new[]
        {
            Styled(),
            Styled() with { LayoutScale = .73, WidthPt = 170 * .73, Runs = [.. Styled().Runs.Select(r => r with { SizePt = r.SizePt * .73 })] },
            Box("굵게 기울임 Gulim", 200, Run(0, 3, "Gulim", 18) with { Bold = true }, Run(3, 4, "Gulim", 18) with { Italic = true }, Run(7, 6, "Gulim", 18)),
            Box("탭\t과 정렬", 160) with { Alignment = TextAlignment.Right },
        };
        foreach (var box in boxes)
        {
            using var screen = Screen(box, 3);
            using var outlines = Outlines(box, 3);
            var (mean, painted) = Difference(screen, outlines);
            double mismatch = Mismatch(screen, outlines);
            Assert.True(painted > 500, $"{box.Text}: only {painted} painted pixels");
            Assert.True(mean < 30 && mismatch < .01, $"{box.Text}: mean painted-pixel RGB difference {mean:0.0}, clear mismatch {mismatch:P2}");
        }
    }

    [Fact]
    public void ColorEmojiAreOneColorOutlinesOnScreenAndInPdf()
    {
        // Skia gives no outline for a color glyph (Segoe UI Emoji is COLRv1); the engine draws the font's one-color
        // glyphs instead, in the run's color, like the WPF engine did.
        var box = Box("😀👍🏽❤", 160, Run(0, 5, size: 30));
        var glyphs = ((TextPaintGroup)SkiaTextEngine.Layout(box).Paint).Children.Cast<TextPaintShape>().ToArray();
        Assert.True(glyphs.Sum(g => g.Figures.Length) >= 3, "every emoji has an outline");
        Assert.All(glyphs, g => Assert.Equal(new TextColor(0, 0, 0, 1), g.Fill));
        using var screen = Screen(box, 3);
        using var outlines = Outlines(box, 3);
        Assert.True(Mismatch(screen, outlines) < .01);
    }

    [Fact]
    public void ThePaintTreeIsClippedFilledBorderedAndScaled()
    {
        var layout = SkiaTextEngine.Layout(Styled());
        var root = Assert.IsType<TextPaintGroup>(layout.Paint);
        Assert.Equal(Affine.Identity, root.Transform);
        var clip = Assert.Single(root.Clip!.Value);
        Assert.Equal(new Point2(170, layout.Height), clip.Points[2]);
        var shapes = root.Children.Cast<TextPaintShape>().ToArray();
        Assert.Equal(new TextColor(240, 200, 100, .35), shapes[0].Fill); // fill first
        Assert.Equal(new TextColor(20, 70, 170, .7), shapes[1].Stroke); // then the border
        Assert.Equal(1, shapes[1].StrokeWidth);
        Assert.Equal(new TextColor(255, 235, 59, .6), shapes[2].Fill); // highlights behind the glyphs
        Assert.Equal(new TextColor(129, 199, 132, .5), shapes[3].Fill);
        Assert.Contains(shapes, s => s.Fill == new TextColor(190, 30, 60, 1) && s.Figures.Length > 5); // "English" in red
        Assert.All(shapes.Skip(4).Where(s => s.Figures.Length > 1).SelectMany(s => s.Figures), f => Assert.True(f.Closed)); // glyph contours

        var scaled = SkiaTextEngine.Layout(Styled() with { LayoutScale = 2, WidthPt = 340, Runs = [.. Styled().Runs.Select(r => r with { SizePt = r.SizePt * 2 })] });
        var outer = Assert.IsType<TextPaintGroup>(scaled.Paint);
        Assert.Equal(Affine.Scale(2), outer.Transform);
        Assert.Equal(root.Children.Length, Assert.IsType<TextPaintGroup>(Assert.Single(outer.Children)).Children.Length);
    }

    [Fact]
    public void BoldAndItalicChangeTheOutlinesEvenWithoutSuchFaces()
    {
        // Gulim has no bold or italic face: Skia (or DirectWrite's simulation on Windows) emboldens and slants it.
        static TextBoxAnnotation Gulim(bool bold, bool italic) => Box("가나다 ABC", 220, Run(0, 7, "Gulim", 30) with { Bold = bold, Italic = italic });
        using var regular = Outlines(Gulim(false, false), 2);
        using var bold = Outlines(Gulim(true, false), 2);
        using var italic = Outlines(Gulim(false, true), 2);
        var (r, b, i) = (Ink(regular), Ink(bold), Ink(italic));
        Assert.True(b.Ink > r.Ink * 1.1, $"bold ink {b.Ink:0} vs {r.Ink:0}");
        // Slanted: the top half moves right against the bottom half.
        Assert.True(i.TopX - i.BottomX > r.TopX - r.BottomX + 2, $"italic top-bottom {i.TopX - i.BottomX:0.0} vs {r.TopX - r.BottomX:0.0}");
    }

    [Fact]
    public void UnderlineAndStrikethroughFollowTheRunButNotHangingSpaces()
    {
        var box = Box("abc def   ", 300, Run(0, 10, "Arial", 20) with { Underline = true, Strikethrough = true });
        var layout = SkiaTextEngine.Layout(box);
        var bars = ((TextPaintGroup)layout.Paint).Children.Cast<TextPaintShape>().Where(s => s.Figures.Length == 1 && s.Figures[0].Points.Length == 4).ToArray();
        Assert.Equal(2, bars.Length);
        double baseline = layout.Lines[0].Baseline;
        var under = bars.Select(b => b.Figures[0].Points).Single(p => p[0].Y > baseline);
        var strike = bars.Select(b => b.Figures[0].Points).Single(p => p[0].Y < baseline);
        Assert.True(strike[2].Y < baseline - 3, "strikethrough sits through the lower-case letters");
        var f = layout.Characters.Single(c => c.Text == "f");
        Assert.Equal(layout.Characters[0].Bounds.X, under[0].X, 6);
        Assert.Equal(f.Bounds.X + f.Bounds.Width, under[1].X, 6); // stops at "f", not at the trailing spaces
    }

    [Fact]
    public void FontReportsTheFaceThatDrewEachCharacter()
    {
        const string text = "가A😀͸\n나";
        var box = Box(text, 300, Run(0, 5, "Arial"), Run(5, 2, "NoSuchFont-P2"));
        var uses = SkiaText.Engine.Fonts(box);
        Assert.Equal([0, 1, 2, 4, 6], uses.Select(u => u.Offset)); // the line feed is left out
        Assert.Equal(SkiaText.Engine.FallbackFont, uses[0].Font); // Arial has no Hangul: the Korean fallback draws it
        Assert.Equal("Arial", uses[1].Font);
        Assert.Equal(2, uses[2].Length);
        Assert.NotEqual("Arial", uses[2].Font);
        Assert.False(uses[2].Missing);
        Assert.True(uses[3].Missing); // an unassigned code point: no font has it
        Assert.Equal(("NoSuchFont-P2", SkiaText.Engine.FallbackFont), (uses[4].Requested, uses[4].Font));
        Assert.All(uses.Where(u => u.Offset != 6), u => Assert.Equal("Arial", u.Requested));
    }

    [Fact]
    public void FamilyListUsesTheNamesFacesReport()
    {
        var families = SkiaText.Engine.FontFamilies();
        Assert.Contains(SkiaText.Engine.FallbackFont, families);
        Assert.All(families, f => Assert.True(SkiaText.Engine.FontAvailable(f), f));
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain(families, f => f.Any(c => c is >= '가' and <= '힣'));
    }
}
