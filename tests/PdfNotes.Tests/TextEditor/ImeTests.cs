using System.IO;
using System.Text;
using PdfNotes.Core.Models;
using PdfNotes.TextEditor;
using static PdfNotes.Tests.TextEditor.TextEditorModelTests;

namespace PdfNotes.Tests.TextEditor;

/// <summary>
/// Roadmap P2 S5: IME composition in the editor model. The recorded input of the 1.1 IME trial (real keyboard, Windows
/// 11 Korean and Japanese IME into an Avalonia control, <c>ImeLogs/</c>) is replayed event by event.
/// </summary>
public class ImeTests
{
    private static string Log(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PdfNotes.slnx"))) return Path.Combine(dir.FullName, "tests", "PdfNotes.Tests", "TextEditor", "ImeLogs", name);
        throw new DirectoryNotFoundException("PdfNotes.slnx not found above the test folder.");
    }

    /// <summary>
    /// Replays a log: <c>preedit</c> → <see cref="TextEditorModel.SetComposition"/> (with the IME cursor),
    /// <c>commit</c> → <see cref="TextEditorModel.CommitComposition"/>, <c>key Back/Return</c> → <see cref="TextEditorModel.Key"/>.
    /// Text comes from the logged code points, not the quoted text. Returns the number of events.
    /// </summary>
    private static int Replay(TextEditorModel editor, string log)
    {
        int events = 0;
        foreach (string line in File.ReadLines(Log(log)))
        {
            var parts = line.Split(' ', 3);
            if (parts.Length < 3) continue;
            string kind = parts[1], rest = parts[2];
            string Text()
            {
                int open = rest.IndexOf('['), close = rest.IndexOf(']');
                var sb = new StringBuilder();
                foreach (string code in rest[(open + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    sb.Append(char.ConvertFromUtf32(Convert.ToInt32(code[2..], 16)));
                return sb.ToString();
            }
            switch (kind)
            {
                case "preedit":
                    string cursor = rest[(rest.LastIndexOf("cursor ", StringComparison.Ordinal) + 7)..];
                    editor.SetComposition(Text(), cursor == "-" ? null : int.Parse(cursor));
                    break;
                case "commit":
                    editor.CommitComposition(Text());
                    break;
                case "key":
                    editor.Key(rest.StartsWith("Back", StringComparison.Ordinal) ? EditorKey.Backspace : EditorKey.Enter);
                    break;
                default: continue;
            }
            events++;
        }
        return events;
    }

    /// <summary>
    /// The trial's main log: Korean, English, Japanese conversion, Backspace and Enter. In the trial the Backspace that
    /// emptied the "ㄴ" composition came again as a key and deleted the 안 before it, so the tester typed 안 again; the
    /// model drops that key, so 안 stays and the retyped one follows it. Everything else ends as the trial's final text.
    /// </summary>
    [Fact]
    public void TheTrialLogReplaysToItsTextWithoutTheDoubleBackspace()
    {
        var editor = NewEditor(Box());
        Assert.True(Replay(editor, "ime-log.txt") > 150);
        Assert.False(editor.IsComposing);
        Assert.Equal("테스트\n한국어\nThis is ENglish test\n日本語の文章です\ntest\n\n안안녕\n\n123145!@#%$!(#@(#!(#", editor.Box.Text);
        Assert.Null(ModelValidator.ValidateAnnotation(editor.Box, _ => false));
    }

    /// <summary>The first run logged no keys: its compositions and commits alone give the committed characters in order.</summary>
    [Fact]
    public void TheFirstRunsCompositionsCommitInOrder()
    {
        var editor = NewEditor(Box());
        Replay(editor, "ime-log-run1.txt");
        Assert.Equal("ddd안녕 123aThis is a test ", editor.Box.Text);
    }

    [Fact]
    public void ACompositionIsShownButNotInTheBoxOrTheHistory()
    {
        var editor = Typed("가나");
        int depth = editor.Depth;
        int changed = 0;
        editor.CompositionChanged += (_, _) => changed++;
        editor.SetComposition("ㅎ");
        editor.SetComposition("하");
        editor.SetComposition("한");
        Assert.Equal("가나", editor.Box.Text);
        Assert.Equal(depth, editor.Depth);
        Assert.Equal("가나한", editor.DisplayBox.Text);
        Assert.Equal((2, 1), editor.CompositionRange);
        Assert.Equal(3, changed);
        // The candidate window goes to the end of the composition, after the caret.
        Assert.True(editor.CompositionCaretRect().X > editor.CaretRect().X);
        editor.SetComposition("");
        editor.CommitComposition("한");
        Assert.Equal("가나한", editor.Box.Text);
        Assert.Equal(depth, editor.Depth); // committed text joins the typing step ("확정 글로 센다")
    }

    [Fact]
    public void JapaneseConversionKeepsTheImeCursor()
    {
        var editor = NewEditor(Box());
        editor.SetComposition("にほｎ", 3);
        Assert.Equal(3, editor.CompositionCursor);
        editor.SetComposition("日本", 2);
        var atCursor = editor.CompositionCaretRect();
        editor.SetComposition("日本語", 2);
        Assert.Equal(atCursor.X, editor.CompositionCaretRect().X, 6); // the cursor, not the end
        editor.SetComposition("");
        editor.CommitComposition("日本語");
        Assert.Equal("日本語", editor.Box.Text);
    }

    [Fact]
    public void BackspaceAfterAnEmptiedCompositionIsDroppedOnce()
    {
        var editor = Typed("안");
        editor.SetComposition("ㄴ");
        editor.SetComposition("");      // the IME's Backspace emptied it
        editor.Key(EditorKey.Backspace); // …and arrives again as a key
        Assert.Equal("안", editor.Box.Text);
        editor.Key(EditorKey.Backspace); // a real Backspace afterwards works
        Assert.Equal("", editor.Box.Text);

        // A commit in between (the usual end of a syllable) clears the guard: the next Backspace deletes.
        editor = Typed("가");
        editor.SetComposition("나");
        editor.SetComposition("");
        editor.CommitComposition("나");
        editor.Key(EditorKey.Backspace);
        Assert.Equal("가", editor.Box.Text);
    }

    [Fact]
    public void KeysAndCommandsCommitTheCompositionFirst()
    {
        var editor = Typed("가");
        editor.SetComposition("한");
        editor.Key(EditorKey.Enter); // Enter: commit, then a new line
        Assert.Equal("가한\n", editor.Box.Text);

        editor.SetComposition("글");
        editor.Key(EditorKey.Left); // an arrow: commit, then move
        Assert.Equal("가한\n글", editor.Box.Text);
        Assert.Equal(3, editor.Caret);

        editor.SetComposition("ㅁ");
        editor.Key(EditorKey.Backspace); // while composing, Backspace is the IME's
        Assert.Equal("ㅁ", editor.Composition);
        editor.Format(r => r with { Bold = true }); // a format change commits first (spec 6.2)
        Assert.Equal("가한\nㅁ글", editor.Box.Text);
        Assert.False(editor.IsComposing);

        editor.SetComposition("ㅂ");
        editor.PointerDown(new(0, 0)); // a click commits first
        Assert.Equal("가한\nㅁㅂ글", editor.Box.Text);

        editor.SetComposition("ㅅ");
        editor.CompleteInput();
        Assert.False(editor.IsComposing);
        Assert.Equal("ㅅ가한\nㅁㅂ글", editor.Box.Text);

        editor.SetComposition("ㅇ");
        editor.CancelComposition(); // the IME dropped it
        Assert.Equal("ㅅ가한\nㅁㅂ글", editor.Box.Text);
    }

    [Fact]
    public void ComposingOverASelectionReplacesItInOneStep()
    {
        var editor = Typed("abc def");
        int depth = editor.Depth;
        editor.Select(4, 7);
        editor.SetComposition("한");
        Assert.Equal("abc ", editor.Box.Text);  // the selection goes when the composition starts
        Assert.Equal("abc 한", editor.DisplayBox.Text);
        editor.SetComposition("");
        editor.CommitComposition("한");
        editor.SetComposition("글");
        editor.SetComposition("");
        editor.CommitComposition("글");
        Assert.Equal("abc 한글", editor.Box.Text);
        Assert.Equal(depth + 1, editor.Depth);
        editor.Undo();
        Assert.Equal("abc def", editor.Box.Text);
    }

    [Fact]
    public void TheDocumentSeesOnlyCommittedText()
    {
        var session = new PdfNotes.Core.Commands.DocumentSession(DocumentModel.Create([new(400, 500, 0, PageSource.Generated, PageBackground.Blank)]));
        var editor = NewEditor(Box());
        using var link = new TextEditSession(session, session.Current.Pages[0].Id, editor);
        editor.SetComposition("ㅎ");
        editor.SetComposition("하");
        Assert.Empty(session.Current.AllAnnotations);
        editor.SetComposition("");
        editor.CommitComposition("하");
        editor.SetComposition("ㄴ");
        Assert.Equal("하", ((TextBoxAnnotation)session.Current.AllAnnotations.Single()).Text);
        link.Finish(); // ending the edit completes the composition
        Assert.Equal("하ㄴ", ((TextBoxAnnotation)session.Current.AllAnnotations.Single()).Text);
        Assert.Equal(1, session.UndoCount);
    }
}
