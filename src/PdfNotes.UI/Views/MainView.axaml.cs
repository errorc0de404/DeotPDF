using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PdfNotes.Core.Models;
using PdfNotes.UI.Canvas;
using PdfNotes.UI.Themes;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Session;
using PdfNotes.ViewModels;
using SkiaSharp;

namespace PdfNotes.UI.Views;

/// <summary>
/// What the view needs from the window or screen around it (roadmap P5a): the desktop window gives its id and brings other
/// windows forward; on Android there is one screen and no other window.
/// </summary>
public interface IViewHost
{
    /// <summary>The id another DeotPDF process uses to bring this window forward (0 when there is none).</summary>
    long WindowId { get; }

    /// <summary>Brings another DeotPDF window forward (spec 4.6).</summary>
    void Bring(long windowId);

    /// <summary>Shows the system's touch keyboard for a text box started with a finger; true when it was asked to show.</summary>
    bool ShowTouchKeyboard();
}

/// <summary>A screen without other windows or a touch keyboard to ask for (tests; Android shows its keyboard itself).</summary>
public sealed class NoViewHost : IViewHost
{
    public static readonly NoViewHost Instance = new();
    public long WindowId => 0;
    public void Bring(long windowId) { }
    public bool ShowTouchKeyboard() => false;
}

/// <summary>
/// The app's screen (roadmap P4, P5a): one view the desktop window and the Android activity both show. It is a view of the
/// P1 <see cref="MainViewModel"/>: the view model owns the document, the tools' state, the styles and the commands, and the
/// controls are bound to it (R4); the view follows its events (R4f) and turns platform input into editor calls. What
/// belongs to a desktop window (its placement, closing, other windows) is the window's (<see cref="IViewHost"/>).
/// </summary>
public sealed partial class MainView : UserControl, IEditingSurface, IWindowCoordinator, IAppShell, IUiThread
{
    private readonly MainViewModel viewModel;
    private readonly IDesktopDialogs dialogs;
    private readonly DesktopClipboard clipboard;
    private PageRenderQueue<SKImage>? queue;
    private readonly string appFolder;
    private readonly IViewHost host;
    private TopLevel? topLevel;

    /// <param name="app">The settings, how they are stored, the settings folder (backups, the running windows' markers, language
    /// packs) and the language; tests pass their own.</param>
    /// <param name="dialogs">The modal UI (questions, messages, file pickers, app windows; tests pass a scripted one).</param>
    /// <param name="host">The window or screen around the view (default: none).</param>
    /// <param name="compose">Puts the view models together from the app's and this view's services (R4g; default <see cref="MainViewModels.Create"/>).</param>
    /// <param name="locations">Finds recent documents again (default: files; Android: content URIs too).</param>
    public MainView(AppServices app, IDesktopDialogs dialogs, IViewHost? host = null, Func<AppServices, ScreenServices, MainViewModel>? compose = null,
        IDocumentLocations? locations = null)
    {
        appFolder = app.SettingsFolder;
        var settings = app.Settings;
        this.host = host ?? NoViewHost.Instance;
        InitializeComponent();
        this.dialogs = dialogs;
        clipboard = new DesktopClipboard(this);
        viewModel = (compose ?? MainViewModels.Create)(app,
            new ScreenServices(this.dialogs, Canvas, this) { Thumbnails = this, Window = this, Ui = this, Shell = this, Clipboard = clipboard, PageDialogs = this, Locations = locations ?? FileLocations.Instance });
        DataContext = viewModel; // the window's controls are bound (R4)
        viewModel.DocumentLeaving += RememberPosition;
        viewModel.DocumentShown += DocumentShown;
        viewModel.DocumentChanged += DocumentChanged;
        viewModel.FileOpened += RestorePosition;
        viewModel.FileSaved += _ => { backup.Saved(); ShowingFile(); };
        viewModel.Loading.PagesAwaited += WaitForFirstPages;
        Canvas.SetMode(settings.ViewMode);
        Canvas.ViewChanged += (_, _) => viewModel.Navigation.ViewChanged();
        InitializeDisplay();
        InitializeTools();
        InitializeStyleBar();
        InitializeText();
        InitializeTextBar();
        InitializeKeys();
        InitializeRibbon();
        InitializeStatusBar();
        InitializeLinks();
        InitializePagePanel();
        InitializeOutline();
        InitializeSearch();
        InitializeNotes();
        InitializeSelectionCommands();
        InitializeDrop();
        InitializeBackup();
        viewModel.Workflow.Replace(OpenDocument.CreateNote(NewPageSpec.Default));
    }

    // ----- the window or screen around the view -----

    /// <summary>The keys go through the whole window or screen (also with nothing focused), and its scale is the thumbnails'.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        topLevel.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        topLevel.AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
        topLevel.ScalingChanged += ScalingChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (topLevel is not null)
        {
            topLevel.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
            topLevel.RemoveHandler(KeyUpEvent, OnWindowKeyUp);
            topLevel.ScalingChanged -= ScalingChanged;
        }
        topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Another monitor scale: thumbnails at its pixels (D13e).</summary>
    private void ScalingChanged(object? sender, EventArgs e) => _ = viewModel.Pages.InvalidateThumbnailsAsync();

    /// <summary>Device pixels per DIP of the window or screen.</summary>
    private double RenderScaling => topLevel?.RenderScaling ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private IFocusManager? FocusManager => (topLevel ?? TopLevel.GetTopLevel(this))?.FocusManager;

    /// <summary>The window came to the front: another window may have copied pages meanwhile (spec 11.4, D14g).</summary>
    public void HostActivated() => viewModel.Pages.CommandsChanged();

    /// <summary>The window went to the back: a held Space hand and the eyedropper end.</summary>
    public void HostDeactivated()
    {
        Canvas.SetTemporaryHand(false);
        EndEyedropper();
    }

    public MainViewModel ViewModel => viewModel;
    public DocumentCanvas DocumentCanvas => Canvas;
    public OpenDocument? Document => viewModel.Document;
    private AppSettings settings => viewModel.Settings;
    private bool busy => viewModel.IsBusy;

    /// <summary>The window's modal UI (questions, messages, file dialogs, app windows).</summary>
    public IDesktopDialogs Dialogs => dialogs;

    /// <summary>The system clipboard of this window (tests use an in-memory one).</summary>
    public DesktopClipboard EditorClipboard => clipboard;

    // ----- document lifetime -----

    /// <summary>Opens a PDF (spec 4.2). On failure the current document stays.</summary>
    public Task<bool> OpenFileAsync(string path) => viewModel.Workflow.OpenFileAsync(path);

    /// <summary>Ctrl+S / Save As (spec 14.1).</summary>
    public Task<bool> SaveAsync(bool saveAs) => viewModel.Workflow.SaveAsync(saveAs);

    public Task<bool> SaveToAsync(string path) => viewModel.Workflow.SaveToAsync(path);

    public Task<bool> CreateNoteAsync(NewPageSpec spec) => viewModel.Workflow.CreateNoteAsync(spec);

    private Task OpenWithDialogAsync() => viewModel.OpenCommand.RunAsync();

    /// <summary>파일 › 새 노트: the page size and background, then a new unsaved note (spec 4.1).</summary>
    public Task<bool> NewNoteAsync() => viewModel.NewNoteAsync();

    // ----- what the view model's commands need from this window (IAppShell) -----

    Task<DocumentLocation?> IAppShell.ChooseDocumentAsync() => dialogs.ChooseDocumentAsync();

    NewPageSpec? IAppShell.ChooseNewNote()
    {
        var dialog = new NewNoteDialog();
        return dialogs.ShowWindow(dialog) ? dialog.Result : null;
    }

    void IAppShell.ShowSettings() => ShowSettings();
    void IAppShell.ShowRecentMenu() => OpenRecentMenu();
    void IAppShell.ShowZoomMenu() => OpenZoomMenu();
    void IAppShell.OpenWebPage(string url) => OpenWebPage(url);
    Task IAppShell.InsertImageFromFileAsync() => InsertImageFromFileAsync();

    void IUiThread.Run(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    void IUiThread.Post(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>Shows a document made elsewhere (tests: one built in memory).</summary>
    public void Show(OpenDocument opened) => viewModel.Workflow.Replace(opened);

    // ----- what the window follows of the document (R4f) -----

    /// <summary>The canvas shows <paramref name="next"/>; the view model releases <paramref name="previous"/> after this.</summary>
    private void DocumentShown(OpenDocument? previous, OpenDocument next)
    {
        linkDocument = next;
        sourceLinks.Clear();
        var previousQueue = queue;
        queue = new PageRenderQueue<SKImage>(next.Renderer, PageImages.FromPdfium, action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));
        Canvas.SetDocument(next.Session.Current.Pages, queue);
        selectTool.Select(null);
        previousQueue?.Dispose();
        backup.Reset();
        ShowingFile();
    }

    /// <summary>The model changed: the canvas shows it; a page structure change ends the PDF text selection.</summary>
    private void DocumentChanged()
    {
        if (Document is not { } doc) return;
        Canvas.UpdatePages(doc.Session.Current.Pages);
        selectTool.Revalidate();
        // A page structure change makes glyph carets of the text selection refer to another page.
        var thumbs = viewModel.Pages.Items;
        if (!thumbs.Select(t => t.PageId).SequenceEqual(doc.Session.Current.Pages.Select(p => p.Id)) ||
            thumbs.Any(t => t.Rendered is { } r && doc.Session.Current.GetPage(t.PageId).Setup != r.Setup)) selectTool.ClearTextSelection();
        styles.Refresh();
    }

    void IEditingSurface.FinishTextEditing() => FinishTextEditing();
    void IEditingSurface.CancelTool() => Canvas.CancelTool();

    /// <summary>The file is open in another window (spec 4.6): that window comes forward and this one keeps its document.</summary>
    bool IWindowCoordinator.ShowOpenElsewhere(string path)
    {
        if (BackupStore.WindowShowing(appFolder, path, backup.Store) is not { } other) return false;
        (BringOverride ?? host.Bring)(other);
        return true;
    }
    // ----- commands -----

    /// <summary>Ctrl+Z / Ctrl+Y (spec 13.2–13.3): while a text box is edited, only its own steps (<see cref="MainViewModel.UndoOrRedo"/>).</summary>
    public void Undo() => viewModel.UndoOrRedo(redo: false);

    public void Redo() => viewModel.UndoOrRedo(redo: true);

    // ----- the status bar: page and zoom -----

    private void InitializeStatusBar()
    {
        // The page box: Enter goes there, Esc or leaving shows the current page again; both give the keys back to the pages.
        PageBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { viewModel.Navigation.CommitPageInput(); Canvas.Focus(); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Escape) { viewModel.Navigation.ResetPageInput(); Canvas.Focus(); e.Handled = true; }
        };
        PageBox.LostFocus += (_, _) => viewModel.Navigation.ResetPageInput();
    }

    /// <summary>The zoom menu opened last (tests).</summary>
    public ContextMenu? LastZoomMenu { get; private set; }

    /// <summary>A click on the zoom percentage: the presets and the two fits, the current zoom checked (spec 4.4).</summary>
    public ContextMenu OpenZoomMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.TopEdgeAlignedRight };
        foreach (var (percent, current) in viewModel.Navigation.ZoomMenu())
            menu.Items.Add(Menus.Item(percent.ToString(System.Globalization.CultureInfo.CurrentCulture) + "%", () => viewModel.Navigation.SetZoom(percent / 100.0), isChecked: current));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menus.Item(Strings.Get("view.fitWidth"), viewModel.Navigation.FitWidth));
        menu.Items.Add(Menus.Item(Strings.Get("view.fitPage"), viewModel.Navigation.FitPage));
        menu.Open(ZoomButton);
        return LastZoomMenu = menu;
    }

    // ----- display (dark pages, page numbers, favorites), theme -----

    private void InitializeDisplay()
    {
        var display = viewModel.Display;
        Canvas.DarkPages = display.DarkPages;
        Canvas.PageNumberSize = display.PageNumbers ? display.PageNumberDip : 0;
        display.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(DisplayViewModel.DarkPages): Canvas.DarkPages = display.DarkPages; _ = viewModel.Pages.InvalidateThumbnailsAsync(); break;
                case nameof(DisplayViewModel.PageNumbers) or nameof(DisplayViewModel.NumberSize): Canvas.PageNumberSize = display.PageNumbers ? display.PageNumberDip : 0; break;
            }
        };
    }

    /// <summary>Shows theme, accent color and page numbers now without storing them (settings window preview).</summary>
    public void PreviewDisplay(DisplayValues values)
    {
        if (Avalonia.Application.Current is { } app) DesktopTheme.Use(app, values.Theme, values.Accent);
        viewModel.Display.Preview(values);
    }

    /// <summary>Applies and stores the 화면 settings (an app setting: no Undo, the document stays unchanged).</summary>
    public void SetDisplay(DisplayValues values)
    {
        if (Avalonia.Application.Current is { } app) DesktopTheme.Use(app, values.Theme, values.Accent);
        viewModel.Display.Apply(values);
    }

    // ----- closing (spec 18) -----

    /// <summary>The window is asked to close: the text and the open note are finished, then the step to take.</summary>
    public CloseStep PrepareClose()
    {
        FinishTextEditing();
        CloseNote();
        return viewModel.Workflow.CloseStep(backup.IsRunning);
    }

    /// <summary>The window closed: the backup marker goes, the position is remembered and the document released.</summary>
    public void Detach()
    {
        EndBackup();
        RememberPosition(Document);
        viewModel.SaveSettings();
        viewModel.Search.Cancel();
        Canvas.Detach();
        queue?.Dispose();
        queue = null;
        viewModel.Workflow.Detach()?.Dispose();
    }

    // ----- small shared pieces -----

    /// <summary>A message box (the editor's dialogs).</summary>
    private void Tell(string text, NoticeKind kind = NoticeKind.Information) => dialogs.Tell(text, kind);

    private bool Confirm(string text, NoticeKind kind = NoticeKind.Question) => dialogs.Ask(text, AskButtons.YesNo, kind) == Answer.Yes;
}
