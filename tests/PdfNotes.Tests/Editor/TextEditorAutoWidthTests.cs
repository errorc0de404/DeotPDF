using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;
using PdfNotes.TextEditor;
using PdfNotes.Tests.TextEditor;

namespace PdfNotes.Tests.Editor;

/// <summary>1.4 auto width Task 3: the editor model fits an auto width box after every edit, inside that edit's Undo step (plan 1.1).</summary>
public sealed class TextEditorAutoWidthTests
{
    private const double PageW = 595, PageH = 842;
    private const string Long = "줄바꿈 없이 이어지는 긴 문단은 페이지 오른쪽 끝까지 늘어난 다음 거기서 접혀야 한다. The quick brown fox jumps over the lazy dog.";

    private static TextBoxAnnotation Auto(string text = "", double x = 50) =>
        TextEditorModelTests.Box(text) with { AutoWidth = true, Transform = Affine.Translation(x, 60) };

    private static TextEditorModel Editor(TextBoxAnnotation box) =>
        new(box, null, SkiaTextEngine.Layout, b => TextAutoWidth.Fit(b, PageW, PageH));

    [Fact]
    public void TypingGrowsAnAutoBox()
    {
        var editor = Editor(Auto("가"));
        editor.Type("가");
        double before = editor.Box.WidthPt;
        editor.Type("가나다");
        Assert.True(editor.Box.WidthPt > before, $"{before} -> {editor.Box.WidthPt}");
        Assert.Single(editor.Layout.Lines);
        Assert.Equal(TextAutoWidth.NaturalWidth(editor.Box), editor.Box.WidthPt, 6);
    }

    [Fact]
    public void AFixedBoxKeepsItsWidth()
    {
        var editor = Editor(TextEditorModelTests.Box("가", 200));
        editor.Type(Long);
        Assert.Equal(200, editor.Box.WidthPt);
    }

    /// <summary>
    /// Review Focus 1: the IME's composition (here the model's <see cref="TextEditorModel.SetComposition"/>, as the
    /// platform calls it; not a real IME) widens the shown box, so a line does not wrap and unwrap while composing.
    /// </summary>
    [Fact]
    public void CompositionGrowsTheBoxToo()
    {
        var editor = Editor(Auto());
        editor.Type("가나");
        double committed = editor.Box.WidthPt;
        editor.SetComposition("다라마바사");
        Assert.True(editor.DisplayBox.WidthPt > committed, $"{committed} -> {editor.DisplayBox.WidthPt}");
        Assert.Single(SkiaTextEngine.Layout(editor.DisplayBox).Lines);
        Assert.Equal(committed, editor.Box.WidthPt); // the document's box waits for the commit
        double composing = editor.DisplayBox.WidthPt;
        editor.CommitComposition("다라마바사");
        Assert.Equal(composing, editor.Box.WidthPt, 6);
    }

    /// <summary>Review Focus 4: the editor's own Ctrl+Z / Ctrl+Y bring the width back with the text.</summary>
    [Fact]
    public void EditorUndoRestoresWidthWithText()
    {
        var editor = Editor(Auto("가"));
        editor.Type("가");
        editor.Type(" ");
        var (text, width) = (editor.Box.Text, editor.Box.WidthPt);
        editor.Type("나다라마바사");
        double wide = editor.Box.WidthPt;
        Assert.True(wide > width);

        Assert.True(editor.Undo());
        Assert.Equal(text, editor.Box.Text);
        Assert.Equal(width, editor.Box.WidthPt);
        Assert.True(editor.Redo());
        Assert.Equal(wide, editor.Box.WidthPt);
    }

    [Fact]
    public void PasteOfLongTextWrapsAtTheCapAndShrinksAfterDelete()
    {
        var editor = Editor(Auto(x: 300));
        editor.Paste(new TextEditorModelTests.Clipboard { Plain = "짧은 줄\n" + Long });
        Assert.True(editor.Box.AutoWidth);
        Assert.Equal(TextAutoWidth.Cap(editor.Box, PageW, PageH), editor.Box.WidthPt, 6);
        Assert.Equal(PageW - 300, editor.Box.WidthPt, 6); // left aligned: from the anchor to the page edge
        Assert.True(editor.Layout.Lines.Length > 2); // the long line folds at the cap

        editor.Select(editor.Box.Text.IndexOf('\n'), editor.Box.Text.Length);
        editor.Key(EditorKey.Delete);
        Assert.Equal("짧은 줄", editor.Box.Text);
        Assert.True(editor.Box.WidthPt < 100, $"{editor.Box.WidthPt}");
        Assert.Equal(TextAutoWidth.NaturalWidth(editor.Box), editor.Box.WidthPt, 6);
    }

    [Fact]
    public void AlignmentChangeRefitsAroundTheNewAnchor()
    {
        var editor = Editor(Auto());
        editor.Type("가나다");
        double width = editor.Box.WidthPt;
        editor.Change(b => b with { Alignment = TextAlignment.Center });
        editor.Type("라마바");
        double grown = editor.Box.WidthPt - width;
        Assert.True(grown > 0);
        // Centered: it grew by half on each side.
        Assert.Equal(50 + width / 2, editor.Box.Transform.OffsetX + editor.Box.WidthPt / 2, 6);
    }
}
