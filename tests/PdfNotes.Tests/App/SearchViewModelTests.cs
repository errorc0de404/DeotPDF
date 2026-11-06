using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Platform;
using Alignment = PdfNotes.Core.Models.TextAlignment;

namespace PdfNotes.Tests.App;

/// <summary>P1 V3: the search bar's state with a fake clock and a fake canvas (delay, Enter, keeping the result, Aa).</summary>
public sealed class SearchViewModelTests : IDisposable
{
    private readonly Shell shell = new();
    private readonly ManualTime time = new();
    private readonly MainViewModel model;
    private SearchViewModel Search => model.Search;
    private FakeViewport Viewport => shell.View;

    public SearchViewModelTests()
    {
        model = MainViewModels.Create(new(new AppSettings(), "DeotPDF", _ => { }, TestFolders.New()) { Time = time }, FakeScreens.Of(shell));
        model.Workflow.Replace(OpenDocument.CreateNote(NewPageSpec.Default));
        var session = model.Document!.Session;
        var spec = session.Current.Pages[0].Setup;
        session.Execute(DocumentEdits.InsertPage(session.Current, 1, spec));
        session.Execute(DocumentEdits.InsertPage(session.Current, 2, spec));
        Add(0, "Alpha beta", 60);
        Add(1, "beta gamma", 60);
        Add(2, "BETA delta", 60);
        time.Advance(TimeSpan.FromSeconds(1)); // the page list's thumbnails, scheduled by the edits
    }

    public void Dispose()
    {
        model.Workflow.Detach()?.Dispose();
    }

    private sealed class Shell : IFakeShell
    {
        public readonly List<OpenDocument> Released = [];
        public readonly FakeViewport View = new();
        public void FinishTextEditing() { }
        public void CancelTool() { }
        public IDocumentViewport Viewport => View;
        public IPageThumbnailSource Thumbnails { get; } = new FakePageList();
        public IWindowCoordinator Window { get; } = new FakeWindow();
        public Answer Ask(string text, AskButtons buttons, NoticeKind kind) => Answer.No;
        public void Tell(string text, NoticeKind kind) { }
        public string? ChooseSavePath(SavePathRequest request) => null;
    }

    private TextBoxAnnotation Add(int page, string text, double y)
    {
        var box = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Translation(50, y), text,
            [new TextRun(0, text.Length, "Malgun Gothic", 16, RgbColor.Black, false, false, false, false)], Alignment.Left, 300, TextBoxStyle.Default, []);
        var session = model.Document!.Session;
        session.Execute(DocumentEdits.AddAnnotation(session.Current, session.Current.Pages[page].Id, box));
        return box;
    }

    /// <summary>Lets the delay pass and waits for the background run.</summary>
    private async Task Settle()
    {
        time.Advance(SearchViewModel.Delay);
        await Search.Running;
    }

    [Fact]
    public async Task TypingSearchesAfterTheDelayFromThePageInView()
    {
        Search.Query = "beta"; // closed: nothing happens
        Assert.False(Search.IsSearching);
        Assert.Equal(0, time.Pending);

        Search.Open();
        Viewport.CurrentPageIndex = 1;
        Search.Query = "bet";
        Search.Query = "beta"; // typing restarts the delay
        Assert.True(Search.IsSearching);
        Assert.Equal(1, time.Pending);
        time.Advance(SearchViewModel.Delay / 2);
        Assert.Empty(Search.Hits);
        await Settle();

        Assert.False(Search.IsSearching);
        Assert.Equal(3, Search.Hits.Count);
        Assert.Equal(1, Search.Index); // starts at the page in view
        Assert.Equal("2/3", Search.CountText);
        var (page, _) = Assert.Single(Viewport.Scrolls); // not in view: scrolled there
        Assert.Equal(1, page);
        Assert.True(Viewport.Refreshes > 0);
        Assert.Equal([1], Search.HitsOn(model.Document!.Session.Current.Pages[1].Id));

        Search.Next();
        Search.Next(); // wraps around
        Assert.Equal("1/3", Search.CountText);
        Search.Previous();
        Assert.Equal(2, Search.Index);

        Viewport.InView = (_, _) => true; // a result on screen does not move the view
        int scrolls = Viewport.Scrolls.Count;
        Search.Next();
        Assert.Equal(scrolls, Viewport.Scrolls.Count);
    }

    [Fact]
    public async Task EnterSearchesAtOnceThenSteps()
    {
        Search.Open();
        Search.Query = "beta";
        Search.Enter(backwards: false); // right after typing: no delay
        await Search.Running;
        Assert.Equal(3, Search.Hits.Count);
        Assert.Equal(0, Search.Index);
        Assert.Equal(0, time.Pending);

        Search.Enter(backwards: true);
        Assert.Equal(2, Search.Index);
        Search.Enter(backwards: false);
        Assert.Equal(0, Search.Index);
    }

    [Fact]
    public async Task MatchCaseSearchesAgain()
    {
        Search.Open();
        Search.Query = "beta";
        await Settle();
        Assert.Equal(3, Search.Hits.Count);
        Search.MatchCase = true;
        Assert.True(Search.IsSearching);
        await Settle();
        Assert.Equal(2, Search.Hits.Count); // "BETA" differs now
        Search.Query = "BETA";
        await Settle();
        Assert.Single(Search.Hits);
    }

    [Fact]
    public async Task EditsKeepTheCurrentResultAndClosingClearsEverything()
    {
        Search.Open();
        Search.Query = "beta";
        await Settle();
        Search.Next(); // page 2's result
        var current = Search.CurrentHit!;
        int scrolls = Viewport.Scrolls.Count;

        Add(0, "one more beta", 200); // a document change searches again and keeps the result
        Assert.True(Search.IsSearching);
        await Settle();
        Assert.Equal(4, Search.Hits.Count);
        Assert.Equal(current.BoxId, Search.CurrentHit!.BoxId);
        Assert.Equal(scrolls, Viewport.Scrolls.Count); // the view does not jump

        var session = model.Document!.Session;
        session.Execute(DocumentEdits.DeletePages(session.Current, [current.PageId]));
        await Settle();
        time.Advance(TimeSpan.FromSeconds(1)); // the page list's thumbnails
        Assert.Equal(3, Search.Hits.Count);
        Assert.Equal(0, Search.Index); // its page is gone: back to the first result

        Search.Query = "gamma"; // a waiting search…
        Search.Close();         // …is cancelled with the bar
        Assert.False(Search.IsOpen || Search.IsSearching);
        Assert.Empty(Search.Hits);
        Assert.Equal(0, time.Pending);
    }

    [Fact]
    public async Task AnotherDocumentStartsANewSearch()
    {
        Search.Open();
        Search.Query = "beta";
        await Settle();
        Assert.Equal(3, Search.Hits.Count);
        Assert.True(await model.Workflow.CreateNoteAsync(NewPageSpec.Default));
        Assert.Empty(Search.Hits);
        Assert.True(Search.IsSearching);
        await Settle();
        Assert.Empty(Search.Hits);
        Assert.Equal("0/0", Search.CountText);
    }
}
