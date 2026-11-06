using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Tests.Core;
using PdfNotes.ViewModels;

namespace PdfNotes.Tests.App;

/// <summary>P1 V1: the main window's title, status line and undo tips come from the view model, without the WPF shell.</summary>
public sealed class MainViewModelTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    private readonly Shell shell = new();
    private readonly MainViewModel model;

    public MainViewModelTests()
    {
        Directory.CreateDirectory(folder);
        model = new MainViewModel(shell, shell, new AppSettings(), "DeotPDF", _ => { }, TestFolders.New());
        model.PropertyChanged += (_, e) => shell.Log.Add("changed:" + e.PropertyName);
        model.Workflow.Replace(OpenDocument.CreateNote(NewPageSpec.Default));
    }

    /// <summary>R3f: the view model shows the texts it is given, not the process-wide language.</summary>
    [Fact]
    public void TheViewModelUsesTheTextsItIsGiven()
    {
        var english = Texts.From(new LanguagePack("en", "English", 1, new Dictionary<string, string> { ["ribbon.undoTip"] = "Undo (Ctrl+Z)" }, null));
        var other = new MainViewModel(shell, shell, new AppSettings(), "DeotPDF", _ => { }, TestFolders.New(), texts: english);
        Assert.Equal("Undo (Ctrl+Z)", other.UndoTip);
        Assert.Equal(Texts.Korean.Get("ribbon.redoTip"), other.RedoTip); // a key the pack lacks is Korean
        Assert.NotEqual(other.UndoTip, model.UndoTip);
    }

    public void Dispose()
    {
        model.Workflow.Detach()?.Dispose();
        foreach (var d in shell.Released) d.Dispose();
        Directory.Delete(folder, true);
    }

    private sealed class Shell : IMainView, IEditorDialogs
    {
        public readonly List<string> Log = [];
        public readonly List<OpenDocument> Released = [];
        public Func<SavePathRequest, string?> SavePath = _ => null;

        public void FinishTextEditing() { }
        public void CancelTool() { }
        public void DocumentLeaving(OpenDocument? previous) { }
        public void DocumentReplaced(OpenDocument? previous, OpenDocument next) { if (previous is not null) Released.Add(previous); Log.Add("replaced"); }
        public void DocumentChanged() => Log.Add("documentChanged");
        public void WaitForFirstPages() { }
        public void Opened(string path) => Log.Add("opened");
        public void Saved(string path) => Log.Add("saved");
        public bool ShowOpenElsewhere(string path) => false;
        public void CommandsChanged() => Log.Add("commands");
        public void OnUiThread(Action action) => action();
        public IDocumentViewport Viewport { get; } = new FakeViewport();
        public IPageListView Pages { get; } = new FakePageList();
        public IWindowCoordinator Window { get; } = new FakeWindow();
        public Answer Ask(string text, AskButtons buttons, NoticeKind kind) => Answer.Cancel;
        public void Tell(string text, NoticeKind kind) => Log.Add("tell:" + kind);
        public string? ChooseSavePath(SavePathRequest request) => SavePath(request);
    }

    private void Draw() =>
        model.Document!.Session.Execute(DocumentEdits.AddAnnotation(model.Document.Session.Current, model.Document.Session.Current.Pages[0].Id, TestModels.Ink()));

    [Fact]
    public void TitleFollowsTheUnsavedMarkAndUndoTipsNameTheStep()
    {
        string name = model.Document!.DisplayNameIn(model.Texts);
        Assert.Equal($"{name} - DeotPDF", model.Title);
        Assert.Equal(Strings.Get("ribbon.undoTip"), model.UndoTip);

        shell.Log.Clear();
        Draw();
        Assert.Equal($"{name} * - DeotPDF", model.Title);
        Assert.Equal(Strings.Format("ribbon.undoNamed", ("name", Strings.Get(model.Document.Session.UndoLabel!))), model.UndoTip);
        Assert.Equal(Strings.Get("ribbon.redoTip"), model.RedoTip);
        // The view updates its controls first, then the title changes, then commands are asked again.
        Assert.Equal(["documentChanged", "changed:Title", "changed:UndoTip", "commands"], shell.Log);

        string label = model.Document.Session.UndoLabel!;
        model.Document.Session.Undo();
        Assert.Equal($"{name} - DeotPDF", model.Title);
        Assert.Equal(Strings.Get("ribbon.undoTip"), model.UndoTip);
        Assert.Equal(Strings.Format("ribbon.redoNamed", ("name", Strings.Get(label))), model.RedoTip);
    }

    [Fact]
    public async Task SavingShowsBusyStatusAndTheNewTitle()
    {
        Draw();
        string path = Path.Combine(folder, "note.pdf");
        shell.SavePath = _ => path;
        var busy = new List<bool>();
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsBusy)) busy.Add(model.IsBusy); };

        Assert.True(await model.Workflow.SaveAsync(saveAs: false));
        Assert.Equal([true, false], busy);
        Assert.Equal(Strings.Get("status.saved"), model.Status);
        Assert.Equal("note.pdf - DeotPDF", model.Title);
        Assert.Contains("saved", shell.Log);
    }

    [Fact]
    public async Task ANewNoteReplacesTheDocumentAndClearsTheStatus()
    {
        model.Status = "something";
        var first = model.Document;
        Assert.True(await model.Workflow.CreateNoteAsync(NewPageSpec.Default));
        Assert.NotSame(first, model.Document);
        Assert.Equal("", model.Status);
        Assert.Contains(first!, shell.Released);
        // The old document's session no longer reaches the view model.
        shell.Log.Clear();
        first!.Session.Execute(DocumentEdits.AddAnnotation(first.Session.Current, first.Session.Current.Pages[0].Id, TestModels.Ink()));
        Assert.DoesNotContain("documentChanged", shell.Log);
    }
}
