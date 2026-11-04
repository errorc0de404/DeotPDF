using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.TextEditor;
using static PdfNotes.Tests.TextEditor.TextEditorModelTests;

namespace PdfNotes.Tests.TextEditor;

/// <summary>
/// Roadmap P2 S4: the text editing of the d06, d06-fixes and d12 Probes, on the model and the document connector
/// (<see cref="TextEditSession"/>) instead of the WPF window. Inputs and expectations are the Probes' except where the
/// new model's rules differ on purpose (noted); results are compared by text, runs and caret offsets, not positions.
/// </summary>
public class TextEditScenarioTests
{
    private static DocumentSession NewDocument() => new(DocumentModel.Create([new(400, 500, 0, PageSource.Generated, PageBackground.Blank)]));

    private static (TextEditorModel Editor, TextEditSession Link) Begin(DocumentSession session, TextBoxAnnotation box, TextRun? typing = null)
    {
        var editor = NewEditor(box, typing);
        return (editor, new TextEditSession(session, session.Current.Pages[0].Id, editor));
    }

    private static void TypeKeys(TextEditorModel editor, string text)
    {
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) editor.Type(e.GetTextElement());
    }

    private static TextBoxAnnotation InDocument(DocumentSession session, Guid id) => (TextBoxAnnotation)session.Current.FindAnnotation(id)!.Annotation;

    /// <summary>d06 "empty box is UI only" and "empty Esc has no history".</summary>
    [Fact]
    public void AnEmptyNewBoxLeavesNoHistory()
    {
        var session = NewDocument();
        var (editor, link) = Begin(session, Box());
        Assert.False(session.IsDirty);
        editor.Type(" ");
        editor.Key(EditorKey.Backspace);
        link.Finish();
        Assert.False(session.IsDirty);
        Assert.Equal(0, session.UndoCount);
        Assert.Empty(session.Current.AllAnnotations);
    }

    /// <summary>d06 Edit(): typing, formats, caret style, plain paste, box change, delete-all and its one Undo.</summary>
    [Fact]
    public void D06Editing()
    {
        var session = NewDocument();
        var box = Box();
        var (editor, link) = Begin(session, box);
        TypeKeys(editor, "한글");
        Assert.Equal(1, session.UndoCount); // d06 "continuous input coalesces"
        TypeKeys(editor, " English 😀");
        editor.Key(EditorKey.Enter);
        TypeKeys(editor, "둘째 줄");
        Assert.Equal("한글 English 😀\n둘째 줄", InDocument(session, box.Id).Text); // "native Unicode and Enter"

        editor.SelectAll();
        editor.Format(r => r with { Underline = true, Strikethrough = true });
        Assert.All(InDocument(session, box.Id).Runs, r => Assert.True(r.Underline && r.Strikethrough)); // "range underline strike"

        editor.Select(0, 2); // the Probe selects the first two characters
        editor.Format(r => r with { Bold = true, SizePt = 24, Color = new(180, 20, 70) });
        var formatted = InDocument(session, box.Id);
        Assert.True(formatted.Runs.Length >= 2 && formatted.Runs[0].Bold && formatted.Runs[0].SizePt == 24 && formatted.Runs[^1].SizePt == 12); // "mixed styles partition exact text"
        Assert.Null(ModelValidator.ValidateAnnotation(formatted, _ => false));

        // "caret style affects future text"
        editor.Select(editor.Box.Text.Length, editor.Box.Text.Length);
        string before = editor.Box.Text;
        editor.Format(r => r with { Bold = false, SizePt = 18 });
        TypeKeys(editor, " 추가");
        var typed = InDocument(session, box.Id);
        Assert.Equal(before + " 추가", typed.Text);
        Assert.Equal(18, typed.Runs[^1].SizePt);
        Assert.Equal(24, typed.Runs[0].SizePt);

        // "plain paste preserves newline emoji"
        editor.Paste(new Clipboard { Plain = "\r\n 붙여넣기 😀" });
        Assert.EndsWith("\n 붙여넣기 😀", InDocument(session, box.Id).Text, StringComparison.Ordinal);
        TypeKeys(editor, " 뒤"); // d06-paste "typing after paste appends"
        Assert.EndsWith("\n 붙여넣기 😀 뒤", InDocument(session, box.Id).Text, StringComparison.Ordinal);

        editor.Change(b => b with { WidthPt = 125, Alignment = TextAlignment.Center, BoxStyle = new(new(240, 210, 100), .3, true, new(30, 70, 160), .7) });
        var changed = InDocument(session, box.Id);
        Assert.Equal((125, TextAlignment.Center), (changed.WidthPt, changed.Alignment));
        link.Finish();

        // "all text deleted removes box" and "delete undo restores full content format location"
        var (again, link2) = Begin(session, changed);
        again.SelectAll();
        again.Key(EditorKey.Backspace);
        Assert.NotNull(session.Current.FindAnnotation(box.Id)); // blank text is not written while editing
        link2.Finish();
        Assert.Null(session.Current.FindAnnotation(box.Id));
        session.Undo();
        Assert.Equal(changed, InDocument(session, box.Id));
    }

    /// <summary>d06-caret: a typing style set at the end of a formatted box.</summary>
    [Fact]
    public void D06Caret()
    {
        var session = NewDocument();
        var (editor, link) = Begin(session, Box());
        TypeKeys(editor, "한글 English");
        editor.SelectAll();
        editor.Format(r => r with { SizePt = 24 });
        editor.Select(editor.Box.Text.Length, editor.Box.Text.Length);
        editor.Format(r => r with { SizePt = 18 });
        TypeKeys(editor, " 추가");
        link.Finish();
        var box = session.Current.AllAnnotations.OfType<TextBoxAnnotation>().Single();
        Assert.Equal("한글 English 추가", box.Text);
        Assert.Equal((24.0, 18.0), (box.Runs[0].SizePt, box.Runs[^1].SizePt));
    }

    /// <summary>
    /// d06-fixes: a style applied to the selected range is one editor step, and Undo while editing takes it back. The
    /// Probe also expected a document step of its own; since 2026-11-03 the whole edit is one document step.
    /// </summary>
    [Fact]
    public void D06FixesStyleIsOneUndoStep()
    {
        var session = NewDocument();
        var (editor, link) = Begin(session, Box());
        TypeKeys(editor, "더블클릭");
        editor.SelectAll();
        int history = session.UndoCount;
        int depth = editor.Depth;
        editor.Format(r => r with { Bold = true, SizePt = 22 }); // the saved style "Slot 1"
        Assert.True(InDocument(session, editor.Box.Id).Runs.All(r => r.Bold && r.SizePt == 22)); // "editor right click slot applies selected range"
        Assert.Equal(depth + 1, editor.Depth);
        editor.Undo(); // Ctrl+Z while editing
        Assert.True(InDocument(session, editor.Box.Id).Runs.All(r => !r.Bold && r.SizePt == 12)); // "context style one undo"
        Assert.Equal(history, session.UndoCount);
        link.Finish();
    }

    /// <summary>d12: editing a selected box starts at the end; a click puts the caret at the clicked character.</summary>
    [Fact]
    public void D12CaretPlacement()
    {
        var session = NewDocument();
        var box = Box("텍스트 상자");
        session.Execute(DocumentEdits.AddAnnotation(session.Current, session.Current.Pages[0].Id, box));
        var (editor, link) = Begin(session, box);
        editor.Type("a"); // "typing with the box selected starts editing at the end"
        Assert.Equal("텍스트 상자a", InDocument(session, box.Id).Text);
        editor.Undo();
        var c = editor.Layout.Characters.Single(x => x.Text == "상");
        editor.PointerDown(new(c.Bounds.X + c.Bounds.Width * .25, c.Bounds.Y + c.Bounds.Height / 2));
        editor.PointerUp();
        editor.Type("X"); // "the caret was at the clicked character"
        Assert.Equal("텍스트 X상자", InDocument(session, box.Id).Text);
        link.Finish();
        Assert.Equal("텍스트 X상자", InDocument(session, box.Id).Text);
    }

    /// <summary>
    /// 2026-11-03 (spec 13.2): Undo while editing goes word by word in the box; after editing, one document Undo takes
    /// back the whole edit — a new box goes away, an edited box comes back as it was.
    /// </summary>
    [Fact]
    public void OneEditIsOneDocumentStep()
    {
        var session = NewDocument();
        var box = Box();
        var (editor, link) = Begin(session, box);
        TypeKeys(editor, "one two");
        editor.SelectAll();
        editor.Format(r => r with { Bold = true });
        Assert.Equal(3, editor.Depth); // "one ", "two", bold
        Assert.Equal(1, session.UndoCount);

        editor.Undo();
        Assert.False(InDocument(session, box.Id).Runs[0].Bold); // the document follows inside the same step
        editor.Undo();
        Assert.Equal("one ", InDocument(session, box.Id).Text);
        editor.Redo();
        TypeKeys(editor, "!");
        Assert.Equal("one two!", InDocument(session, box.Id).Text);
        Assert.Equal(1, session.UndoCount);
        link.Finish();
        session.Undo();
        Assert.Null(session.Current.FindAnnotation(box.Id)); // the new box goes as a whole
        session.Redo();
        var written = InDocument(session, box.Id);
        Assert.Equal("one two!", written.Text);

        // Editing it again: one step that brings back "one two!".
        var (again, link2) = Begin(session, written);
        TypeKeys(again, " three four");
        again.Key(EditorKey.Home, EditorModifiers.Control);
        TypeKeys(again, "zero ");
        link2.Finish();
        Assert.Equal("zero one two! three four", InDocument(session, box.Id).Text);
        Assert.Equal(2, session.UndoCount);
        session.Undo();
        Assert.Equal(written, InDocument(session, box.Id));
    }

    /// <summary>An edit that ends where it began leaves no document step: a new box emptied again, a change undone.</summary>
    [Fact]
    public void AnEditThatChangesNothingLeavesNoStep()
    {
        var session = NewDocument();
        var box = Box();
        var (editor, link) = Begin(session, box);
        editor.Type("a");
        editor.Key(EditorKey.Backspace);
        editor.Type("b");
        Assert.Equal("b", InDocument(session, box.Id).Text);
        editor.Undo();
        Assert.Equal("", editor.Box.Text);
        editor.Undo();
        Assert.Equal("a", InDocument(session, box.Id).Text);
        editor.Redo();
        link.Finish(); // ends blank: the new box never was
        Assert.Null(session.Current.FindAnnotation(box.Id));
        Assert.Equal(0, session.UndoCount);

        var kept = Box("keep");
        session.Execute(DocumentEdits.AddAnnotation(session.Current, session.Current.Pages[0].Id, kept));
        int count = session.UndoCount;
        var (other, link2) = Begin(session, kept);
        other.Type("!");
        other.Undo();
        link2.Finish();
        Assert.Equal(count, session.UndoCount);
        Assert.Equal("keep", InDocument(session, kept.Id).Text);
    }

    /// <summary>
    /// Review fix (P2 S8 check): a save in the middle of an edit ends the document's coalescing, so the edit becomes two
    /// steps. An edit that ends where it began still leaves the document as before, not halfway.
    /// </summary>
    [Fact]
    public void ASaveInTheMiddleOfAnEditDoesNotLeaveItHalfDone()
    {
        var session = NewDocument();
        var box = Box("abc");
        session.Execute(DocumentEdits.AddAnnotation(session.Current, session.Current.Pages[0].Id, box));
        var (editor, link) = Begin(session, box);
        editor.Type("d");
        session.MarkSaved(session.CreateSnapshot()); // Ctrl+S while editing
        editor.Key(EditorKey.Backspace);
        link.Finish();
        Assert.Equal("abc", InDocument(session, box.Id).Text);

        var fresh = Box();
        var (other, link2) = Begin(session, fresh);
        other.Type("xyz");
        session.MarkSaved(session.CreateSnapshot());
        other.SelectAll();
        other.Key(EditorKey.Backspace);
        link2.Finish();
        Assert.Null(session.Current.FindAnnotation(fresh.Id)); // the emptied new box does not come back
    }

    /// <summary>Review fix: a box holds at most ModelLimits.MaxTextLength characters; a longer paste is cut, not dropped silently later.</summary>
    [Fact]
    public void APasteBeyondTheTextLimitIsCutToFit()
    {
        var session = NewDocument();
        var (editor, link) = Begin(session, Box("ab"));
        editor.Paste(new Clipboard { Plain = new string('x', ModelLimits.MaxTextLength + 10) });
        Assert.Equal(ModelLimits.MaxTextLength, editor.Box.Text.Length);
        Assert.Equal(ModelLimits.MaxTextLength, InDocument(session, editor.Box.Id).Text.Length); // and it reached the document
        editor.Type("y");
        Assert.Equal(ModelLimits.MaxTextLength, editor.Box.Text.Length);
        link.Finish();
    }
}
