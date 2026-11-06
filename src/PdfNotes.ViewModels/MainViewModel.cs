using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Session;

namespace PdfNotes.ViewModels;

/// <summary>
/// The main window's screen state (P1): the document file flow, the tool switch, the title, the status line and the
/// undo/redo tips (V1); the ribbon tab, recent documents, update check, loading card, outline and other apps'
/// annotations (V2); search (V3); navigation, display and favorites (V4); the page panel (V5); backups, imports and exports (V6); tool styles and the tool switch (V7); selection commands, the text bar and the sticky note card (V8). Platform-free so the WPF window and later the Avalonia views share it.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDocumentHost
{
    private readonly ScreenServices screen;

    /// <summary>The UI texts of this window's language.</summary>
    public Texts Texts { get; }

    /// <summary>What commands need from the screen: its file pickers, own windows and menus.</summary>
    public IAppShell Shell => screen.Shell;

    /// <summary>The thread the screen runs on.</summary>
    public IUiThread Ui => screen.Ui;

    /// <summary>The document canvas (V3).</summary>
    public IDocumentViewport Viewport => screen.Viewport;

    /// <summary>The canvas's input, ended before a command changes the document under it.</summary>
    public IEditingSurface Editing => screen.Editing;

    /// <summary>Draws page thumbnails (V5).</summary>
    public IPageThumbnailSource Thumbnails => screen.Thumbnails;

    /// <summary>The window: input a backup waits for, and other windows (V6).</summary>
    public IWindowCoordinator Window => screen.Window;

    /// <summary>The shell's page windows (import, merge, export, blank page, resize).</summary>
    public IPageDialogs PageDialogs => screen.PageDialogs;

    /// <summary>Finds a recent document again (P5b).</summary>
    public IDocumentLocations Locations => screen.Locations;

    private Texts texts => Texts;
    private readonly string appName;
    private string title;
    private string status = "";
    private string undoTip;
    private string redoTip;

    private readonly TimeProvider time;
    private readonly Action<AppSettings> saveSettings;

    /// <summary>Made by <see cref="MainViewModels.Create"/>, which then puts its parts in.</summary>
    internal MainViewModel(AppServices app, ScreenServices screen)
    {
        this.screen = screen;
        Texts = app.Texts;
        undoTip = Texts.Get("ribbon.undoTip");
        redoTip = Texts.Get("ribbon.redoTip");
        appName = app.AppName;
        time = app.Time;
        saveSettings = app.SaveSettings;
        title = appName;
        Settings = app.Settings;
        Workflow = new DocumentWorkflow(this, screen.Dialogs, Texts);
        Tools = new ToolSwitch(Settings, SaveSettings);
    }

    /// <summary>The parts are in: what joins them.</summary>
    internal void Assembled()
    {
        Recent.PropertyChanged += (_, _) => recent?.Refresh(); // the recent button is off when the list empties
    }

    public AppSettings Settings { get; }

    public RibbonViewModel Ribbon { get; internal set; } = null!;
    public RecentViewModel Recent { get; internal set; } = null!;
    public UpdateViewModel Update { get; internal set; } = null!;
    public LoadingViewModel Loading { get; internal set; } = null!;
    public OutlineViewModel Outline { get; internal set; } = null!;
    public ForeignViewModel Foreign { get; internal set; } = null!;
    public SearchViewModel Search { get; internal set; } = null!;
    public NavigationViewModel Navigation { get; internal set; } = null!;
    public DisplayViewModel Display { get; internal set; } = null!;
    public FavoritesViewModel Favorites { get; internal set; } = null!;
    public PagesViewModel Pages { get; internal set; } = null!;
    public ImportExportViewModel ImportExport { get; internal set; } = null!;
    public BackupViewModel Backup { get; internal set; } = null!;
    public ToolStylesViewModel ToolStyles { get; internal set; } = null!;
    public ToolBarViewModel ToolBar { get; internal set; } = null!;
    public SelectionViewModel Selection { get; internal set; } = null!;
    public TextStyleViewModel TextStyle { get; internal set; } = null!;
    public NoteCardViewModel NoteCard { get; internal set; } = null!;

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
        time.CreateTimer(_ => Ui.Run(action), null, delay, Timeout.InfiniteTimeSpan);

    /// <summary>The first page of the document that shows source page <paramref name="sourceIndex"/>, or null when it was deleted.</summary>
    public int? PageShowing(int? sourceIndex)
    {
        if (sourceIndex is not { } wanted || Document is null) return null;
        var pages = Document.Session.Current.Pages;
        for (int i = 0; i < pages.Count; i++)
            if (pages[i].Setup.Source is { Kind: PdfNotes.Core.Models.PageSourceKind.Pdf, PdfPageIndex: { } index } && index == wanted) return i;
        return null;
    }

    /// <summary>Opens a recent document like 열기; one that is gone (or no longer allowed) is reported briefly and leaves the list.</summary>
    public async Task<bool> OpenRecentAsync(string address)
    {
        if (Locations.Find(address) is not { } location)
        {
            Recent.Remove(address);
            Status = texts.Format("recent.missing", ("name", Locations.NameOf(address)));
            return false;
        }
        return await Workflow.OpenAsync(location);
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

    // ----- commands of the quick bar and the 파일 tab (R4b) -----

    public AsyncCommand OpenCommand => open ??= new(async () => { if (await Shell.ChooseDocumentAsync() is { } location) await Workflow.OpenAsync(location); }, () => !IsBusy);
    public AsyncCommand NewCommand => @new ??= new(() => NewNoteAsync(), () => !IsBusy);
    public AsyncCommand SaveCommand => save ??= new(() => Workflow.SaveAsync(saveAs: false), () => !IsBusy);
    public AsyncCommand SaveAsCommand => saveAs ??= new(() => Workflow.SaveAsync(saveAs: true), () => !IsBusy);
    public Command RecentCommand => recent ??= new(Shell.ShowRecentMenu, () => !IsBusy && Recent.HasFiles);
    public AsyncCommand MergeCommand => merge ??= new(() => ImportExport.MergeAsync(), () => !IsBusy);
    public AsyncCommand ExportFlattenedCommand => exportFlattened ??= new(() => ImportExport.ExportPagesAsync(allPages: true, flatten: true), () => !IsBusy); // the export window, preset (spec 11.10)
    public AsyncCommand ExportPagesCommand => exportPages ??= new(() => ImportExport.ExportPagesAsync(allPages: true), () => !IsBusy);
    public Command SettingsCommand => settingsCommand ??= new(Shell.ShowSettings);
    public Command UndoCommand => undo ??= new(() => UndoOrRedo(redo: false), () => Document?.Session.CanUndo == true);
    public Command RedoCommand => redo ??= new(() => UndoOrRedo(redo: true), () => Document?.Session.CanRedo == true);
    private AsyncCommand? open, @new, save, saveAs, merge, exportFlattened, exportPages;
    private Command? recent, settingsCommand, undo, redo;

    // ----- commands of the 보기 tab (R4c) -----

    public Command HideNotesCommand => hideNotes ??= new(() => SetNotesHidden(!Display.NotesHidden));
    public Command SearchCommand => search ??= new(() => Search.OpenCommand.Execute());
    public Command OpenUpdatePageCommand => openUpdatePage ??= new(() => Shell.OpenWebPage(Update.Url));

    /// <summary>필기 › 이미지 (R4d).</summary>
    public AsyncCommand InsertImageCommand => field ??= new(Shell.InsertImageFromFileAsync);

    /// <summary>A command from the ribbon ends what the canvas is doing: the text box being edited, a press in progress.</summary>
    public void EndInput()
    {
        Editing.FinishTextEditing();
        Editing.CancelTool();
    }
    private Command? hideNotes, search, openUpdatePage;

    /// <summary>파일 › 새 노트: the page size and background, then a new unsaved note (spec 4.1).</summary>
    public async Task<bool> NewNoteAsync() => !IsBusy && Shell.ChooseNewNote() is { } spec && await Workflow.CreateNoteAsync(spec);

    /// <summary>The busy state, the document's undo history or the recent list changed: buttons ask their commands again.</summary>
    public void RefreshCommands()
    {
        foreach (var command in new[] { open, @new, save, saveAs, merge, exportFlattened, exportPages }) command?.Refresh();
        foreach (var command in new[] { recent, undo, redo }) command?.Refresh();
    }

    /// <summary>Recomputes the title and the undo/redo tips from the open document.</summary>
    public void Refresh()
    {
        Title = Document is { } doc ? $"{doc.DisplayNameIn(texts)}{(doc.Session.IsDirty ? " *" : "")} - {appName}" : appName;
        if (Document?.Session is not { } session) return;
        UndoTip = session.UndoLabel is { } undo ? texts.Format("ribbon.undoNamed", ("name", texts.Get(undo))) : texts.Get("ribbon.undoTip");
        RedoTip = session.RedoLabel is { } redo ? texts.Format("ribbon.redoNamed", ("name", texts.Get(redo))) : texts.Get("ribbon.redoTip");
    }

    // ----- what the screen follows (R4f) -----

    /// <summary>The open document is about to be replaced: the screen remembers its state (its position, an open note).</summary>
    public event Action<OpenDocument?>? DocumentLeaving;

    /// <summary>
    /// The second document is shown in place of the first: the screen shows its pages. The first is released after this.
    /// </summary>
    public event Action<OpenDocument?, OpenDocument>? DocumentShown;

    /// <summary>The open document's model changed (an edit, Undo, Redo, a saved mark): the canvas shows it.</summary>
    public event Action? DocumentChanged;

    /// <summary>A file was opened from this path (the last position is restored).</summary>
    public event Action<string>? FileOpened;

    /// <summary>The document was written to this path (the backup starts over).</summary>
    public event Action<string>? FileSaved;

    private void SessionStateChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, Document?.Session)) return;
        DocumentChanged?.Invoke();
        Pages.Refresh(); // the rows follow the pages (after the canvas compared them with the model)
        Refresh();
        RefreshCommands();
        Pages.CommandsChanged();
        Outline.Refresh(); // items of deleted pages are dimmed
        _ = Foreign.RecountAsync(); // imported pages may bring other apps' annotations
        Search.Schedule(keep: true); // text edits, deleted or moved pages: results follow the document
    }

    // ----- the workflow's host -----

    void IDocumentHost.FinishTextEditing() => Editing.FinishTextEditing();
    void IDocumentHost.CancelTool() => Editing.CancelTool();

    void IDocumentHost.DocumentLeaving(OpenDocument? previous)
    {
        DocumentLeaving?.Invoke(previous);
        NoteCard.Commit(); // an open sticky note is finished first (spec 24.9)
        if (Display.NotesHidden) SetNotesHidden(false); // a new or opened document shows its notes (spec 24.10)
        if (previous is not null) previous.Session.StateChanged -= SessionStateChanged;
    }

    void IDocumentHost.DocumentReplaced(OpenDocument? previous, OpenDocument next)
    {
        next.Session.StateChanged += SessionStateChanged;
        DocumentShown?.Invoke(previous, next);
        previous?.Dispose();
        Pages.DocumentReplaced(); // after the canvas shows the new pages: the active page is the one in view
        Navigation.DocumentReplaced();
        Navigation.ResetPageInput();
        Navigation.ResetZoomInput();
        Outline.Load();
        Search.DocumentReplaced();
        ToolStyles.Refresh();
        Refresh();
        RefreshCommands();
        _ = Foreign.RecountAsync();
    }

    void IDocumentHost.SetStatus(string text) => Status = text;
    void IDocumentHost.Loading(string? name)
    {
        if (name is not null) Loading.Begin(name);
        else Loading.End(); // a shown card waits for the first pages (LoadingViewModel.PagesAwaited)
    }

    void IDocumentHost.StateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        Refresh();
        Foreign.Refresh();
        RefreshCommands();
        Pages.CommandsChanged();
    }

    void IDocumentHost.Opened(string path)
    {
        Recent.Add(path);
        FileOpened?.Invoke(path);
    }

    void IDocumentHost.Saved(string path)
    {
        Recent.Add(path);
        FileSaved?.Invoke(path);
    }
    bool IDocumentHost.ShowOpenElsewhere(string path) => Window.ShowOpenElsewhere(path);
}
