using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Session;
using PdfNotes.Pdf.Writing;
using PdfNotes.Tests.Core;

namespace PdfNotes.Tests.App;

/// <summary>
/// Roadmap P5b: documents that are not files (Android content URIs) open and save through <see cref="DocumentLocation"/>,
/// with the questions asked asynchronously; one the app may only read asks where to save.
/// </summary>
public sealed class ContentLocationTests : IDisposable
{
    /// <summary>A document held in memory, like a content URI: no path, a name and an address of its own.</summary>
    private sealed class MemoryLocation(string name, byte[] content, bool canReplace = true) : DocumentLocation
    {
        public byte[] Content = content;
        public int Writes;
        public bool FailNextWrite;
        public override string Name => name;
        public override string Key => "memory:" + name;
        public override string Address => "content://test/" + name;
        public override bool CanReplace => canReplace;
        public override byte[] ReadAll() => Content;

        public override void Replace(byte[] content, Action<byte[]> verifyWritten)
        {
            if (FailNextWrite) { FailNextWrite = false; throw new SaveFailedException(SaveFailure.DiskFull, "no space"); }
            verifyWritten(content);
            Content = content;
            Writes++;
        }

        public override FileBaseline? Stamp() => new(Content.Length, new DateTime(2026, 11, 7, 0, 0, Writes, DateTimeKind.Utc));
    }

    private sealed class Shell : IDocumentHost, IEditorDialogs
    {
        public DocumentWorkflow? Workflow;
        public readonly List<string> Log = [];
        public readonly List<OpenDocument> Released = [];
        public Queue<Answer> Answers = new();
        public Func<SavePathRequest, DocumentLocation?> SaveLocation = _ => null;

        public void FinishTextEditing() { }
        public void CancelTool() { }
        public void DocumentLeaving(OpenDocument? previous) { }
        public void DocumentReplaced(OpenDocument? previous, OpenDocument next) { if (previous is not null) Released.Add(previous); }
        public void SetStatus(string text) { }
        public void StateChanged() { }
        public void Opened(string path) => Log.Add("opened:" + path);
        public void Saved(string path) => Log.Add("saved:" + path);
        public bool ShowOpenElsewhere(string path) { Log.Add("elsewhere:" + path); return false; }
        public Answer Ask(string text, AskButtons buttons, NoticeKind kind) => throw new InvalidOperationException("Android asks asynchronously.");
        public Task<Answer> AskAsync(string text, AskButtons buttons, NoticeKind kind) { Log.Add("ask:" + buttons); return Task.FromResult(Answers.Dequeue()); }
        public void Tell(string text, NoticeKind kind) => Log.Add("tell:" + kind);
        public string? ChooseSavePath(SavePathRequest request) => throw new InvalidOperationException("Android chooses a location.");
        public Task<DocumentLocation?> ChooseSaveLocationAsync(SavePathRequest request) { Log.Add("choose:" + request.SuggestedName); return Task.FromResult(SaveLocation(request)); }
    }

    private readonly Shell host = new();
    private readonly DocumentWorkflow workflow;

    public ContentLocationTests()
    {
        workflow = new DocumentWorkflow(host, host, Texts.Korean);
        host.Workflow = workflow;
        workflow.Replace(OpenDocument.CreateNote(NewPageSpec.Default));
    }

    public void Dispose()
    {
        workflow.Detach()?.Dispose();
        foreach (var d in host.Released) d.Dispose();
    }

    /// <summary>A PDF the app saved, as bytes.</summary>
    private static async Task<byte[]> SavedPdfAsync(int strokes)
    {
        string file = Path.Combine(Path.GetTempPath(), $"PdfNotes-location-{Guid.NewGuid():N}.pdf");
        try
        {
            using var doc = OpenDocument.CreateNote(NewPageSpec.Default);
            for (int i = 0; i < strokes; i++) Draw(doc);
            await doc.SaveAsync(file);
            return await File.ReadAllBytesAsync(file);
        }
        finally { File.Delete(file); }
    }

    private static void Draw(OpenDocument doc) =>
        doc.Session.Execute(DocumentEdits.AddAnnotation(doc.Session.Current, doc.Session.Current.Pages[0].Id, TestModels.Ink()));

    [Fact]
    public async Task ADocumentThatIsNotAFileOpensAndSavesInPlace()
    {
        var location = new MemoryLocation("lecture.pdf", await SavedPdfAsync(strokes: 1));
        Assert.True(await workflow.OpenAsync(location));
        var doc = workflow.Document!;
        Assert.Same(location, doc.Location);
        Assert.Null(doc.FilePath);
        Assert.Equal("lecture.pdf", doc.DisplayNameIn(Texts.Korean));
        Assert.Single(doc.Session.Current.Pages[0].Annotations);
        Assert.Contains("opened:content://test/lecture.pdf", host.Log); // the recent list keeps the address
        Assert.DoesNotContain(host.Log, l => l.StartsWith("elsewhere:", StringComparison.Ordinal)); // no other window shows a URI

        Draw(doc);
        Assert.True(await workflow.SaveAsync(saveAs: false));
        Assert.Equal(1, location.Writes);
        Assert.DoesNotContain(host.Log, l => l.StartsWith("choose:", StringComparison.Ordinal)); // its own place: no question
        Assert.Contains("saved:content://test/lecture.pdf", host.Log);
        Assert.False(doc.Session.IsDirty);

        // What was written opens with both strokes.
        var again = new MemoryLocation("again.pdf", location.Content);
        Assert.True(await workflow.OpenAsync(again));
        Assert.Equal(2, workflow.Document!.Session.Current.Pages[0].Annotations.Count);
    }

    [Fact]
    public async Task ADocumentTheAppMayOnlyReadAsksWhereToSave()
    {
        var shared = new MemoryLocation("shared.pdf", await SavedPdfAsync(strokes: 0), canReplace: false);
        Assert.True(await workflow.OpenAsync(shared));
        Draw(workflow.Document!);
        var copy = new MemoryLocation("copy.pdf", []);
        host.SaveLocation = _ => copy;
        Assert.True(await workflow.SaveAsync(saveAs: false));
        Assert.Contains("choose:shared.pdf", host.Log); // suggested under its own name
        Assert.Equal(0, shared.Writes);
        Assert.Equal(1, copy.Writes);
        Assert.Same(copy, workflow.Document!.Location); // later saves go to the copy
    }

    [Fact]
    public async Task UnsavedChangesAreAskedAsynchronouslyAndAFailedSaveKeepsTheDocument()
    {
        var location = new MemoryLocation("notes.pdf", await SavedPdfAsync(strokes: 0));
        Assert.True(await workflow.OpenAsync(location));
        var doc = workflow.Document!;
        Draw(doc);
        location.FailNextWrite = true;
        host.Answers.Enqueue(Answer.Yes); // save before the new note…
        Assert.False(await workflow.CreateNoteAsync(NewPageSpec.Default)); // …the save fails: nothing is replaced
        Assert.Same(doc, workflow.Document);
        Assert.True(doc.Session.IsDirty);
        Assert.Contains("tell:Warning", host.Log);
        host.Answers.Enqueue(Answer.No); // don't save
        Assert.True(await workflow.CreateNoteAsync(NewPageSpec.Default));
        Assert.Equal(["ask:YesNoCancel", "ask:YesNoCancel"], host.Log.Where(l => l.StartsWith("ask:", StringComparison.Ordinal)));
    }
}
