using System.Globalization;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;

namespace PdfNotes.Tests.App;

/// <summary>P1 V4: the shortcut table, the status bar's page and zoom, the jump history, display values and favorites.</summary>
public sealed class NavigationViewModelTests : IDisposable
{
    private readonly Shell shell = new();
    private readonly AppSettings settings = new();
    private readonly MainViewModel model;
    private int saves;
    private NavigationViewModel Navigation => model.Navigation;
    private FakeViewport Viewport => shell.View;

    public NavigationViewModelTests()
    {
        model = new MainViewModel(shell, shell, settings, "DeotPDF", new ManualTime(), _ => saves++);
        model.Workflow.Replace(OpenDocument.CreateNote(NewPageSpec.Default));
        var session = model.Document!.Session;
        var spec = session.Current.Pages[0].Setup;
        session.Execute(DocumentEdits.InsertPage(session.Current, 1, spec));
        session.Execute(DocumentEdits.InsertPage(session.Current, 2, spec));
        Viewport.PageCount = 3;
    }

    public void Dispose()
    {
        model.Workflow.Detach()?.Dispose();
        foreach (var d in shell.Released) d.Dispose();
    }

    private sealed class Shell : IMainView, IEditorDialogs
    {
        public readonly List<OpenDocument> Released = [];
        public readonly FakeViewport View = new();
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
        public IPageListView Pages { get; } = new FakePageList();
        public IWindowCoordinator Window { get; } = new FakeWindow();
        public IToolView Tools { get; } = new FakeToolView();
        public Answer Ask(string text, AskButtons buttons, NoticeKind kind) => Answer.No;
        public void Tell(string text, NoticeKind kind) { }
        public string? ChooseSavePath(SavePathRequest request) => null;
    }

    [Fact]
    public void ShortcutTableHasOneCommandPerKeyAndTheRightScopes()
    {
        Assert.Equal(Shortcuts.All.Length, Shortcuts.All.Select(s => (s.Key, s.Modifiers)).Distinct().Count());
        Assert.Equal((ShortcutScope.Anywhere, ShortcutCommand.ZoomIn), Scope(ShortcutKey.Plus, InputModifiers.Control)); // also while typing (spec 24.2)
        Assert.Equal((ShortcutScope.Anywhere, ShortcutCommand.FitWidth), Scope(ShortcutKey.Zero, InputModifiers.Control));
        Assert.Equal((ShortcutScope.Writing, ShortcutCommand.Favorite3), Scope(ShortcutKey.D3, InputModifiers.Control));
        Assert.Equal(2, Shortcuts.FavoriteSlot(ShortcutCommand.Favorite3));
        Assert.Null(Shortcuts.FavoriteSlot(ShortcutCommand.Back));
        Assert.Equal((ShortcutScope.NotTextInput, ShortcutCommand.Back), Scope(ShortcutKey.Left, InputModifiers.Alt));
        Assert.Equal((ShortcutScope.Canvas, ShortcutCommand.SidewaysLeft), Scope(ShortcutKey.Left, InputModifiers.None));
        Assert.Equal((ShortcutScope.Canvas, ShortcutCommand.LastPage), Scope(ShortcutKey.End, InputModifiers.Control));
        Assert.Null(Shortcuts.Find(ShortcutKey.Left, InputModifiers.Shift)); // Shift+arrows: the selection's big move, not a page key
        Assert.Null(Shortcuts.Find(ShortcutKey.Plus, InputModifiers.Control | InputModifiers.Shift));
        Assert.Null(Shortcuts.Find(ShortcutKey.Home, InputModifiers.None));

        static (ShortcutScope, ShortcutCommand) Scope(ShortcutKey key, InputModifiers modifiers) =>
            Shortcuts.Find(key, modifiers) is { } s ? (s.Scope, s.Command) : throw new InvalidOperationException($"{key} missing");
    }

    [Fact]
    public void StatusBarAndPageKeysMoveTheView()
    {
        Viewport.CurrentPageIndex = 1;
        Viewport.Zoom = 1.25;
        Assert.Equal("2", Navigation.PageNumberText);
        Assert.Equal("/ 3", Navigation.PageCountText);
        Assert.Equal(ZoomSteps.Format(1.25, CultureInfo.CurrentCulture), Navigation.ZoomText);
        var changed = new List<string?>();
        Navigation.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Navigation.ViewChanged();
        Assert.Contains(nameof(NavigationViewModel.ZoomText), changed);

        Assert.True(Navigation.GoToPageNumber(" 3 "));
        Assert.Equal(2, Viewport.CurrentPageIndex);
        Assert.False(Navigation.GoToPageNumber("three"));
        Assert.True(Navigation.SetZoomText("150%"));
        Assert.Equal(1.5, Viewport.Zoom);
        Assert.False(Navigation.SetZoomText("big"));
        Navigation.ZoomByStep(1);
        Assert.Equal(2, Viewport.Zoom); // the next preset after 150 %
        Assert.Single(Navigation.ZoomMenu(), m => m.Current);
        Assert.Equal(200, Navigation.ZoomMenu().Single(m => m.Current).Percent);

        Navigation.SetMode(ViewMode.SinglePage);
        Assert.Equal(ViewMode.SinglePage, Viewport.Mode);
        Assert.Equal(ViewMode.SinglePage, settings.ViewMode);
        Assert.True(saves > 0);

        Viewport.Calls.Clear();
        Assert.True(Navigation.Run(ShortcutCommand.FirstPage, repeat: false));
        Assert.True(Navigation.Run(ShortcutCommand.NextPage, repeat: false));
        Assert.True(Navigation.Run(ShortcutCommand.SidewaysRight, repeat: true));
        Assert.True(Navigation.Run(ShortcutCommand.ScrollDown, repeat: false));
        Assert.Equal(["page 0", "page 1", "sideways 1", "lineDown"], Viewport.Calls);
        Assert.False(Navigation.Run(ShortcutCommand.Favorite1, repeat: false)); // the view's

        Viewport.Calls.Clear();
        Viewport.IsToolActive = true; // a stroke in progress: the page stays, the keys are still used
        Assert.True(Navigation.Run(ShortcutCommand.NextPage, repeat: false));
        Assert.True(Navigation.Run(ShortcutCommand.LastPage, repeat: false));
        Assert.True(Navigation.Run(ShortcutCommand.ScrollUp, repeat: false)); // scrolling a line still works
        Assert.Equal(["lineUp"], Viewport.Calls);
    }

    [Fact]
    public void JumpsCanBeWalkedBackAndForwardAndSkipDeletedPages()
    {
        Assert.False(Navigation.CanGoBack || Navigation.GoBack());
        Viewport.Zoom = 1.5;
        Viewport.Center = new(10, 20);
        Navigation.JumpTo(2, null);
        Assert.True(Navigation.CanGoBack);
        Assert.Equal(2, Viewport.CurrentPageIndex);
        Navigation.JumpTo(1, new Point2(30, 40)); // a link target point (no content transform on a note page)
        Assert.Contains("show 1 30,40", Viewport.Calls);

        Viewport.Zoom = 1;
        Assert.True(Navigation.GoBack());
        Assert.Equal(2, Viewport.CurrentPageIndex);
        Assert.True(Navigation.GoBack());
        Assert.Equal((0, new Point2(10, 20), 1.5), (Viewport.CurrentPageIndex, Viewport.Center, Viewport.Zoom));
        Assert.False(Navigation.CanGoBack);
        Assert.True(Navigation.GoForward());
        Assert.Equal(2, Viewport.CurrentPageIndex);
        Assert.True(Navigation.CanGoForward);

        var session = model.Document!.Session;
        session.Execute(DocumentEdits.DeletePages(session.Current, [session.Current.Pages[1].Id]));
        Viewport.PageCount = 2;
        Assert.False(Navigation.GoForward()); // its only place was on the deleted page
        Assert.True(Navigation.GoBack());

        for (int i = 0; i < NavigationViewModel.MaxHistory + 5; i++) Navigation.JumpTo(i % 2, null);
        int steps = 0;
        while (Navigation.GoBack()) steps++;
        Assert.Equal(NavigationViewModel.MaxHistory, steps);

        Navigation.JumpTo(1, null);
        model.Workflow.Replace(OpenDocument.CreateNote(NewPageSpec.Default)); // jumps belong to one document
        Assert.False(Navigation.CanGoBack || Navigation.CanGoForward);
    }

    [Fact]
    public void DisplayPreviewIsNotStoredUntilApplied()
    {
        var display = model.Display;
        var stored = display.Stored;
        var changed = new List<string?>();
        display.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        display.Preview(stored with { DarkPages = true, NumberSize = PageNumberSize.Large, Favorites = false });
        Assert.True(display.DarkPages);
        Assert.False(display.FavoritesShown);
        Assert.Equal(18, display.PageNumberDip);
        Assert.Contains(nameof(DisplayViewModel.DarkPages), changed);
        Assert.False(settings.DarkPages);
        Assert.Equal(0, saves);

        display.Preview(stored); // cancel
        Assert.False(display.DarkPages);
        display.Apply(stored with { PageNumbers = false, AutoSwitchTabs = false });
        Assert.False(display.PageNumbers);
        Assert.False(settings.ShowPageNumbers || settings.AutoSwitchTabs);
        Assert.Equal(1, saves);

        display.NotesHidden = true; // view state only
        Assert.True(display.NotesHidden);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void FavoritesAreStoredClearedAndNamed()
    {
        var favorites = model.Favorites;
        int rebuilt = 0;
        favorites.PropertyChanged += (_, _) => rebuilt++;
        Assert.Equal(4, favorites.Slots.Count);
        Assert.Null(favorites.Slots[3]); // the defaults fill three of four
        Assert.Equal(Strings.Get("favorites.emptyTip"), FavoritesViewModel.Tip(null, 0));

        var pen = new AppSettings.FavoriteSettings { Tool = ToolKind.Pen, Style = new() { WidthPt = 2.04 } };
        favorites.Store(0, pen);
        var underline = new AppSettings.FavoriteSettings { Tool = ToolKind.FreeHighlight, MarkKind = TextMarkupKind.Underline, Style = new() { WidthPt = 1.5 } };
        favorites.Store(2, underline);
        Assert.Same(pen, favorites.Slots[0]);
        Assert.Equal(2, rebuilt);
        Assert.Equal(2, saves);
        Assert.Equal(Strings.Format("favorites.tip", ("name", Strings.Get("ribbon.pen")), ("width", 2.0), ("key", 1)), FavoritesViewModel.Tip(pen, 0));
        Assert.Equal(Strings.Format("favorites.tip", ("name", Strings.Get("markup.underline")), ("width", 1.5), ("key", 3)), FavoritesViewModel.Tip(underline, 2));
        Assert.Equal(AnnotationKind.Ink, FavoritesViewModel.Kind(pen));
        Assert.Equal(AnnotationKind.TextMarkup, FavoritesViewModel.Kind(underline));
        Assert.Equal(AnnotationKind.FreeHighlight, FavoritesViewModel.Kind(new() { Tool = ToolKind.FreeHighlight }));

        favorites.Clear(0);
        Assert.Null(favorites.Slots[0]);
        Assert.Equal(3, saves);
    }
}
