using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Session;

namespace PdfNotes.ViewModels;

/// <summary>
/// What the main window's view does for <see cref="MainViewModel"/>: the parts of the document lifetime that touch the
/// canvas, the page list and other controls. The view model keeps the title, the status line and the undo tips.
/// </summary>
public interface IMainView
{
    /// <summary>Commits a text box being edited into the document.</summary>
    void FinishTextEditing();

    /// <summary>Cancels a canvas operation in progress.</summary>
    void CancelTool();

    /// <summary>The open document is about to be replaced: remember its state.</summary>
    void DocumentLeaving(OpenDocument? previous);

    /// <summary>Shows <paramref name="next"/> in place of <paramref name="previous"/>, then releases the previous one.</summary>
    void DocumentReplaced(OpenDocument? previous, OpenDocument next);

    /// <summary>The open document's model changed (an edit, Undo, Redo, a saved mark).</summary>
    void DocumentChanged();

    /// <summary>The loading card waits for the first pages: call <see cref="LoadingViewModel.PagesDrawn"/> once they are drawn.</summary>
    void WaitForFirstPages();

    /// <summary>A file was opened from <paramref name="path"/> (recent list, last position).</summary>
    void Opened(string path);

    /// <summary>The document was written to <paramref name="path"/> (recent list, backup state).</summary>
    void Saved(string path);

    /// <summary>Another window shows <paramref name="path"/>: bring it forward and return true (spec 4.6).</summary>
    bool ShowOpenElsewhere(string path);

    /// <summary>Busy or the document changed: commands may be enabled differently now.</summary>
    void CommandsChanged();

    /// <summary>Runs <paramref name="action"/> on the UI thread (at once when called there).</summary>
    void OnUiThread(Action action);

    /// <summary>The document canvas (V3).</summary>
    IDocumentViewport Viewport { get; }

    /// <summary>The page panel's list (V5).</summary>
    IPageListView Pages { get; }

    /// <summary>Whether the user is in the middle of something a backup must not interrupt (V6).</summary>
    IWindowCoordinator Window { get; }

    /// <summary>The canvas tools (V7).</summary>
    IToolView Tools { get; }
}

/// <summary>
/// The main window's screen state (P1): the document file flow, the tool switch, the title, the status line and the
/// undo/redo tips (V1); the ribbon tab, recent documents, update check, loading card, outline and other apps'
/// annotations (V2); search (V3); navigation, display and favorites (V4); the page panel (V5); backups, imports and exports (V6); tool styles and the tool switch (V7); selection commands, the text bar and the sticky note card (V8). Platform-free so the WPF window and later the Avalonia views share it.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDocumentHost
{
    private readonly IMainView view;

    /// <summary>The UI texts of this window's language.</summary>
    public Texts Texts { get; }

    private Texts texts => Texts;
    private readonly string appName;
    private string title;
    private string status = "";
    private string undoTip;
    private string redoTip;

    private readonly TimeProvider time;
    private readonly Action<AppSettings> saveSettings;

    /// <param name="saveSettings">Writes the app settings (tests pass one that writes nothing).</param>
    /// <param name="settingsFolder">The app's settings folder: backups, the backup store's records, the running windows' markers.</param>
    /// <param name="time">Clock and timers (tests pass a fake one).</param>
    /// <param name="texts">The UI texts (the app passes the language in use; default Korean).</param>
    /// <param name="checkUpdates">Asks for the latest release (tests pass a fake one).</param>
    /// <param name="clipboard">The system clipboard (page copies; tests pass a fake one).</param>
    /// <param name="pageDialogs">The shell's page windows (import, merge, export).</param>
    public MainViewModel(IMainView view, IEditorDialogs dialogs, AppSettings settings, string appName, Action<AppSettings> saveSettings, string settingsFolder,
        TimeProvider? time = null, Func<Task<UpdateResult>>? checkUpdates = null, IEditorClipboard? clipboard = null, IPageDialogs? pageDialogs = null,
        Texts? texts = null)
    {
        Texts = texts ?? Texts.Korean;
        undoTip = Texts.Get("ribbon.undoTip");
        redoTip = Texts.Get("ribbon.redoTip");
        this.view = view;
        this.appName = appName;
        this.time = time ?? TimeProvider.System;
        this.saveSettings = saveSettings;
        title = appName;
        Settings = settings;
        Workflow = new DocumentWorkflow(this, dialogs, Texts);
        Tools = new ToolSwitch(settings, SaveSettings);
        Ribbon = new RibbonViewModel(settings);
        Recent = new RecentViewModel(settings, SaveSettings);
        Update = new UpdateViewModel(settings, SaveSettings, dialogs, this.time, checkUpdates ?? (() => UpdateCheck.CheckAsync()), Texts);
        Loading = new LoadingViewModel(After, Texts);
        Outline = new OutlineViewModel(this);
        Foreign = new ForeignViewModel(this, view.OnUiThread);
        Search = new SearchViewModel(this, view);
        Navigation = new NavigationViewModel(this, view);
        Display = new DisplayViewModel(settings, SaveSettings);
        Favorites = new FavoritesViewModel(settings, SaveSettings, Texts);
        Pages = new PagesViewModel(this, view, dialogs, clipboard ?? NoClipboard.Instance);
        Selection = new SelectionViewModel(this, view, dialogs, clipboard ?? NoClipboard.Instance);
        TextStyle = new TextStyleViewModel(this, dialogs);
        NoteCard = new NoteCardViewModel(this);
        ImportExport = new ImportExportViewModel(this, view, dialogs, pageDialogs ?? NoPageDialogs.Instance);
        ToolStyles = new ToolStylesViewModel(this);
        ToolBar = new ToolBarViewModel(this, view);
        Backup = new BackupViewModel(this, view, dialogs, this.time, settingsFolder);
    }

    /// <summary>Page windows that always cancel (a view model made without them).</summary>
    private sealed class NoPageDialogs : IPageDialogs
    {
        public static readonly NoPageDialogs Instance = new();
        public string[]? ChoosePdfFiles(string title) => null;
        public Task<int[]?> ChooseImportPagesAsync(MergeFile file) => Task.FromResult<int[]?>(null);
        public Task<MergeChoice?> ChooseMergeAsync(IReadOnlyList<MergeFile> files, bool insert, Func<Task<IReadOnlyList<MergeFile>>> addFiles) => Task.FromResult<MergeChoice?>(null);
        public PageExportChoice? ChooseExport(int pageCount, string range, bool flatten) => null;
    }

    /// <summary>A clipboard that holds nothing (a view model made without one).</summary>
    private sealed class NoClipboard : IEditorClipboard
    {
        public static readonly NoClipboard Instance = new();
        public bool ContainsData(string format) => false;
        public string? GetData(string format) => null;
        public void SetData(string format, string data) { }
        public bool ContainsText() => false;
        public string GetText() => "";
        public void SetText(string text) { }
        public bool ContainsImage() => false;
        public byte[]? GetImagePng() => null;
    }

    public AppSettings Settings { get; }

    public RibbonViewModel Ribbon { get; }
    public RecentViewModel Recent { get; }
    public UpdateViewModel Update { get; }
    public LoadingViewModel Loading { get; }
    public OutlineViewModel Outline { get; }
    public ForeignViewModel Foreign { get; }
    public SearchViewModel Search { get; }
    public NavigationViewModel Navigation { get; }
    public DisplayViewModel Display { get; }
    public FavoritesViewModel Favorites { get; }
    public PagesViewModel Pages { get; }
    public ImportExportViewModel ImportExport { get; }
    public BackupViewModel Backup { get; }
    public ToolStylesViewModel ToolStyles { get; }
    public ToolBarViewModel ToolBar { get; }
    public SelectionViewModel Selection { get; }
    public TextStyleViewModel TextStyle { get; }
    public NoteCardViewModel NoteCard { get; }

    private OpenDocument? glyphDocument;
    private PageGlyphSource glyphs = new((PdfNotes.Pdf.Reading.DocumentSources?)null);

    /// <summary>PDF glyphs of the open document's source pages (spec 9.3), cached; replaced when another document is shown.</summary>
    public PageGlyphSource Glyphs
    {
        get
        {
            if (!ReferenceEquals(glyphDocument, Document)) { glyphDocument = Document; glyphs = new(Document?.Sources); }
            return glyphs;
        }
    }

    /// <summary>Selectable glyphs of a page in reading order; generated pages and pages without text have none (no OCR).</summary>
    public IReadOnlyList<PdfNotes.Core.Geometry.TextGlyph> PageGlyphs(int pageIndex) =>
        Document is not { } doc || pageIndex < 0 || pageIndex >= doc.Session.Current.Pages.Count ? [] : Glyphs.Glyphs(doc.Session.Current.Pages[pageIndex].Setup);

    public void SaveSettings() => saveSettings(Settings);

    /// <summary>Runs <paramref name="action"/> once on the UI thread after <paramref name="delay"/>; disposing cancels it.</summary>
    public IDisposable After(TimeSpan delay, Action action) =>
        time.CreateTimer(_ => view.OnUiThread(action), null, delay, Timeout.InfiniteTimeSpan);

    /// <summary>The first page of the document that shows source page <paramref name="sourceIndex"/>, or null when it was deleted.</summary>
    public int? PageShowing(int? sourceIndex)
    {
        if (sourceIndex is not { } wanted || Document is null) return null;
        var pages = Document.Session.Current.Pages;
        for (int i = 0; i < pages.Count; i++)
            if (pages[i].Setup.Source is { Kind: PdfNotes.Core.Models.PageSourceKind.Pdf, PdfPageIndex: { } index } && index == wanted) return i;
        return null;
    }

    /// <summary>Opens a recent document like 열기; a file that is gone is reported briefly and leaves the list.</summary>
    public async Task<bool> OpenRecentAsync(string path)
    {
        if (!File.Exists(path))
        {
            Recent.Remove(path);
            Status = texts.Format("recent.missing", ("name", Path.GetFileName(path)));
            return false;
        }
        return await Workflow.OpenFileAsync(path);
    }

    /// <summary>New, open, save, export and the unsaved-changes question (D14d).</summary>
    public DocumentWorkflow Workflow { get; }

    /// <summary>The current tool and the automatic selection visit (D14d).</summary>
    public ToolSwitch Tools { get; }

    /// <summary>The open document (null only before the first one is shown and after closing).</summary>
    public OpenDocument? Document => Workflow.Document;

    /// <summary>True while a file is being opened or saved; document-switching commands are disabled meanwhile.</summary>
    public bool IsBusy => Workflow.IsBusy;

    /// <summary>The window title: `name * - DeotPDF` (spec 4.2, the mark while unsaved).</summary>
    public string Title { get => title; private set => SetProperty(ref title, value); }

    /// <summary>The status line text.</summary>
    public string Status { get => status; set => SetProperty(ref status, value); }

    /// <summary>The undo button names the step it would take (spec 13.3, D14g): `실행 취소: 필기 삭제 (Ctrl+Z)`.</summary>
    public string UndoTip { get => undoTip; private set => SetProperty(ref undoTip, value); }

    public string RedoTip { get => redoTip; private set => SetProperty(ref redoTip, value); }

    /// <summary>Recomputes the title and the undo/redo tips from the open document.</summary>
    public void Refresh()
    {
        Title = Document is { } doc ? $"{doc.DisplayNameIn(texts)}{(doc.Session.IsDirty ? " *" : "")} - {appName}" : appName;
        if (Document?.Session is not { } session) return;
        UndoTip = session.UndoLabel is { } undo ? texts.Format("ribbon.undoNamed", ("name", texts.Get(undo))) : texts.Get("ribbon.undoTip");
        RedoTip = session.RedoLabel is { } redo ? texts.Format("ribbon.redoNamed", ("name", texts.Get(redo))) : texts.Get("ribbon.redoTip");
    }

    private void SessionStateChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, Document?.Session)) return;
        view.DocumentChanged();
        Refresh();
        view.CommandsChanged();
        _ = Foreign.RecountAsync(); // imported pages may bring other apps' annotations
        Search.Schedule(keep: true); // text edits, deleted or moved pages: results follow the document
    }

    // ----- the workflow's host -----

    void IDocumentHost.FinishTextEditing() => view.FinishTextEditing();
    void IDocumentHost.CancelTool() => view.CancelTool();

    void IDocumentHost.DocumentLeaving(OpenDocument? previous)
    {
        view.DocumentLeaving(previous);
        if (previous is not null) previous.Session.StateChanged -= SessionStateChanged;
    }

    void IDocumentHost.DocumentReplaced(OpenDocument? previous, OpenDocument next)
    {
        next.Session.StateChanged += SessionStateChanged;
        view.DocumentReplaced(previous, next);
        Navigation.DocumentReplaced();
        Outline.Load();
        Search.DocumentReplaced();
        Refresh();
        view.CommandsChanged();
        _ = Foreign.RecountAsync();
    }

    void IDocumentHost.SetStatus(string text) => Status = text;
    void IDocumentHost.Loading(string? name)
    {
        if (name is not null) Loading.Begin(name);
        else if (Loading.End()) view.WaitForFirstPages();
    }

    void IDocumentHost.StateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        Refresh();
        Foreign.Refresh();
        view.CommandsChanged();
    }

    void IDocumentHost.Opened(string path)
    {
        Recent.Add(path);
        view.Opened(path);
    }

    void IDocumentHost.Saved(string path)
    {
        Recent.Add(path);
        view.Saved(path);
    }
    bool IDocumentHost.ShowOpenElsewhere(string path) => view.ShowOpenElsewhere(path);
}
