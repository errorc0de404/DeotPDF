using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;

namespace PdfNotes.Tests.App;

/// <summary>P1 V5: the page panel's rows, active page, selection, thumbnails and page commands.</summary>
public sealed class PagesViewModelTests : IDisposable
{
    private readonly Shell shell = new();
    private readonly AppSettings settings = new();
    private readonly ManualTime time = new();
    private readonly FakeClipboard clipboard = new();
    private readonly MainViewModel model;
    private int saves;
    private PagesViewModel Pages => model.Pages;
    private DocumentSession Session => model.Document!.Session;
    private FakePageList List => shell.List;

    public PagesViewModelTests()
    {
        model = new MainViewModel(shell, shell, settings, "DeotPDF", _ => saves++, TestFolders.New(), time, clipboard: clipboard);
        model.Workflow.Replace(OpenDocument.CreateNote(NewPageSpec.Default));
        Pages.DocumentReplaced();
        var spec = Session.Current.Pages[0].Setup;
        Session.Execute(DocumentEdits.InsertPage(Session.Current, 1, spec));
        Session.Execute(DocumentEdits.InsertPage(Session.Current, 2, spec));
        shell.View.PageCount = 3;
        Pages.Refresh();
    }

    public void Dispose()
    {
        model.Workflow.Detach()?.Dispose();
        foreach (var d in shell.Released) d.Dispose();
    }

    private Guid Id(int index) => Session.Current.Pages[index].Id;

    private sealed class Shell : IMainView, IEditorDialogs
    {
        public readonly List<OpenDocument> Released = [];
        public readonly FakeViewport View = new();
        public readonly FakePageList List = new();
        public readonly List<string> Told = [], Asked = [];
        public Answer Answer = Answer.Yes;
        public int Finished;
        public void FinishTextEditing() => Finished++;
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
        public IPageListView Pages => List;
        public IWindowCoordinator Window { get; } = new FakeWindow();
        public IToolView Tools { get; } = new FakeToolView();
        public Answer Ask(string text, AskButtons buttons, NoticeKind kind) { Asked.Add(text); return Answer; }
        public void Tell(string text, NoticeKind kind) => Told.Add(text);
        public string? ChooseSavePath(SavePathRequest request) => null;
    }

    [Fact]
    public void RowsFollowThePagesAndTheActivePageFollowsTheView()
    {
        Assert.Equal(Session.Current.Pages.Select(p => p.Id), Pages.Items.Select(i => i.PageId));
        Assert.Equal([1, 2, 3], Pages.Items.Select(i => i.Number));
        Assert.True(Pages.Items[0].IsActive);
        Assert.Equal([Id(0)], Pages.SelectedPageIds); // nothing selected: the active page

        shell.View.CurrentPageIndex = 2; // the view scrolled
        Pages.FollowView();
        Assert.Equal(2, Pages.ActivePageIndex);
        Assert.True(Pages.Items[2].IsActive && !Pages.Items[0].IsActive);
        Assert.Equal([Id(2)], List.Followed);

        Pages.Activate(Id(1), scroll: true); // a click in the list
        Assert.Equal(1, shell.View.CurrentPageIndex);
        Assert.Equal(1, Pages.ActivePageIndex);

        var first = Pages.Items[0];
        List.Select([Id(0), Id(2)]);
        Session.Execute(PageEdits.MovePages(Session.Current, [Id(0)], 3)); // the first page to the end
        Pages.Refresh();
        Assert.Same(first, Pages.Items[2]); // rows are kept, renumbered
        Assert.Equal([1, 2, 3], Pages.Items.Select(i => i.Number));
        Assert.Equal(2, List.Selected.Count); // the selection survives a rebuild
    }

    [Fact]
    public void DeleteAsksAndKeepsOnePage()
    {
        List.Select([Id(1)]);
        shell.Answer = Answer.No;
        Assert.False(Pages.DeleteSelected());
        Assert.Equal(3, Session.Current.Pages.Count);
        Assert.Equal(Strings.Format("page.deleteConfirm", ("count", 1)), shell.Asked.Single());

        shell.Answer = Answer.Yes;
        var third = Id(2);
        Assert.True(Pages.DeleteSelected());
        Assert.Equal(2, Session.Current.Pages.Count);
        Assert.Equal([third], List.Selected); // the page now in its place
        Assert.Equal(1, Pages.ActivePageIndex);

        List.Select([Id(0), Id(1)]);
        Assert.False(Pages.CanDelete);
        Assert.False(Pages.DeleteSelected());
        Assert.Equal(Strings.Get("page.lastPage"), shell.Told.Single());
        Assert.Equal(2, Session.Current.Pages.Count);
    }

    [Fact]
    public void CopiedPagesArePastedAfterTheActivePage()
    {
        Assert.False(Pages.CanPaste || Pages.HasPageClipboard);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Id(0), new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(10, 10), new(50, 50)], 2, RgbColor.Black, 1)));
        var changed = new List<string?>();
        Pages.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        List.Select([Id(0)]);
        Assert.True(Pages.Copy(PageCopyMode.PageOnly));
        Assert.True(Pages.CanPaste);
        Assert.Contains(nameof(PagesViewModel.CanPaste), changed);
        Assert.Equal(Strings.Get("page.copiedOne"), model.Status);
        Assert.True(clipboard.ContainsData(PageTransfer.Format)); // other windows see the copy at once
        Assert.Equal(Session.SessionId, PageTransfer.Decode(clipboard.GetData(PageTransfer.Format))?.Session);

        Pages.Activate(Id(1), scroll: false);
        List.Select([]);
        Assert.True(Pages.Paste());
        Assert.Equal(4, Session.Current.Pages.Count);
        var pasted = Session.Current.Pages[2];
        Assert.Empty(pasted.Annotations); // page only
        Assert.Equal([pasted.Id], List.Selected);
        Assert.Equal(2, Pages.ActivePageIndex);
        Assert.True(Session.Undo());
        Assert.Equal(3, Session.Current.Pages.Count);
    }

    [Fact]
    public void MoveRotateAndBlankPagesAreOneUndoEach()
    {
        int undo = Session.UndoCount;
        List.Select([Id(0), Id(1)]);
        Guid[] ids = [Id(0), Id(1)];
        Assert.True(Pages.MoveTo(Pages.SelectedPageIds, 3));
        Assert.Equal(ids, new[] { Id(1), Id(2) });
        Assert.Equal(ids, List.Selected.OrderBy(id => Session.Current.IndexOfPage(id)));
        Assert.Equal(1, Pages.ActivePageIndex);

        Assert.True(Pages.RotateSelected(clockwise: true));
        Assert.Equal(90, Session.Current.GetPage(ids[0]).Setup.Rotation);

        Pages.Activate(Id(0), scroll: false);
        Assert.Same(Session.Current.Pages[0], Pages.BlankReference(insertAfterActive: true));
        Assert.True(Pages.AddBlank(insertAfterActive: true, NewPageSpec.Default));
        Assert.Equal(4, Session.Current.Pages.Count);
        Assert.Equal(1, Pages.ActivePageIndex);
        Assert.True(Pages.AddBlank(insertAfterActive: false, NewPageSpec.Default));
        Assert.Equal(4, Pages.ActivePageIndex);
        Assert.Equal(undo + 4, Session.UndoCount);
        Assert.True(shell.Finished >= 4); // text input is finished before each command
    }

    [Fact]
    public async Task ThumbnailsAreDrawnAfterAPauseAndOnlyForChangedPages()
    {
        List.Drawn.Clear();
        Pages.ScheduleThumbnails();
        time.Advance(TimeSpan.FromMilliseconds(249));
        Assert.Empty(List.Drawn);
        await Pages.RenderThumbnailsAsync();
        Assert.Equal(3, List.Drawn.Count);
        Assert.All(Pages.Items, i => Assert.NotNull(i.Image));
        Assert.Equal(PagesViewModel.ThumbnailWidth * 2, Pages.Items[0].ImageHeight);

        List.Drawn.Clear();
        await Pages.RenderThumbnailsAsync();
        Assert.Empty(List.Drawn); // nothing changed

        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Id(1), new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(10, 10), new(50, 50)], 2, RgbColor.Black, 1)));
        await Pages.RenderThumbnailsAsync();
        Assert.Equal([Id(1)], List.Drawn);
        Assert.Equal($"{Id(1)}:1:False", Pages.Items[1].Image);

        List.Drawn.Clear();
        await Pages.InvalidateThumbnailsAsync(); // another monitor scale
        Assert.Equal(3, List.Drawn.Count);
    }

    [Fact]
    public void ThePanelIsRememberedAndTheMenuFollowsTheSelection()
    {
        Assert.True(Pages.PanelVisible);
        Pages.PanelVisible = false;
        Assert.False(settings.PagePanelVisible);
        Assert.Equal(1, saves);

        var menu = Pages.Menu();
        Assert.Equal(14, menu.Count);
        Assert.Equal(3, menu.Count(e => e.Text is null));
        Assert.False(menu.Single(e => e.Command == PageCommand.Paste && e.Text is not null).Enabled);
        Assert.False(menu.Single(e => e.Command == PageCommand.ClearNotes).Enabled); // no notes yet
        Assert.Equal(Strings.Get("page.clearNotes"), menu.Single(e => e.Command == PageCommand.ClearNotes).Text);

        List.Select([Id(0), Id(1)]);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Id(1), new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(10, 10), new(50, 50)], 2, RgbColor.Black, 1)));
        menu = Pages.Menu();
        Assert.True(menu.Single(e => e.Command == PageCommand.ClearNotes).Enabled);
        Assert.Equal(Strings.Get("page.clearSelectedNotes"), menu.Single(e => e.Command == PageCommand.ClearNotes).Text);
        Assert.Equal([Id(0)], Pages.Targets(PageApplyScope.Current));
        Assert.Equal(3, Pages.Targets(PageApplyScope.All).Length);
        Assert.Equal(2, Pages.Targets(PageApplyScope.Selected).Length);
    }
}
