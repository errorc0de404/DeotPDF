using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Platform;

namespace PdfNotes.Tests.App;

/// <summary>P1 V8: selection commands and the canvas menu, the text bar on selected boxes and through the editor session, and the sticky note card.</summary>
public sealed class SelectionTextViewModelTests : IDisposable
{
    private readonly Shell shell = new();
    private readonly AppSettings settings = new();
    private readonly FakeClipboard clipboard = new();
    private readonly MainViewModel model;
    private readonly SelectTool select;
    private int saves;
    private DocumentSession Session => model.Document!.Session;
    private Guid PageId => Session.Current.Pages[0].Id;

    public SelectionTextViewModelTests()
    {
        model = new MainViewModel(shell, shell, settings, "DeotPDF", _ => saves++, TestFolders.New(), new ManualTime(), clipboard: clipboard);
        model.Workflow.Replace(OpenDocument.CreateNote(NewPageSpec.Default));
        select = new SelectTool(new FakeCanvas(), () => model.Document?.Session);
        model.ToolStyles.Attach(select);
        model.Selection.Attach(select, _ => { });
        model.TextStyle.Attach(shell.Editor, select);
    }

    public void Dispose()
    {
        model.Workflow.Detach()?.Dispose();
        foreach (var d in shell.Released) d.Dispose();
    }

    private sealed class FakeCanvas : ICanvasView
    {
        public PageAnchor? HitTestPage(Point2 viewportPoint) => null;
        public ViewTransform PageTransform(int index) => throw new NotSupportedException();
        public double DpiScale => 1;
        public Point2 Offset => default;
        public void SetHorizontalOffset(double value) { }
        public void SetVerticalOffset(double value) { }
        public void SetPreview(int pageIndex, ICanvasPreview? preview, bool mark = false) { }
        public void RefreshPreview() { }
        public void RefreshOverlays() { }
        public void RefreshOverlay(int pageIndex) { }
    }

    /// <summary>An editor session over a box held in memory (the WPF window's RichTextBox in the app).</summary>
    private sealed class FakeEditor : ITextEditorSession
    {
        public TextBoxAnnotation? Editing;
        public (int Start, int Length) Selected;
        public TextRun Typing = TextEditing.Default;
        public int Completed;
        public bool IsEditing => Editing is not null;
        public TextBoxAnnotation Box => Editing!;
        public (int Start, int Length) Range => Selected;
        public TextRun TypingStyle => Typing;
        public void CompleteInput() => Completed++;
        public void Format(Func<TextRun, TextRun> change)
        {
            if (Selected.Length > 0) Editing = TextEditing.Format(Editing!, Selected.Start, Selected.Length, change);
            else Typing = change(Typing);
        }
        public void Change(Func<TextBoxAnnotation, TextBoxAnnotation> change) => Editing = change(Editing!);
    }

    private sealed class Shell : IMainView, IEditorDialogs
    {
        public readonly List<OpenDocument> Released = [];
        public readonly FakeToolView ToolView = new();
        public readonly FakeEditor Editor = new();
        public readonly List<string> Told = [];
        public void FinishTextEditing() { }
        public void CancelTool() { }
        public void DocumentLeaving(OpenDocument? previous) { }
        public void DocumentReplaced(OpenDocument? previous, OpenDocument next) { if (previous is not null) Released.Add(previous); }
        public void DocumentChanged() { }
        public void WaitForFirstPages() { }
        public void Opened(string path) { }
        public void Saved(string path) { }
        public bool ShowOpenElsewhere(string path) => false;
        public void CommandsChanged() { }
        public void OnUiThread(Action action) => action();
        public IDocumentViewport Viewport { get; } = new FakeViewport();
        public IPageListView Pages { get; } = new FakePageList();
        public IWindowCoordinator Window { get; } = new FakeWindow();
        public IToolView Tools => ToolView;
        public Answer Ask(string text, AskButtons buttons, NoticeKind kind) => Answer.No;
        public void Tell(string text, NoticeKind kind) => Told.Add(text);
        public string? ChooseSavePath(SavePathRequest request) => null;
    }

    private T Add<T>(T annotation) where T : Annotation
    {
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, PageId, annotation));
        return annotation;
    }

    private static InkStroke Ink(bool locked = false) => new(Guid.NewGuid(), locked, Affine.Identity, [new(10, 10), new(80, 60)], 2, RgbColor.Black, 1);

    private static TextBoxAnnotation Box(string text) => new(Guid.NewGuid(), false, Affine.Translation(50, 50), text,
        [TextEditing.Default with { Start = 0, Length = text.Length }], TextAlignment.Left, 180, TextBoxStyle.Default, []);

    [Fact]
    public void CopyPasteAndDeleteGoThroughTheClipboardAndTheSession()
    {
        var ink = Add(Ink());
        Assert.False(model.Selection.CanExecute(SelectionCommand.Copy));
        select.Select(ink.Id);
        Assert.True(model.Selection.CanExecute(SelectionCommand.Cut));
        Assert.True(model.Selection.Copy());
        Assert.True(model.Selection.CanPaste);

        var pasted = model.Selection.Paste(0, null);
        Assert.Equal(PasteKind.Annotations, pasted.Kind);
        Assert.Equal(2, Session.Current.Pages[0].Annotations.Count);
        Assert.DoesNotContain(ink.Id, select.SelectedIds); // the copy is selected, with the selection tool
        Assert.Equal(ToolKind.Select, model.Tools.Current);

        model.Selection.Delete();
        Assert.Single(Session.Current.Pages[0].Annotations);
        Assert.Equal(1, shell.ToolView.Returns); // an automatic selection visit goes back to its tool

        clipboard.SetData(AnnotationTransfer.Format, "{not json");
        Assert.Equal(PasteKind.None, model.Selection.Paste(0, null).Kind);
        Assert.Single(shell.Told); // a broken packet is reported, nothing pasted
    }

    [Fact]
    public void SelectAllTakesTheUnlockedAnnotationsOfTheCurrentPage()
    {
        var a = Add(Ink()); var b = Add(Ink()); Add(Ink(locked: true));
        model.Selection.SelectAll();
        Assert.Equal(ToolKind.Select, model.Tools.Current);
        Assert.Equal([a.Id, b.Id], select.SelectedIds.OrderBy(i => Session.Current.Pages[0].Annotations.Select(x => x.Id).ToList().IndexOf(i)));
    }

    [Fact]
    public void TheCanvasMenuActsOnALockedAnnotationAloneAndOrdersTheSelection()
    {
        var locked = Add(Ink(locked: true));
        var menu = model.Selection.Menu(0, locked.Id, onTextSelection: false)!;
        Assert.Equal([locked.Id], menu.Targets);
        var unlock = Assert.Single(menu.Entries, e => e.Command == AnnotationCommand.Unlock);
        Assert.False(menu.Entries.Single(e => e.Command == AnnotationCommand.Delete).Enabled);
        Assert.True(model.Selection.Run(menu, unlock));
        Assert.False(Session.Current.FindAnnotation(locked.Id)!.Annotation.Locked);

        var top = Add(Ink());
        menu = model.Selection.Menu(0, top.Id, false)!;
        Assert.Equal([top.Id], select.SelectedIds); // a right-click outside the selection selects the annotation
        model.Selection.Run(menu, menu.Entries.Single(e => e.Command == AnnotationCommand.ToBack));
        Assert.Equal(top.Id, Session.Current.Pages[0].Annotations[0].Id);

        menu = model.Selection.Menu(0, null, false)!;
        Assert.Contains(menu.Entries, e => e.Command == AnnotationCommand.Tool && e.Tool == ToolKind.Select && e.Checked);
        Assert.False(model.Selection.Run(menu, menu.Entries.Single(e => e.Command == AnnotationCommand.AddTextBox))); // the view's command
    }

    [Fact]
    public void TheTextBarFormatsSelectedBoxesInOneUndoAndRemembersTheBoxStyle()
    {
        var first = Add(Box("abc")); var second = Add(Box("de"));
        model.Tools.Choose(ToolKind.Select, notesHidden: false);
        select.SetSelection([first.Id, second.Id]);
        Assert.True(model.TextStyle.Bar is { Visible: true, Enabled: true, Target: TextColorTarget.Text, TransparencyEnabled: false });

        int undo = Session.UndoCount;
        model.TextStyle.SetFlag('B', true);
        Assert.Equal(undo + 1, Session.UndoCount);
        Assert.All(Session.Current.Pages[0].Annotations.OfType<TextBoxAnnotation>(), b => Assert.All(b.Runs, r => Assert.True(r.Bold)));

        model.TextStyle.SetColorTarget(TextColorTarget.Fill);
        Assert.Null(model.TextStyle.TargetColor); // no fill yet
        model.TextStyle.SetColor(new RgbColor(10, 120, 200));
        var filled = (TextBoxAnnotation)Session.Current.FindAnnotation(first.Id)!.Annotation;
        Assert.Equal(1, filled.BoxStyle.BackgroundOpacity); // choosing a fill color turns it on, opaque
        Assert.Equal(new RgbColor(10, 120, 200), model.TextStyle.NewBoxStyle.BackgroundColor); // the next new box starts with it
        Assert.Contains(new RgbColor(10, 120, 200), model.ToolStyles.UserColors);

        model.TextStyle.SetColorTarget(TextColorTarget.Border);
        model.TextStyle.SetTransparency(100);
        Assert.False(((TextBoxAnnotation)Session.Current.FindAnnotation(second.Id)!.Annotation).BoxStyle.BorderEnabled);
        Assert.Equal("100", model.TextStyle.Bar.Transparency);
        Assert.True(saves > 0);
    }

    [Fact]
    public void WhileEditingTheBarWorksThroughTheEditorSession()
    {
        var editor = shell.Editor;
        editor.Editing = Box("hello");
        editor.Selected = (0, 5);
        Assert.True(model.TextStyle.Bar.Enabled);

        int undo = Session.UndoCount;
        model.TextStyle.SetSize("24");
        Assert.Equal(24, editor.Editing.Runs[0].SizePt);
        Assert.Equal(undo, Session.UndoCount); // the editor writes the document itself
        Assert.True(editor.Completed > 0); // an IME composition is finished first

        model.TextStyle.ToggleHighlight();
        Assert.True(model.TextStyle.Bar.Highlighted);
        model.TextStyle.ToggleHighlight();
        Assert.False(model.TextStyle.Bar.Highlighted);

        editor.Selected = (5, 0);
        model.TextStyle.ToggleFlag('I');
        Assert.True(editor.Typing.Italic); // no selection: the next typed characters
        Assert.True(model.TextStyle.CurrentStyle.Italic);

        model.TextStyle.SaveStyle(2);
        Assert.Equal(editor.Typing with { Start = 0, Length = 0 }, settings.TextSlots[2]);
        editor.Selected = (0, 5);
        model.TextStyle.ApplySlot(2);
        Assert.All(editor.Editing.Runs, r => Assert.True(r.Italic));
    }

    [Fact]
    public void ANoteIsAddedOnlyWithTextAndAClearedOneIsDeleted()
    {
        var blank = model.NoteCard.Create(0, new Point2(100, 100))!;
        Assert.True(model.NoteCard.Open(blank, PageId, asNew: true));
        Assert.Null(model.NoteCard.Close("  "));
        Assert.Empty(Session.Current.Pages[0].Annotations);

        var note = model.NoteCard.Create(0, new Point2(100, 100))!;
        model.NoteCard.Open(note, PageId, asNew: true);
        Assert.Equal(note.Id, model.NoteCard.Close("회의\r\n메모"));
        var added = Assert.IsType<StickyNote>(Assert.Single(Session.Current.Pages[0].Annotations));
        Assert.Equal("회의\n메모", added.Text);
        Assert.Equal("undo.addNote", Session.UndoLabel);

        model.NoteCard.Open(added, PageId, asNew: false);
        model.NoteCard.Close("");
        Assert.Empty(Session.Current.Pages[0].Annotations);
        Assert.Equal("undo.deleteNote", Session.UndoLabel);

        model.Display.NotesHidden = true;
        Assert.Null(model.NoteCard.Create(0, new Point2(10, 10)));
        Assert.False(model.NoteCard.Open(note, PageId, asNew: true));
        Assert.Null(model.NoteCard.Note);
    }
}
