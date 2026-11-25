using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;

namespace PdfNotes.Tests.TextEngine;

/// <summary>1.4 auto width Task 2: natural width, page-edge cap and the anchor that stays put (plan 1.1).</summary>
public sealed class TextAutoWidthTests
{
    private const double PageW = 595, PageH = 842, Pad = TextLayout.PaddingPt;
    private const string Long = "줄바꿈 없이 이어지는 긴 문단은 페이지 오른쪽 끝까지 늘어난 다음 거기서 접혀야 한다. The quick brown fox jumps over the lazy dog.";

    private static TextRun Run(int length, double size = 12) => new(0, length, "Malgun Gothic", size, RgbColor.Black, false, false, false, false);

    private static TextBoxAnnotation Box(string text, Affine transform, TextAlignment align = TextAlignment.Left, double width = 180, double size = 12) =>
        new(Guid.NewGuid(), false, transform, text, text.Length == 0 ? [] : [Run(text.Length, size)], align, width, TextBoxStyle.Default, []) { AutoWidth = true };

    private static TextBoxAnnotation At(string text, double x, double y, TextAlignment align = TextAlignment.Left, double width = 180) =>
        Box(text, Affine.Translation(x, y), align, width);

    private static double RightEdge(TextBoxAnnotation box) => TextLayout.Measure(box).Characters.Max(c => c.Bounds.X + c.Bounds.Width);

    private static Point2 Anchor(TextBoxAnnotation box) => box.Transform.Transform(new(box.Alignment switch
    {
        TextAlignment.Center => box.WidthPt / 2,
        TextAlignment.Right => box.WidthPt,
        _ => 0,
    }, 0));

    [Fact]
    public void ShortTextIsAsWideAsItself()
    {
        var box = At("가나다", 100, 100);
        var fit = TextAutoWidth.Fit(box, PageW, PageH);

        Assert.Equal(TextAutoWidth.NaturalWidth(box), fit.WidthPt);
        Assert.Single(TextLayout.Measure(fit).Lines);
        Assert.Equal(RightEdge(fit) + Pad, fit.WidthPt, 6);
        Assert.True(fit.WidthPt < 180, $"short text is {fit.WidthPt}pt wide");
        Assert.Equal(box.Transform, fit.Transform);
    }

    [Fact]
    public void LongestLineSetsTheWidth()
    {
        var fit = TextAutoWidth.Fit(At("가\n가나다라마\n가", 100, 100), PageW, PageH);
        var middle = TextAutoWidth.NaturalWidth(At("가나다라마", 100, 100));

        Assert.Equal(middle, fit.WidthPt, 6);
        Assert.Equal(3, TextLayout.Measure(fit).Lines.Length);
    }

    [Fact]
    public void LongParagraphWrapsAtThePageEdge()
    {
        var box = At(Long, 400, 100);
        Assert.True(TextAutoWidth.NaturalWidth(box) > 195);
        var fit = TextAutoWidth.Fit(box, PageW, PageH);

        Assert.Equal(195, fit.WidthPt, 6);
        Assert.True(TextLayout.Measure(fit).Lines.Length >= 2);
        Assert.True(fit.Transform.Transform(new(RightEdge(fit), 0)).X <= PageW, "text stays on the page");
    }

    [Theory]
    [InlineData(TextAlignment.Center)]
    [InlineData(TextAlignment.Right)]
    public void CenterKeepsItsCenterAndRightKeepsItsRightEdge(TextAlignment align)
    {
        var turned = Box("가나다", Affine.Rotation(0.4).Then(Affine.Translation(200, 300)), align);
        foreach (var box in new[] { At("가나다", 200, 300, align), turned, At(Long, 200, 300, align) })
        {
            var fit = TextAutoWidth.Fit(box, PageW, PageH);
            Assert.NotEqual(box.WidthPt, fit.WidthPt, 3);
            var before = Anchor(box);
            var after = Anchor(fit);
            Assert.True(Math.Abs(before.X - after.X) < 0.01 && Math.Abs(before.Y - after.Y) < 0.01, $"anchor moved from {before} to {after}");
        }
    }

    [Fact]
    public void CenterCapIsTwiceTheShorterSide()
    {
        // The center of a 180pt box at x 100 is at 190: 190 to the left edge, 405 to the right.
        Assert.Equal(380, TextAutoWidth.Cap(At(Long, 100, 100, TextAlignment.Center), PageW, PageH), 6);
        // The right edge at 280 grows to the left: 280 to the left page edge.
        Assert.Equal(280, TextAutoWidth.Cap(At(Long, 100, 100, TextAlignment.Right), PageW, PageH), 6);
    }

    [Fact]
    public void RotatedBoxCapsAlongItsOwnAxis()
    {
        // Turned 90°: the box's horizontal runs down the page, so its cap is the distance to the bottom edge.
        var box = Box(Long, Affine.Rotation(Math.PI / 2).Then(Affine.Translation(100, 300)));
        Assert.Equal(PageH - 300, TextAutoWidth.Cap(box, PageW, PageH), 6);

        var fit = TextAutoWidth.Fit(box, PageW, PageH);
        Assert.Equal(PageH - 300, fit.WidthPt, 6);
        Assert.Equal(box.Transform, fit.Transform);
    }

    [Fact]
    public void AnchorOutsideOrTooCloseUsesTheDefaultCap()
    {
        Assert.Equal(180, TextAutoWidth.DefaultCapPt);
        Assert.Equal(180, TextAutoWidth.Cap(At(Long, -10, 100), PageW, PageH));
        Assert.Equal(180, TextAutoWidth.Cap(At(Long, 100, PageH + 5), PageW, PageH));
        Assert.Equal(180, TextAutoWidth.Cap(At(Long, PageW - 3, 100), PageW, PageH));
        Assert.Equal(180, TextAutoWidth.Fit(At(Long, PageW - 3, 100), PageW, PageH).WidthPt, 6);
    }

    [Fact]
    public void FixedBoxIsUntouched()
    {
        var box = At("가나다", 100, 100) with { AutoWidth = false };
        Assert.True(ReferenceEquals(TextAutoWidth.Fit(box, PageW, PageH), box));
    }

    [Fact]
    public void FittedBoxIsAlreadyFit()
    {
        var fit = TextAutoWidth.Fit(At(Long, 100, 100, TextAlignment.Right), PageW, PageH);
        Assert.True(ReferenceEquals(TextAutoWidth.Fit(fit, PageW, PageH), fit));
    }

    [Fact]
    public void ScaledBoxFitsWithScaledPadding()
    {
        // Review Focus 2: a box on a page reduced to half (LayoutScale 0.5, sizes already scaled) has half the padding and
        // half the minimum width.
        static TextBoxAnnotation Half(string text, double x) => Box(text, Affine.Translation(x, 100), size: 6) with { LayoutScale = 0.5 };

        Assert.Equal(TextAutoWidth.NaturalWidth(At("가나다", 0, 100)) / 2, TextAutoWidth.NaturalWidth(Half("가나다", 0)), 6);
        Assert.Equal(2 * 12 + 2 * Pad, TextAutoWidth.NaturalWidth(At(".", 0, 100)), 6);
        Assert.Equal(2 * 6 + 2 * Pad * 0.5, TextAutoWidth.NaturalWidth(Half(".", 0)), 6);

        // 20pt to the edge is below the unscaled minimum (32) but above the scaled one (16).
        Assert.Equal(180, TextAutoWidth.Cap(At(Long, PageW - 20, 100), PageW, PageH));
        var fit = TextAutoWidth.Fit(Half(Long, PageW - 20), PageW, PageH);
        Assert.Equal(20, fit.WidthPt, 6);
        Assert.True(TextLayout.Measure(fit).Lines.Length >= 2);
        Assert.All(TextLayout.Measure(fit).Characters.Where(c => !char.IsWhiteSpace(c.Text[0])),
            c => Assert.True(c.Bounds.X + c.Bounds.Width <= 20 - Pad * 0.5 + 1e-6, $"'{c.Text}' ends at {c.Bounds.X + c.Bounds.Width}"));

        var shortFit = TextAutoWidth.Fit(Half("가나다", 100), PageW, PageH);
        Assert.Single(TextLayout.Measure(shortFit).Lines);
        Assert.Equal(RightEdge(shortFit) + Pad * 0.5, shortFit.WidthPt, 6);
    }

    [Fact]
    public void NaturalWidthCountsTabsAndEmoji()
    {
        // Review Focus 3: a tab advances to the next tab stop and counts as measured; an emoji (a surrogate pair) is one
        // character as wide as its glyph. The fitted box keeps the line on one line.
        const string text = "가\t나😀";
        var box = At(text, 100, 100);
        double natural = TextAutoWidth.NaturalWidth(box);
        var wide = TextLayout.Measure(box with { WidthPt = 10_000 });
        var emoji = wide.Characters.Single(c => c.Text == "😀");

        Assert.Equal(emoji.Bounds.X + emoji.Bounds.Width + Pad, natural, 6);
        Assert.True(natural > TextAutoWidth.NaturalWidth(At("가나😀", 100, 100)) + 10, "the tab adds its advance");
        Assert.True(emoji.Bounds.X >= Pad + SkiaTextEngine.TabStop, "the tab reaches its stop");

        var fit = TextAutoWidth.Fit(box, PageW, PageH);
        Assert.Equal(natural, fit.WidthPt, 6);
        Assert.Single(TextLayout.Measure(fit).Lines);
        // A tab at the end of a line counts too.
        Assert.True(TextAutoWidth.NaturalWidth(At("가\t", 100, 100)) >= Pad + SkiaTextEngine.TabStop + Pad - 1e-6);
    }
}
