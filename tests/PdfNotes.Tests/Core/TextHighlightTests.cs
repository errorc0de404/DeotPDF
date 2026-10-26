using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Tests.Core;

/// <summary>D09: glyph carets and line quads (spec 9.3), manual height, text box highlight ranges under editing (spec 9.4).</summary>
public sealed class TextHighlightTests
{
    private static readonly Point2 Right = new(1, 0);

    /// <summary>
    /// D14a fix: at a wrap the first character of the next line used to get the previous line's key, so a highlight over
    /// the wrap became one band between both lines. Each laid-out line now gets its own rectangle, on that line.
    /// </summary>
    [Fact]
    public void HighlightOverAWrapIsOneRectanglePerVisualLine()
    {
        const string text = "회의 메모: Q3 계획 — budget 12.5% 증가, 담당 김철수/이영희, 마감 10/31.";
        var box = TextEditingTests.Box(text, 220) with { Highlights = [new(10, 20, HighlightHeightMode.Auto, null, new(129, 199, 132), .5)] };
        var characters = PdfNotes.Text.TextLayout.Measure(box).Characters;
        foreach (var line in characters.GroupBy(c => c.Line))
            Assert.True(line.Max(c => c.Bounds.Y) - line.Min(c => c.Bounds.Y) < 1, $"line {line.Key} mixes visual lines");
        var lines = PdfNotes.Text.TextLayout.HighlightLines(box, characters).Single().Lines;
        Assert.Equal(2, lines.Length);
        var first = characters.Single(c => c.Offset == 10);
        Assert.Equal(first.Bounds.X, lines.MinBy(r => r.Y).X, 6); // starts at the highlighted character, not the left edge
        Assert.True(lines.Max(r => r.Y) >= lines.Min(r => r.Y + r.Height) - 1e-6, "the two rectangles do not overlap vertically");
    }

    /// <summary>Horizontal glyphs of <paramref name="text"/>, 10pt wide, 12pt tall, starting at (x, top).</summary>
    private static IEnumerable<TextGlyph> Line(string text, double x, double top, double width = 10, double height = 12) =>
        text.Select((c, i) => new TextGlyph(c.ToString(), new(new(x + i * width, top), new(x + (i + 1) * width, top),
            new(x + i * width, top + height), new(x + (i + 1) * width, top + height)), Right));

    private static TextGlyph Generated(string text) => new(text, default, Right);

    private static TextGlyph[] TwoLines() => [.. Line("Hello", 100, 100), Generated("\r"), Generated("\n"), .. Line("World", 100, 120)];

    [Fact]
    public void CaretsFallBeforeOrAfterTheNearestGlyph()
    {
        var glyphs = TwoLines();
        Assert.Equal(0, TextSelection.CaretAt(glyphs, new(102, 106)));
        Assert.Equal(1, TextSelection.CaretAt(glyphs, new(108, 106)));
        Assert.Equal(5, TextSelection.CaretAt(glyphs, new(149, 106)));
        Assert.Equal(9, TextSelection.CaretAt(glyphs, new(125, 126))); // 'r' of World: generated glyphs count but are never hit
        Assert.Null(TextSelection.CaretAt(glyphs, new(100, 160), maxDistance: 5));
        Assert.Equal(12, TextSelection.CaretAt(glyphs, new(500, 128))); // far right: after the last glyph
        Assert.True(TextSelection.IsOverText(glyphs, new(130, 110), 0));
        Assert.False(TextSelection.IsOverText(glyphs, new(130, 116), 2));
    }

    [Fact]
    public void ASelectionOverTwoLinesMakesOneQuadPerLineAndKeepsTheOriginalText()
    {
        var glyphs = TwoLines();
        var quads = TextSelection.LineQuads(glyphs, 2, 10); // "llo\r\nWor"
        Assert.Equal(2, quads.Length);
        Assert.Equal(new Quad(new(120, 100), new(150, 100), new(120, 112), new(150, 112)), quads[0]);
        Assert.Equal(new Quad(new(100, 120), new(130, 120), new(100, 132), new(130, 132)), quads[1]);
        Assert.Equal("llo\r\nWor", TextSelection.TextOf(glyphs, 2, 10));
        Assert.Empty(TextSelection.LineQuads(glyphs, 5, 7)); // only generated line-end characters
    }

    [Fact]
    public void ColumnsAndOrientationChangesStartNewLines()
    {
        // Reading order goes down the left column, then jumps up to the right column.
        TextGlyph[] columns = [.. Line("ab", 100, 100), .. Line("cd", 100, 120), .. Line("ef", 300, 100)];
        Assert.Equal(3, TextSelection.LineQuads(columns, 0, 6).Length);
        // Text reading upwards (rotated 90° counterclockwise on the page): one quad along that direction.
        var up = new Point2(0, -1);
        TextGlyph[] vertical = [.. Enumerable.Range(0, 3).Select(i => new TextGlyph("v", new(new(200, 300 - i * 10), new(200, 290 - i * 10),
            new(212, 300 - i * 10), new(212, 290 - i * 10)), up))];
        var quad = Assert.Single(TextSelection.LineQuads(vertical, 0, 3));
        // Upper-left/upper-right run along the reading direction on the glyphs' "top" side (the left edge x = 200).
        Assert.Equal(new Point2(200, 300), quad.UpperLeft);
        Assert.Equal(new Point2(200, 270), quad.UpperRight);
        Assert.Equal(new Point2(212, 300), quad.LowerLeft);
        Assert.Equal(new Point2(212, 270), quad.LowerRight);
        Assert.Equal(1, TextSelection.CaretAt(vertical, new(206, 292))); // past the middle of the first glyph upwards
    }

    [Fact]
    public void ManualHeightIsCenteredOnTheGlyphLineAndAutoKeepsTheGlyphHeight()
    {
        var q = new Quad(new(100, 100), new(150, 100), new(100, 112), new(150, 112));
        Assert.Equal(q, TextSelection.Painted(q, HighlightHeightMode.Auto, null));
        var manual = TextSelection.Painted(q, HighlightHeightMode.Manual, 20);
        Assert.Equal(new Quad(new(100, 96), new(150, 96), new(100, 116), new(150, 116)), manual);
        var h = new PdfTextHighlight(Guid.NewGuid(), false, [q], "x", HighlightHeightMode.Manual, 4, new(255, 230, 0), .4);
        Assert.True(AnnotationGeometry.Hit(h, new(120, 106), 0));
        Assert.False(AnnotationGeometry.Hit(h, new(120, 101), 0)); // outside the 4pt band
        Assert.Equal(new Rect2(100, 104, 50, 4), AnnotationGeometry.LocalBounds(h));
        Assert.False(AnnotationGeometry.CanTransform(h));
        Assert.Throws<NotSupportedException>(() => AnnotationGeometry.Similarity(h, default, move: new(1, 1)));
    }

    [Fact]
    public void GlyphHighlightsAreLassoedAndErasedWholeButNeverCopied()
    {
        var q = new Quad(new(100, 100), new(150, 100), new(100, 112), new(150, 112));
        var h = new PdfTextHighlight(Guid.NewGuid(), false, [q], "x", HighlightHeightMode.Auto, null, new(255, 230, 0), .4);
        var model = DocumentModel.Create([TestModels.A4()]);
        model = model with { Pages = [model.Pages[0] with { Annotations = [h] }] };
        Assert.Equal([h.Id], Lasso.Select(model.Pages[0], [new(90, 90), new(160, 90), new(160, 106), new(90, 106)], _ => default));
        // D14g (spec 7.3–7.4): the highlighter's marks on text are erased — whole, also by the area eraser.
        foreach (var mode in new[] { EraserMode.Stroke, EraserMode.Area })
        {
            var gesture = new EraseGesture(model, model.Pages[0].Id, mode, 6);
            Assert.True(gesture.AddPoint(new(125, 106)));
            Assert.Empty(gesture.Preview[h.Id]);
            var session = new DocumentSession(model);
            Assert.True(session.Execute(gesture.Commit()));
            Assert.Empty(session.Current.Pages[0].Annotations);
        }
        var away = new EraseGesture(model, model.Pages[0].Id, EraserMode.Area, 6);
        Assert.False(away.AddPoint(new(125, 130))); // not touched
        var locked = model with { Pages = [model.Pages[0] with { Annotations = [h with { Locked = true }] }] };
        var none = new EraseGesture(locked, locked.Pages[0].Id, EraserMode.Stroke, 50);
        none.AddPoint(new(125, 106));
        Assert.True(none.Commit().IsEmpty); // locked marks stay
        Assert.Empty(AnnotationClipboard.Capture(model, model.Pages[0].Id, [h.Id]).Annotations);
    }

    [Fact]
    public void DoubleClickWordsStopAtSpacesPunctuationAndLineEnds()
    {
        TextGlyph[] glyphs = [.. Line("this is an apple.", 100, 100), Generated("\r"), Generated("\n"), .. Line("apple한글", 100, 120)];
        // "apple" on the first line: glyphs 11..15; the period (16) is not part of it.
        Assert.Equal((11, 16), TextSelection.WordAt(glyphs, new(125 + 100, 106), 2));
        Assert.Equal((5, 7), TextSelection.WordAt(glyphs, new(155, 106), 2)); // "is"
        Assert.Null(TextSelection.WordAt(glyphs, new(145, 106), 2));          // the space between "this" and "is"
        Assert.Null(TextSelection.WordAt(glyphs, new(265, 106), 2));          // the period
        Assert.Equal((19, 26), TextSelection.WordAt(glyphs, new(105, 126), 2)); // "apple한글": letters of any script, not joined to line 1
        Assert.Null(TextSelection.WordAt(glyphs, new(105, 160), 2));
        Assert.Equal((0, 3), TextSelection.Word(5, 1, i => "가나다 라"[i].ToString()));
        Assert.True(TextSelection.IsWordText("é") && TextSelection.IsWordText("1") && !TextSelection.IsWordText(" ") && !TextSelection.IsWordText("-"));
    }

    // ----- text box ranges -----

    private static TextHighlightRange Mark(int start, int length) => new(start, length, HighlightHeightMode.Auto, null, new(255, 230, 0), .4);

    private static (int, int)[] Ranges(ImmutableArray<TextHighlightRange> r) => [.. r.Select(h => (h.Start, h.Length))];

    [Theory]
    [InlineData("abcdef", "abXcdef", 3, 3, 2)]   // insert at the start edge: not joined, shifted
    [InlineData("abcdef", "abcXdef", 4, 2, 3)]   // insert strictly inside: joined
    [InlineData("abcdef", "abcdXef", 5, 2, 2)]   // insert at the end edge: not joined
    [InlineData("abcdef", "abef", 2, 2, 0)]      // the whole highlight deleted
    [InlineData("abcdef", "abdef", 2, 2, 1)]     // head deleted
    [InlineData("abcdef", "abcef", 3, 2, 1)]     // tail deleted
    [InlineData("abcdef", "aXf", 2, 0, 0)]       // replaced together with its neighbours
    [InlineData("abcdef", "Xabcdef", 1, 3, 2)]   // insert before: shifted
    public void HighlightsFollowTheirCharacters(string before, string after, int caret, int expectedStart, int expectedLength)
    {
        var result = TextHighlights.Remap(before, after, [Mark(2, 2)], caret); // "cd"
        if (expectedLength == 0) Assert.Empty(result);
        else Assert.Equal([(expectedStart, expectedLength)], Ranges(result));
    }

    [Fact]
    public void TheCaretDecidesAmbiguousEditsNextToEqualCharacters()
    {
        // Highlight "aa" in "xaay". Typing "a" right before it is ambiguous by text alone.
        var highlights = ImmutableArray.Create(Mark(1, 2));
        Assert.Equal([(2, 2)], Ranges(TextHighlights.Remap("xaay", "xaaay", highlights, caretAfter: 2)));  // typed at the start edge
        Assert.Equal([(1, 3)], Ranges(TextHighlights.Remap("xaay", "xaaay", highlights, caretAfter: 3)));  // typed inside
        Assert.Equal([(1, 2)], Ranges(TextHighlights.Remap("xaay", "xaaay", highlights, caretAfter: 4)));  // typed at the end edge
        Assert.Equal([(1, 1)], Ranges(TextHighlights.Remap("xaay", "xay", highlights, caretAfter: 1)));    // deleted inside
    }

    [Fact]
    public void RangesStayOnGraphemesAcrossNewlinesAndKeepTheirStyle()
    {
        string before = "가😀나다";
        var styled = new TextHighlightRange(0, 3, HighlightHeightMode.Manual, 9, new(0, 200, 0), .5); // "가😀"
        var result = TextHighlights.Remap(before, "가\n😀나다", [styled], caretAfter: 2);
        var only = Assert.Single(result);
        Assert.Equal((0, 4), (only.Start, only.Length)); // the newline inside joins, the emoji stays whole
        Assert.Equal(styled with { Start = 0, Length = 4 }, only);
        var cut = TextHighlights.Remap(before, "가\uD83D나다", [styled], caretAfter: 2); // half an emoji left behind
        Assert.True(cut.All(h => h.Start + h.Length <= 3));
    }

    [Fact]
    public void AddReplacesOverlapsAndRemoveKeepsTheOutsideParts()
    {
        var box = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Identity, "0123456789",
            [new TextRun(0, 10, "Malgun Gothic", 12, RgbColor.Black, false, false, false, false)], TextAlignment.Left, 100, TextBoxStyle.Default, []);
        box = TextHighlights.Add(box, 2, 3, Mark(0, 0));
        box = TextHighlights.Add(box, 6, 2, Mark(0, 0) with { Color = new(0, 0, 255) });
        box = TextHighlights.Add(box, 4, 3, Mark(0, 0) with { Color = new(255, 0, 0) });
        Assert.Equal([(2, 2), (4, 3), (7, 1)], Ranges(box.Highlights));
        Assert.Null(ModelValidator.ValidateAnnotation(box, _ => true));
        Assert.True(TextHighlights.Covers(box, 2, 6));
        Assert.False(TextHighlights.Covers(box, 1, 3));
        box = TextHighlights.Remove(box, 3, 2);
        Assert.Equal([(2, 1), (5, 2), (7, 1)], Ranges(box.Highlights));
        Assert.Same(box, TextHighlights.Remove(box, 9, 1));
    }
}
