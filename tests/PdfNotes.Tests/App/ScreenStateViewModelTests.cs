using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Session;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Tests.App;

/// <summary>P1 V2: ribbon tab, recent documents, update check, loading card, outline and other apps' annotations, without the WPF shell.</summary>
public sealed class ScreenStateViewModelTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    private readonly Shell shell = new();
    private readonly ManualTime time = new();
    private readonly AppSettings settings = new();
    private readonly MainViewModel model;
    private int saves;
    private Func<Task<UpdateResult>> latest = () => Task.FromResult(new UpdateResult(UpdateState.UpToDate));

    public ScreenStateViewModelTests()
    {
        Directory.CreateDirectory(folder);
        model = new MainViewModel(shell, shell, settings, "DeotPDF", time, _ => saves++, () => latest());
        model.Workflow.Replace(OpenDocument.CreateNote(NewPageSpec.Default));
    }

    public void Dispose()
    {
        model.Workflow.Detach()?.Dispose();
        foreach (var d in shell.Released) d.Dispose();
        Directory.Delete(folder, true);
    }

    private sealed class Shell : IMainView, IEditorDialogs
    {
        public readonly List<OpenDocument> Released = [];
        public readonly List<string> Asked = [];
        public Answer Answer = Answer.Yes;
        public int Waits;

        public void FinishTextEditing() { }
        public void CancelTool() { }
        public void DocumentLeaving(OpenDocument? previous) { }
        public void DocumentReplaced(OpenDocument? previous, OpenDocument next) { if (previous is not null) Released.Add(previous); }
        public void DocumentChanged() { }
        public void WaitForFirstPages() => Waits++;
        public void Opened(string path) { }
        public void Saved(string path) { }
        public bool ShowOpenElsewhere(string path) => false;
        public void CommandsChanged() { }
        public void OnUiThread(Action action) => action();
        public IDocumentViewport Viewport { get; } = new FakeViewport();
        public IPageListView Pages { get; } = new FakePageList();
        public IWindowCoordinator Window { get; } = new FakeWindow();
        public IToolView Tools { get; } = new FakeToolView();
        public Answer Ask(string text, AskButtons buttons, NoticeKind kind) { Asked.Add(text); return Answer; }
        public void Tell(string text, NoticeKind kind) { }
        public string? ChooseSavePath(SavePathRequest request) => null;
    }

    [Fact]
    public void AutomaticRibbonTabSwitchesOnlyBetweenWriteAndPage()
    {
        var ribbon = model.Ribbon;
        Assert.Equal(RibbonTab.Home, ribbon.Current);
        ribbon.Auto(RibbonTab.Page);
        Assert.Equal(RibbonTab.Page, ribbon.Current);

        ribbon.Hold = true; // an Undo keeps the tab
        ribbon.Auto(RibbonTab.Home);
        Assert.Equal(RibbonTab.Page, ribbon.Current);
        ribbon.Hold = false;

        ribbon.Current = RibbonTab.View; // an open 보기 tab stays
        ribbon.Auto(RibbonTab.Home);
        Assert.Equal(RibbonTab.View, ribbon.Current);

        ribbon.Current = RibbonTab.Home;
        settings.AutoSwitchTabs = false;
        ribbon.Auto(RibbonTab.Page);
        Assert.Equal(RibbonTab.Home, ribbon.Current);
    }

    [Fact]
    public async Task SavedFilesBecomeRecentAndAMissingOneLeavesTheList()
    {
        var changed = new List<string?>();
        model.Recent.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(model.Recent.HasFiles);

        string path = Path.Combine(folder, "note.pdf");
        Assert.True(await model.Workflow.SaveToAsync(path));
        Assert.Equal([path], model.Recent.Files);
        Assert.True(model.Recent.HasFiles);
        Assert.Contains(nameof(RecentViewModel.HasFiles), changed);
        Assert.True(saves > 0);

        for (int i = 0; i < AppSettings.MaxRecentFiles + 2; i++) model.Recent.Add(Path.Combine(folder, $"other{i}.pdf"));
        model.Recent.Add(path.ToUpperInvariant()); // the same file in other letters moves to the top, once
        Assert.Equal(AppSettings.MaxRecentFiles, model.Recent.Files.Count);
        Assert.Single(model.Recent.Files, p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(path, model.Recent.Files[0], ignoreCase: true);

        string gone = model.Recent.Files[1];
        Assert.False(await model.OpenRecentAsync(gone));
        Assert.DoesNotContain(gone, model.Recent.Files);
        Assert.Equal(Strings.Format("recent.missing", ("name", Path.GetFileName(gone))), model.Status);
    }

    [Fact]
    public void PositionsAreRememberedPerDocument()
    {
        string path = Path.Combine(folder, "a.pdf");
        Assert.Null(model.Recent.PositionOf(path));
        model.Recent.RememberPosition(path, 3, new Point2(10, 20), 1.5);
        model.Recent.RememberPosition(path.ToUpperInvariant(), 4, new Point2(30, 40), 2);
        var saved = model.Recent.PositionOf(path);
        Assert.NotNull(saved);
        Assert.Equal((4, 30d, 40d, 2d), (saved.Page, saved.X, saved.Y, saved.Zoom));
        Assert.Single(settings.Positions);
    }

    [Fact]
    public void LoadingCardShowsOnlyForSlowOpensAndWaitsForTheFirstPages()
    {
        var loading = model.Loading;
        IDocumentHost host = model;

        host.Loading("fast.pdf");
        Assert.Equal(Strings.Format("status.openingFile", ("name", "fast.pdf")), loading.Text);
        time.Advance(LoadingViewModel.Delay / 2);
        host.Loading(null); // read before the delay: the card never appears
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(loading.IsShown);
        Assert.Equal(0, shell.Waits);

        host.Loading("slow.pdf");
        time.Advance(LoadingViewModel.Delay);
        Assert.True(loading.IsShown && loading.BlocksInput);
        host.Loading(null); // read: the card stays over the blank pages but lets input through
        Assert.True(loading.IsShown && loading.WaitsForPages && !loading.BlocksInput);
        Assert.Equal(1, shell.Waits);
        loading.PagesDrawn();
        Assert.False(loading.IsShown || loading.WaitsForPages);

        host.Loading("slower.pdf");
        time.Advance(LoadingViewModel.Delay);
        host.Loading(null);
        time.Advance(LoadingViewModel.Linger); // pages never came: the card goes anyway
        Assert.False(loading.IsShown);
    }

    [Fact]
    public async Task UpdateCheckAsksOnceAndShowsANewerRelease()
    {
        int checks = 0;
        latest = () => { checks++; return Task.FromResult(new UpdateResult(UpdateState.Available, "9.0.0", "https://example.invalid/r")); };

        await model.Update.CheckAtStartAsync("1.0.0-dev"); // development builds never ask or check
        Assert.Empty(shell.Asked);
        Assert.Equal(0, checks);

        await model.Update.CheckAtStartAsync("1.0.0");
        Assert.Single(shell.Asked);
        Assert.True(settings.CheckUpdates);
        Assert.Equal(1, checks);
        Assert.Equal(time.Now.UtcDateTime, settings.UpdateCheckedAt);
        Assert.True(model.Update.IsAvailable);
        Assert.Equal(Strings.Format("about.available", ("version", "9.0.0")), model.Update.AvailableText);
        Assert.Equal("https://example.invalid/r", model.Update.Url);

        time.Advance(TimeSpan.FromHours(1)); // checked today: not again, and not asked again
        await model.Update.CheckAtStartAsync("1.0.0");
        Assert.Single(shell.Asked);
        Assert.Equal(1, checks);

        latest = () => Task.FromResult(new UpdateResult(UpdateState.Failed));
        var before = settings.UpdateCheckedAt;
        time.Advance(TimeSpan.FromDays(2));
        await model.Update.CheckAsync();
        Assert.Equal(before, settings.UpdateCheckedAt); // a failed check is tried again next time
    }

    [Fact]
    public async Task OutlineFollowsTheDocumentAndItsPages()
    {
        string path = Path.Combine(folder, "linked.pdf");
        ProbeSamplePdf.CreateLinked(path);
        settings.OutlineVisible = true;
        Assert.True(await model.Workflow.OpenFileAsync(path));
        var outline = model.Outline;
        Assert.True(outline.IsAvailable && outline.IsVisible);
        var items = OutlineViewModel.All(outline.Items).ToArray();
        Assert.Equal(["1장 시작", "1.1 둘째 쪽", "2장", "2.1 마지막 쪽"], items.Select(i => i.Title));
        Assert.Same(items[1], outline.CurrentItem(2));
        Assert.Same(items[3], outline.CurrentItem(4));

        var session = model.Document!.Session;
        session.Execute(DocumentEdits.DeletePages(session.Current, [session.Current.Pages[1].Id]));
        Assert.False(outline.Reachable(items[1])); // its page was deleted: dimmed, does nothing
        Assert.Null(outline.Target(items[1]));
        Assert.Equal(2, outline.Target(items[2])); // the page moved up
        Assert.Same(items[0], outline.CurrentItem(1));

        outline.SetVisible(false);
        Assert.False(outline.IsVisible || settings.OutlineVisible);
        outline.RememberHeight(20); // too small to remember
        outline.RememberHeight(300);
        Assert.Equal(300, outline.Height);

        shell.Answer = Answer.No; // don't save the changes
        Assert.True(await model.Workflow.CreateNoteAsync(NewPageSpec.Default));
        Assert.False(outline.IsAvailable);
        outline.SetVisible(true); // nothing to show
        Assert.False(outline.IsVisible);
    }

    [Fact]
    public async Task ForeignAnnotationsAreCountedAndConvertedOnce()
    {
        string path = Path.Combine(folder, "foreign.pdf");
        ForeignAnnotationSample.Create(path);
        var changed = new List<string?>();
        model.Foreign.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.True(await model.Workflow.OpenFileAsync(path));
        await model.Foreign.RecountAsync(); // the open started a count in the background; this one waits for it to be shown

        int count = model.Foreign.Count;
        Assert.True(count >= ForeignAnnotationSample.ConvertiblePdfAnnotations, $"count {count}");
        Assert.True(model.Foreign.IsShown && model.Foreign.CanConvert);
        Assert.Contains(nameof(ForeignViewModel.IsShown), changed);
        Assert.Equal(Strings.Format("status.foreignFound", ("count", count)), model.Status);

        int converted = await model.Foreign.ConvertAsync();
        Assert.True(converted > 0);
        Assert.Equal(Strings.Format("status.foreignConverted", ("count", converted)), model.Status);
        Assert.False(model.Foreign.CanConvert); // all converted
        Assert.True(model.Foreign.IsShown);    // still in the sources: the command stays, disabled

        model.Document!.Session.Undo();
        Assert.True(model.Foreign.CanConvert);

        shell.Answer = Answer.No; // don't save the changes
        Assert.True(await model.Workflow.CreateNoteAsync(NewPageSpec.Default));
        Assert.Equal(0, model.Foreign.Count);
        Assert.False(model.Foreign.IsShown);
    }
}
