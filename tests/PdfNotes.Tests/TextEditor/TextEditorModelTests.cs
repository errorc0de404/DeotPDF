using System.Reflection;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;
using PdfNotes.TextEditor;

namespace PdfNotes.Tests.TextEditor;

/// <summary>
/// Roadmap P2 S4: the platform-neutral text box editor. Layouts come from the Skia engine directly (it reports empty
/// lines), whatever engine the test assembly registered.
/// </summary>
public class TextEditorModelTests
{
    internal static readonly TextRun Style = TextEditing.Default;

    internal static TextBoxAnnotation Box(string text = "", double width = 200, params TextRun[] runs) =>
        new(Guid.NewGuid(), false, Affine.Translation(50, 60), text, runs.Length > 0 ? [.. runs] : text.Length == 0 ? [] : [Style with { Start = 0, Length = text.Length }],
            TextAlignment.Left, width, TextBoxStyle.Default, []);

    internal static TextEditorModel NewEditor(TextBoxAnnotation box, TextRun? typing = null) => new(box, typing, SkiaTextEngine.Layout);

    /// <summary>A new box with <paramref name="text"/> typed into it grapheme by grapheme, as keys would.</summary>
    internal static TextEditorModel Typed(string text, double width = 200)
    {
        var editor = NewEditor(Box(width: width));
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) editor.Type(e.GetTextElement());
        return editor;
    }

    private static void Valid(TextEditorModel editor) => Assert.Null(ModelValidator.ValidateAnnotation(editor.Box, _ => false));

    internal sealed class Clipboard : ITextClipboard
    {
        public string? Plain, Formatted;
        public void Set(string plain, string formatted) { Plain = plain; Formatted = formatted; }
        public string? GetFormatted() => Formatted;
        public string? GetText() => Plain;
    }

    // ----- typing and deleting -----

    [Fact]
    public void TypingFillsAnEmptyBoxInTheTypingStyle()
    {
        var red = Style with { Color = new(200, 0, 0), SizePt = 18 };
        var editor = NewEditor(Box(), red);
        editor.Type("한글 English 😀");
        editor.Key(EditorKey.Enter);
        editor.Type("둘째\r\n줄");
        Assert.Equal("한글 English 😀\n둘째\n줄", editor.Box.Text);
        Assert.All(editor.Box.Runs, r => Assert.Equal(red with { Start = r.Start, Length = r.Length }, r));
        Assert.Equal(editor.Box.Text.Length, editor.Caret);
        Valid(editor);
    }

    [Fact]
    public void TypingOverASelectionTakesItsFirstCharacterStyle()
    {
        var bold = Style with { Bold = true };
        var editor = NewEditor(Box("abcdef", 200, bold with { Start = 0, Length = 3 }, Style with { Start = 3, Length = 3 }));
        editor.Select(1, 4);
        editor.Type("X");
        Assert.Equal("aXef", editor.Box.Text);
        Assert.True(editor.Box.Runs.Single(r => r.Start <= 1 && r.Start + r.Length > 1).Bold);
        Valid(editor);
    }

    [Theory]
    [InlineData("a👍🏽", "a")]          // an emoji with its skin tone
    [InlineData("aé", "a")]       // e with a combining acute
    [InlineData("가한", "가")]          // a Hangul syllable
    [InlineData("a\r\n", "a")]          // typed CR LF became one line feed
    public void BackspaceDeletesAWholeGrapheme(string text, string left)
    {
        var editor = Typed(text);
        editor.Key(EditorKey.Backspace);
        Assert.Equal(left, editor.Box.Text);
        Valid(editor);
    }

    [Fact]
    public void DeleteAndWordDeletion()
    {
        var editor = Typed("회의 메모: Q3 계획");
        editor.Key(EditorKey.Home);
        editor.Key(EditorKey.Delete);
        Assert.Equal("의 메모: Q3 계획", editor.Box.Text);
        editor.Key(EditorKey.Delete, EditorModifiers.Control);
        Assert.Equal("메모: Q3 계획", editor.Box.Text);
        editor.Key(EditorKey.End);
        editor.Key(EditorKey.Backspace, EditorModifiers.Control);
        Assert.Equal("메모: Q3 ", editor.Box.Text);
        editor.Key(EditorKey.Backspace, EditorModifiers.Control);
        Assert.Equal("메모: ", editor.Box.Text);
        editor.SelectAll();
        editor.Key(EditorKey.Backspace);
        Assert.Equal("", editor.Box.Text);
        Assert.Empty(editor.Box.Runs);
        Valid(editor);
    }

    // ----- moving -----

    [Fact]
    public void ArrowsMoveByGraphemesAndWordsAndShiftSelects()
    {
        const string text = "회의 메모: Q3계획 é!";
        var editor = Typed(text);
        editor.Key(EditorKey.Home, EditorModifiers.Control);
        var stops = new List<int>();
        for (int i = 0; i < 8; i++) { editor.Key(EditorKey.Right, EditorModifiers.Control); stops.Add(editor.Caret); }
        // Words: 회의|메모|:|Q3|계획|é|!|end — each stop after the word and its spaces.
        Assert.Equal([3, 5, 7, 9, 12, 14, 15, 15], stops);
        for (int i = 0; i < 3; i++) editor.Key(EditorKey.Left, EditorModifiers.Control);
        Assert.Equal(9, editor.Caret);
        editor.Key(EditorKey.Right);
        editor.Key(EditorKey.Right, EditorModifiers.Shift);
        Assert.Equal((10, 1), editor.Range);
        editor.Key(EditorKey.Left); // with a selection: to its start
        Assert.Equal((10, 0), editor.Range);
        editor.Key(EditorKey.End, EditorModifiers.Shift);
        Assert.Equal((10, text.Length - 10), editor.Range);
        editor.Key(EditorKey.Right);
        Assert.Equal(text.Length, editor.Caret);
        editor.Key(EditorKey.Left);
        Assert.Equal(text.Length - 1, editor.Caret);
        editor.Key(EditorKey.Left);
        Assert.Equal(text.Length - 3, editor.Caret); // over e + U+0301 in one step
    }

    [Fact]
    public void HomeEndAndVerticalMovesFollowTheLines()
    {
        // Narrow: the sentence wraps; a hard line break and an empty line follow.
        var editor = Typed("The quick brown fox jumps over the lazy dog\n\nend", 90);
        var lines = editor.Layout.Lines;
        Assert.True(lines.Length >= 5);
        var empty = lines.Single(l => l.Start == l.End && l.Start < editor.Box.Text.Length);
        editor.Key(EditorKey.Home, EditorModifiers.Control);
        editor.Key(EditorKey.End);
        Assert.Equal(lines[0].End, editor.Caret);
        var upper = editor.CaretRect();
        Assert.Equal(lines[0].Top, upper.Y, 6); // the end of the upper line, not the start of the next
        Assert.True(upper.X > lines[1].Left + 10);
        editor.Key(EditorKey.Home);
        Assert.Equal(0, editor.Caret);

        // Down keeps the x it started from, through a shorter line.
        editor.Key(EditorKey.Right); editor.Key(EditorKey.Right); editor.Key(EditorKey.Right);
        double x = editor.CaretRect().X;
        int downs = 0;
        while (editor.CaretRect().Y < empty.Top - .1) { editor.Key(EditorKey.Down); downs++; }
        Assert.Equal(empty.Start, editor.Caret); // on the empty line
        Assert.Equal(empty.Top, editor.CaretRect().Y, 6);
        editor.Key(EditorKey.Down);
        Assert.Equal(editor.Box.Text.Length - 3 + Nearest("end", x, editor), editor.Caret); // the x from the first line
        for (int i = 0; i <= downs; i++) editor.Key(EditorKey.Up);
        Assert.Equal(x, editor.CaretRect().X, 1);
        editor.Key(EditorKey.Up);
        Assert.Equal(0, editor.Caret); // up from the first line goes to the start
        editor.Key(EditorKey.End, EditorModifiers.Control);
        editor.Key(EditorKey.Down);
        Assert.Equal(editor.Box.Text.Length, editor.Caret);
    }

    /// <summary>The offset in the last line "end" nearest to x (relative to the line start).</summary>
    private static int Nearest(string word, double x, TextEditorModel editor)
    {
        int lineStart = editor.Box.Text.Length - word.Length;
        var characters = editor.Layout.Characters.Where(c => c.Offset >= lineStart).ToArray();
        for (int i = 0; i < characters.Length; i++)
            if (x < characters[i].Bounds.X + characters[i].Bounds.Width / 2) return i;
        return word.Length;
    }

    // ----- pointer -----

    [Fact]
    public void ClicksPlaceTheCaretSelectWordsAndAll()
    {
        var editor = Typed("한글 문장 English words", 300);
        var chars = editor.Layout.Characters;
        var m = chars.Single(c => c.Text == "문");
        var point = new Point2(m.Bounds.X + m.Bounds.Width * .3, m.Bounds.Y + m.Bounds.Height / 2);
        editor.PointerDown(point);
        editor.PointerUp();
        Assert.Equal((3, 0), editor.Range); // left half of 문: before it
        editor.PointerDown(new(m.Bounds.X + m.Bounds.Width * .7, point.Y));
        Assert.Equal(4, editor.Caret); // right half: after it
        var w = chars.Single(c => c.Text == "w");
        editor.PointerMove(new(w.Bounds.X + 1, point.Y));
        editor.PointerUp();
        Assert.Equal((4, w.Offset - 4), editor.Range); // dragged
        editor.PointerDown(new(w.Bounds.X + 1, point.Y), shift: true);
        Assert.Equal(4, editor.Anchor); // Shift+click extends from the same anchor

        editor.PointerDown(point, clickCount: 2);
        Assert.Equal("문장", editor.SelectedText);
        editor.PointerMove(new(w.Bounds.X + 1, point.Y));
        Assert.Equal("문장 English words", editor.SelectedText); // a double-click drag extends by words
        editor.PointerUp();
        editor.PointerDown(point, clickCount: 3);
        Assert.Equal(editor.Box.Text, editor.SelectedText);
        editor.PointerDown(new(-50, -50));
        Assert.Equal(0, editor.Caret);
        editor.PointerDown(new(1000, 1000));
        Assert.Equal(editor.Box.Text.Length, editor.Caret);
    }

    [Fact]
    public void SelectionRectanglesCoverEachLine()
    {
        var editor = Typed("first line\nsecond line is longer", 300);
        editor.Select(6, 17);
        var rects = editor.SelectionRects();
        Assert.Equal(2, rects.Count);
        var lines = editor.Layout.Lines;
        Assert.Equal(lines[0].Top, rects[0].Y, 6);
        Assert.Equal(lines[1].Top, rects[1].Y, 6);
        Assert.True(rects[0].X + rects[0].Width > editor.CaretRect(10).X); // the selected line break shows after "line"
        Assert.Equal(editor.CaretRect(17).X, rects[1].X + rects[1].Width, 6);
        var (start, end) = editor.SelectionEnds();
        Assert.Equal(editor.CaretRect(6), start);
        Assert.Equal(editor.CaretRect(17), end);
    }

    // ----- format -----

    [Fact]
    public void FormatAppliesToTheSelectionOrToTheNextTypedCharacters()
    {
        var editor = Typed("한글 English");
        editor.SelectAll();
        editor.Format(r => r with { SizePt = 24 });
        Assert.All(editor.Box.Runs, r => Assert.Equal(24, r.SizePt));
        editor.Key(EditorKey.End, EditorModifiers.Control);
        editor.Format(r => r with { SizePt = 18 });
        Assert.Equal(18, editor.TypingStyle.SizePt);
        editor.Type(" 추가");
        Assert.Equal("한글 English 추가", editor.Box.Text);
        Assert.Equal(24, editor.Box.Runs[0].SizePt);
        Assert.Equal(18, editor.Box.Runs[^1].SizePt);
        // The caret's typing style follows the character before it once the caret moves.
        editor.Key(EditorKey.Home);
        editor.Key(EditorKey.Right);
        Assert.Equal(24, editor.TypingStyle.SizePt);
        editor.Format(r => r with { Bold = true });
        editor.Key(EditorKey.Right); // moving drops a style set for typing
        Assert.False(editor.TypingStyle.Bold);
        Valid(editor);
    }

    [Fact]
    public void ChangeKeepsTheSelection()
    {
        var editor = Typed("abc def");
        editor.Select(2, 5);
        editor.Change(b => b with { WidthPt = 125, Alignment = TextAlignment.Center });
        Assert.Equal((2, 3), editor.Range);
        Assert.Equal(125, editor.Box.WidthPt);
    }

    // ----- highlights -----

    [Fact]
    public void HighlightsFollowTheirCharacters()
    {
        var yellow = new TextHighlightRange(0, 0, HighlightHeightMode.Auto, null, new(255, 235, 59), .6);
        var editor = Typed("abcdef");
        editor.Change(b => TextHighlights.Add(b, 2, 2, yellow)); // "cd"
        editor.Select(3, 3); editor.Type("X"); // inside: joins
        Assert.Equal((2, 3), (editor.Box.Highlights[0].Start, editor.Box.Highlights[0].Length));
        editor.Select(2, 2); editor.Type("Y"); // at the start edge: does not join
        Assert.Equal((3, 3), (editor.Box.Highlights[0].Start, editor.Box.Highlights[0].Length));
        editor.Select(6, 6); editor.Type("Z"); // at the end edge: does not join
        Assert.Equal((3, 3), (editor.Box.Highlights[0].Start, editor.Box.Highlights[0].Length));
        editor.Select(3, 5); editor.Key(EditorKey.Delete); // deleting characters shrinks it
        Assert.Equal((3, 1), (editor.Box.Highlights[0].Start, editor.Box.Highlights[0].Length));
        editor.Select(3, 4); editor.Key(EditorKey.Delete);
        Assert.Empty(editor.Box.Highlights);
        Valid(editor);
    }

    // ----- Undo -----

    [Fact]
    public void UndoStepsAreWordsFormatsAndPastesAndStopAtTheStart()
    {
        var editor = Typed("hello world");
        Assert.Equal(2, editor.Depth); // "hello " and "world"
        editor.Key(EditorKey.Backspace); editor.Key(EditorKey.Backspace); editor.Key(EditorKey.Backspace);
        Assert.Equal(3, editor.Depth); // the Backspaces are one step
        editor.SelectAll();
        editor.Format(r => r with { Bold = true });
        var clipboard = new Clipboard { Plain = " pasted" };
        editor.Key(EditorKey.End, EditorModifiers.Control);
        editor.Paste(clipboard);
        Assert.Equal("hello wo pasted", editor.Box.Text);
        Assert.Equal(5, editor.Depth);

        Assert.True(editor.Undo()); Assert.Equal("hello wo", editor.Box.Text);
        Assert.True(editor.Undo()); Assert.False(editor.Box.Runs[0].Bold);
        Assert.Equal((0, 8), editor.Range); // the selection comes back with the step
        Assert.True(editor.Undo()); Assert.Equal("hello world", editor.Box.Text);
        Assert.True(editor.Undo()); Assert.Equal("hello ", editor.Box.Text);
        Assert.True(editor.Undo()); Assert.Equal("", editor.Box.Text);
        Assert.False(editor.Undo()); // nothing more: no document Undo while editing (spec 13.2)
        Assert.True(editor.Redo()); Assert.True(editor.Redo());
        Assert.Equal("hello world", editor.Box.Text);
        Assert.Equal((11, 0), editor.Range); // Redo leaves the caret where the step left it, not the selection Undo came from
        editor.Type("!"); // a new change drops the redo steps
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void MovingTheCaretEndsTheTypingStep()
    {
        var editor = Typed("abc");
        editor.Key(EditorKey.Left);
        editor.Type("x");
        editor.Type("y");
        Assert.Equal("abxyc", editor.Box.Text);
        Assert.Equal(2, editor.Depth);
        editor.Undo();
        Assert.Equal("abc", editor.Box.Text);
    }

    // ----- clipboard -----

    [Fact]
    public void CopyPutsPlainAndFormattedTextAndPasteKeepsTheFormat()
    {
        var bold = Style with { Bold = true, Color = new(190, 30, 60) };
        var source = NewEditor(Box("plain BOLD 😀", 200, Style with { Start = 0, Length = 6 }, bold with { Start = 6, Length = 7 }));
        source.Change(b => TextHighlights.Add(b, 6, 4, new(0, 0, HighlightHeightMode.Manual, 8, new(129, 199, 132), .5)));
        source.Select(3, source.Box.Text.Length);
        var clipboard = new Clipboard();
        Assert.True(source.Copy(clipboard));
        Assert.Equal("in BOLD 😀", clipboard.Plain);
        Assert.Contains("\"bold\":true", clipboard.Formatted);

        var target = NewEditor(Box("[]"));
        target.Select(1, 1);
        Assert.True(target.Paste(clipboard));
        Assert.Equal("[in BOLD 😀]", target.Box.Text);
        Assert.False(target.Box.Runs.Single(r => r.Start <= 1 && r.Start + r.Length > 1).Bold);
        var pastedBold = target.Box.Runs.Single(r => r.Start <= 4 && r.Start + r.Length > 4);
        Assert.Equal(bold with { Start = pastedBold.Start, Length = pastedBold.Length }, pastedBold);
        Assert.Equal((4, 4), (target.Box.Highlights.Single().Start, target.Box.Highlights.Single().Length));
        Assert.Equal(8, target.Box.Highlights.Single().ManualHeightPt);
        Valid(target);

        // Another program's text: plain, in the typing style, line ends normalized.
        var other = NewEditor(Box("x"));
        Assert.True(other.Paste(new Clipboard { Plain = "a\r\nb" }));
        Assert.Equal("xa\nb", other.Box.Text);
        Assert.Single(other.Box.Runs);

        Assert.True(source.Cut(clipboard));
        Assert.Equal("pla", source.Box.Text);
        source.Undo();
        Assert.Equal("plain BOLD 😀", source.Box.Text);
        Assert.False(NewEditor(Box("abc")).Copy(clipboard)); // nothing selected
    }

    // ----- the text bar's session -----

    /// <summary>P4 connects the text bar with an adapter: every member of ITextEditorSession is on the model with its signature.</summary>
    [Fact]
    public void TheModelHasEveryTextBarSessionMember()
    {
        foreach (var member in typeof(PdfNotes.ViewModels.ITextEditorSession).GetMembers())
        {
            var match = member switch
            {
                PropertyInfo p => typeof(TextEditorModel).GetProperty(p.Name) is { } q && q.PropertyType == p.PropertyType,
                MethodInfo { IsSpecialName: true } => true, // property accessors
                MethodInfo m => typeof(TextEditorModel).GetMethod(m.Name, [.. m.GetParameters().Select(x => x.ParameterType)]) is { } n && n.ReturnType == m.ReturnType,
                _ => false,
            };
            Assert.True(match, member.Name);
        }
    }

    /// <summary>Review fix: an unpaired surrogate (allowed by the model validator) neither breaks layout nor word moves.</summary>
    [Fact]
    public void AnUnpairedSurrogateIsJustACharacter()
    {
        string text = "ab\uD83D cd\uDE00";
        var editor = NewEditor(Box(text));
        Assert.Equal(text.Length, editor.Layout.Characters.Length);
        editor.Key(EditorKey.Home, EditorModifiers.Control);
        for (int i = 0; i < 4; i++) editor.Key(EditorKey.Right, EditorModifiers.Control);
        Assert.Equal(text.Length, editor.Caret);
        editor.PointerDown(new(10, 10), clickCount: 2);
        Assert.NotNull(PdfNotes.Text.SkiaText.Engine.Fonts(editor.Box));
    }

    /// <summary>Review fix: while composing, the shown box is the same record until something changes (its layout is cached).</summary>
    [Fact]
    public void TheShownBoxIsReusedWhileComposing()
    {
        var editor = Typed("가");
        editor.SetComposition("나");
        Assert.Same(editor.DisplayBox, editor.DisplayBox);
        var first = editor.DisplayBox;
        editor.SetComposition("낙");
        Assert.NotSame(first, editor.DisplayBox);
    }

    /// <summary>Review fix: U+2028 and U+2029 end their line like a line feed (UAX #14 class BK) and are not drawn.</summary>
    [Fact]
    public void LineAndParagraphSeparatorsEndTheirLines()
    {
        var editor = NewEditor(Box("a\u2028b\u2029"));
        var lines = editor.Layout.Lines;
        Assert.Equal(3, lines.Length);
        Assert.Equal((0, 1), (lines[0].Start, lines[0].End));
        Assert.DoesNotContain(editor.Layout.Characters, c => c.Text is "\u2028" or "\u2029");
        editor.Key(EditorKey.End, EditorModifiers.Control);
        Assert.Equal(lines[2].Top, editor.CaretRect().Y, 6); // the end of the text is on the new last line
    }
}
