using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;

namespace PdfNotes.Tests.App;

/// <summary>P1 V6: automatic backup on a fake clock, page import and export through the shell's page windows.</summary>
public sealed class BackupImportViewModelTests : IDisposable
{
    private readonly Shell shell = new();
    private readonly AppSettings settings = new();
    private readonly ManualTime time = new();
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotesV6-" + Guid.NewGuid().ToString("N"));
    private readonly MainViewModel model;
    private BackupViewModel Backup => model.Backup;
    private DocumentSession Session => model.Document!.Session;

    public BackupImportViewModelTests()
    {
        Directory.CreateDirectory(folder);
        settings.BackupFolder = Path.Combine(folder, "Backups");
        model = new MainViewModel(shell, shell, settings, "DeotPDF", _ => { }, folder, time, pageDialogs: shell);
        model.Workflow.Replace(OpenDocument.CreateNote(NewPageSpec.Default));
        shell.List.Pages = model.Pages;
        model.Pages.DocumentReplaced();
    }

    public void Dispose()
    {
        Backup.Stop();
        model.Workflow.Detach()?.Dispose();
        foreach (var d in shell.Released) d.Dispose();
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    private void Draw() => Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id,
        new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(10, 10), new(80, 60)], 2, RgbColor.Black, 1)));

    private sealed class Shell : IMainView, IEditorDialogs, IPageDialogs
    {
        public readonly List<OpenDocument> Released = [];
        public readonly FakeViewport View = new();
        public readonly FakePageList List = new();
        public readonly FakeWindow Coordinator = new();
        public readonly List<string> Told = [], Asked = [];
        public Answer Answer = Answer.Yes;
        public string[]? Files;
        public int[]? ImportPages;
        public (int Count, string Range, bool Flatten)? ExportAsked;
        public PageExportChoice? Export = null;
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
        public IDocumentViewport Viewport => View;
        public IPageThumbnailSource Thumbnails => List;
        public IWindowCoordinator Window => Coordinator;
        public Answer Ask(string text, AskButtons buttons, NoticeKind kind) { Asked.Add(text); return Answer; }
        public void Tell(string text, NoticeKind kind) => Told.Add(text);
        public string? ChooseSavePath(SavePathRequest request) => null;
        public string[]? ChoosePdfFiles(string title) => Files;
        public Task<int[]?> ChooseImportPagesAsync(MergeFile file) => Task.FromResult(ImportPages);
        public Task<MergeChoice?> ChooseMergeAsync(IReadOnlyList<MergeFile> files, bool insert, Func<Task<IReadOnlyList<MergeFile>>> addFiles) =>
            Task.FromResult<MergeChoice?>(new([.. files.Select(f => (f, new[] { 0 }))], MergePosition.Start));
        public PageExportChoice? ChooseExport(int pageCount, string range, bool flatten) { ExportAsked = (pageCount, range, flatten); return Export; }
        public NewPageSpec? ChooseBlankPage(bool insert, PageSetup reference) => null;
        public PageTransformRequest? ChooseTransform(bool resize, DocumentModel model, Page active, Func<PageApplyScope, PageSetup[]> targets) => null;
    }

    [Fact]
    public async Task ABackupWaitsForTheIntervalAndForWorkInProgress()
    {
        Assert.False(Backup.Started);
        int timers = time.Pending;
        Backup.Start();
        Assert.True(Backup.Started);
        Assert.Equal(timers + 1, time.Pending); // the 500 ms tick
        Backup.IntervalOverride = TimeSpan.FromSeconds(2);

        await Backup.TickAsync();
        Assert.Null(Backup.LastFile); // nothing changed

        Draw();
        await Backup.TickAsync(); // the change is noticed now
        time.Now += TimeSpan.FromSeconds(1);
        await Backup.TickAsync();
        Assert.Null(Backup.LastFile); // not yet due

        time.Now += TimeSpan.FromSeconds(1.5);
        shell.Coordinator.InputPending = true; // a pressed button or an open dialog
        Assert.True(Backup.Deferred);
        await Backup.TickAsync();
        Assert.Null(Backup.LastFile);
        shell.Coordinator.InputPending = false;
        shell.View.IsToolActive = true; // a stroke in progress
        await Backup.TickAsync();
        Assert.Null(Backup.LastFile);
        shell.View.IsToolActive = false;

        await Backup.TickAsync();
        Assert.NotNull(Backup.LastFile);
        Assert.True(File.Exists(Backup.LastFile));
        Assert.StartsWith(settings.BackupFolder!, Backup.LastFile);
        Assert.True(Session.IsDirty); // a folder backup leaves the unsaved mark
        Assert.Equal(Strings.Format("backup.done", ("time", string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:HH:mm}", Backup.LastTime))), model.Status);

        var first = Backup.LastFile;
        time.Now += TimeSpan.FromSeconds(5);
        await Backup.TickAsync();
        Assert.Equal(first, Backup.LastFile); // the same state is not backed up again

        shell.Answer = Answer.No;
        Assert.False(Backup.Clear());
        Assert.Contains(FileSizes.Format(new FileInfo(first!).Length), shell.Asked.Single());
        Assert.True(File.Exists(first));
        shell.Answer = Answer.Yes;
        Assert.True(Backup.Clear());
        Assert.False(File.Exists(first));
        Assert.Null(Backup.LastFile);

        Backup.Stop();
        Assert.Equal(timers, time.Pending);
    }

    [Fact]
    public void CrashBackupWritesOnlyUnsavedChanges()
    {
        int kept = 0;
        Assert.Null(Backup.WriteCrashBackup(new InvalidOperationException("test"), () => kept++)); // nothing unsaved
        Assert.Equal(1, kept);
        Draw();
        var file = Backup.WriteCrashBackup(new InvalidOperationException("test"), () => kept++);
        Assert.NotNull(file);
        Assert.StartsWith("crash_", Path.GetFileName(file));
        Assert.True(File.Exists(file) && File.Exists(Path.ChangeExtension(file, ".txt")));
    }

    [Fact]
    public async Task PagesOfAnotherPdfAreInsertedAfterTheActivePage()
    {
        string other = Path.Combine(folder, "other.pdf");
        var source = OpenDocument.CreateNote(NewPageSpec.Default);
        var spec = source.Session.Current.Pages[0].Setup;
        source.Session.Execute(DocumentEdits.InsertPage(source.Session.Current, 1, spec));
        await source.SaveAsync(other);
        source.Dispose();

        shell.Files = null;
        Assert.False(await model.ImportExport.ImportPagesAsync()); // the file window was cancelled

        shell.ImportPages = null;
        Assert.False(await model.ImportExport.ImportPagesAsync([other])); // the range window was cancelled
        Assert.Single(Session.Current.Pages);

        shell.ImportPages = [1];
        Assert.True(await model.ImportExport.ImportPagesAsync([other]));
        Assert.Equal(2, Session.Current.Pages.Count);
        Assert.Equal([Session.Current.Pages[1].Id], shell.List.Selected);
        Assert.Equal(Strings.Get("import.doneOne"), model.Status);
        Assert.False(model.ImportExport.Importing);

        Assert.True(await model.ImportExport.ImportPagesAsync([other, other])); // several files: the merge window, at the start
        Assert.Equal(4, Session.Current.Pages.Count);
        Assert.Equal(2, shell.List.Selected.Count);
        Assert.Equal(0, model.Pages.ActivePageIndex);

        string broken = Path.Combine(folder, "broken.pdf");
        File.WriteAllText(broken, "not a pdf");
        Assert.False(await model.ImportExport.ImportPagesAsync([broken]));
        Assert.StartsWith("broken.pdf", shell.Told.Single());
        Assert.Equal(4, Session.Current.Pages.Count);
    }

    [Fact]
    public async Task ExportStartsWithTheSelectedPagesAndWritesNothingWhenCancelled()
    {
        var spec = Session.Current.Pages[0].Setup;
        Session.Execute(DocumentEdits.InsertPage(Session.Current, 1, spec));
        Session.Execute(DocumentEdits.InsertPage(Session.Current, 2, spec));
        shell.List.Select([Session.Current.Pages[1].Id, Session.Current.Pages[2].Id]);

        Assert.Equal(0, await model.ImportExport.ExportPagesAsync());
        Assert.Equal((3, PageRanges.Format([1, 2], 3), false), shell.ExportAsked);
        Assert.Equal(0, await model.ImportExport.ExportPagesAsync(flatten: true));
        Assert.Equal((3, PageRanges.All, true), shell.ExportAsked);
        Assert.Equal(0, await model.ImportExport.ExportPagesAsync(allPages: true));
        Assert.Equal(PageRanges.All, shell.ExportAsked?.Range);
        Assert.True(Session.IsDirty); // the document is unchanged by a cancelled export
    }
}
