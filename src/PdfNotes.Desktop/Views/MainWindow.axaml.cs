using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PdfNotes.Core.Models;
using PdfNotes.Desktop.Canvas;
using PdfNotes.Desktop.Themes;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Session;
using PdfNotes.ViewModels;
using SkiaSharp;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// The main window of the Avalonia app (roadmap P4). Like the WPF window it is a view of the P1 <see cref="MainViewModel"/>
/// (<see cref="IMainView"/>): the view model owns the document, the tools' state, the styles and the commands; this
/// window shows them and turns platform input into editor calls. A3: canvas tools and keys; A4: text boxes; A5: the
/// ribbon, the style bars, the page panel, search, outline, notes, dialogs and the rest of the WPF window's frame.
/// </summary>
public sealed partial class MainWindow : Window, IMainView, IWindowCoordinator
{
    private readonly MainViewModel viewModel;
    private readonly IDesktopDialogs dialogs;
    private readonly DesktopClipboard clipboard;
    private PageRenderQueue<SKImage>? queue;
    private bool closeConfirmed;
    private readonly string appFolder;

    public MainWindow() : this(AppSettings.Load(), saveSettings: null) { }

    /// <param name="saveSettings">How settings are stored (tests pass one that does nothing).</param>
    /// <param name="appFolder">The settings folder for backups and the running windows' markers (tests pass their own).</param>
    /// <param name="dialogs">Makes the window's modal UI for it (default <see cref="DesktopDialogs"/>; tests pass a scripted one).</param>
    public MainWindow(AppSettings settings, Action<AppSettings>? saveSettings, string? appFolder = null, Func<Window, IDesktopDialogs>? dialogs = null)
    {
        this.appFolder = appFolder ?? AppSettings.Folder;
        InitializeComponent();
        this.dialogs = dialogs?.Invoke(this) ?? new DesktopDialogs(this);
        clipboard = new DesktopClipboard(this);
        viewModel = new MainViewModel(this, this.dialogs, settings, App.DisplayName, saveSettings: saveSettings ?? (s => s.Save()), clipboard: clipboard, pageDialogs: this,
            backupFolder: this.appFolder);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Title)) Title = viewModel.Title;
            else if (e.PropertyName == nameof(MainViewModel.Status)) StatusText.Text = viewModel.Status;
            else if (e.PropertyName is nameof(MainViewModel.UndoTip) or nameof(MainViewModel.RedoTip)) UpdateCommands();
        };
        RestoreWindowPlacement();
        Canvas.SetMode(settings.ViewMode);
        Canvas.ViewChanged += (_, _) => { viewModel.Navigation.ViewChanged(); UpdateViewStatus(); };
        viewModel.Navigation.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NavigationViewModel.Mode)) UpdateViewStatus(); };
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
        InitializeLoading();
        InitializeForeign();
        InitializeUpdates();
        InitializeBackup();
        Closing += WindowClosing;
        Closed += (_, _) => WindowClosed();
        Activated += (_, _) => viewModel.Pages.CommandsChanged(); // another window may have copied pages meanwhile (spec 11.4, D14g)
        Deactivated += (_, _) => { Canvas.SetTemporaryHand(false); EndEyedropper(); };
        viewModel.Workflow.Replace(OpenDocument.CreateNote(NewPageSpec.Default));
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

    private async Task OpenWithDialogAsync()
    {
        if (busy) return;
        if (await dialogs.ChooseOpenPathAsync() is { } path) await OpenFileAsync(path);
    }

    /// <summary>파일 › 새 노트: the page size and background, then a new unsaved note (spec 4.1).</summary>
    public async Task<bool> NewNoteAsync()
    {
        if (busy) return false;
        var dialog = new NewNoteDialog();
        return dialogs.ShowWindow(dialog) && dialog.Result is { } spec && await CreateNoteAsync(spec);
    }

    /// <summary>Shows a document made elsewhere (tests: one built in memory).</summary>
    public void Show(OpenDocument opened) => viewModel.Workflow.Replace(opened);

    void IMainView.DocumentLeaving(OpenDocument? previous)
    {
        RememberPosition(previous);
        CloseNote();
        if (viewModel.Display.NotesHidden) SetNotesHidden(false); // a new or opened document shows its notes (spec 24.10)
    }

    void IMainView.DocumentReplaced(OpenDocument? previous, OpenDocument next)
    {
        linkDocument = next;
        sourceLinks.Clear();
        var previousQueue = queue;
        queue = new PageRenderQueue<SKImage>(next.Renderer, PageImages.FromPdfium, action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));
        Canvas.SetDocument(next.Session.Current.Pages, queue);
        selectTool.Select(null);
        viewModel.Pages.DocumentReplaced();
        previousQueue?.Dispose();
        previous?.Dispose();
        backup.Reset();
        ShowingFile();
        Title = viewModel.Title;
        StatusText.Text = viewModel.Status;
        UpdateViewStatus();
        UpdateStyleBar();
        UpdateCommands();
    }

    void IMainView.DocumentChanged()
    {
        if (Document is not { } doc) return;
        Canvas.UpdatePages(doc.Session.Current.Pages);
        selectTool.Revalidate();
        // A page structure change makes glyph carets of the text selection refer to another page.
        var thumbs = viewModel.Pages.Items;
        if (!thumbs.Select(t => t.PageId).SequenceEqual(doc.Session.Current.Pages.Select(p => p.Id)) ||
            thumbs.Any(t => t.Rendered is { } r && doc.Session.Current.GetPage(t.PageId).Setup != r.Setup)) selectTool.ClearTextSelection();
        viewModel.Pages.Refresh();
        RefreshOutline();
        UpdateStyleBar();
        UpdateCommands();
    }

    void IMainView.FinishTextEditing() => FinishTextEditing();
    void IMainView.CancelTool() => Canvas.CancelTool();
    void IMainView.Opened(string path) => RestorePosition(path);
    void IMainView.Saved(string path)
    {
        backup.Saved();
        ShowingFile();
    }

    /// <summary>The file is open in another window (spec 4.6): that window comes forward and this one keeps its document.</summary>
    bool IMainView.ShowOpenElsewhere(string path)
    {
        if (BackupStore.WindowShowing(appFolder, path, backup.Store) is not { } other) return false;
        (BringOverride ?? Platform.WindowActivation.Bring)(other);
        return true;
    }
    void IMainView.CommandsChanged() => UpdateCommands();

    void IMainView.OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    IDocumentViewport IMainView.Viewport => Canvas;
    IPageListView IMainView.Pages => this;
    IWindowCoordinator IMainView.Window => this;
    IToolView IMainView.Tools => this;

    // ----- commands -----

    /// <summary>Undo/Redo and the busy state (spec 13.3): the buttons follow the document and name the step.</summary>
    private void UpdateCommands()
    {
        UndoButton.IsEnabled = Document?.Session.CanUndo == true;
        RedoButton.IsEnabled = Document?.Session.CanRedo == true;
        ToolTip.SetTip(UndoButton, viewModel.UndoTip);
        ToolTip.SetTip(RedoButton, viewModel.RedoTip);
        foreach (var idle in new Control[] { QuickOpenButton, QuickSaveButton, NewButton, OpenButton, SaveButton, SaveAsButton, ExportFlattenedButton, ExportPagesFileButton, MergePdfsButton, RecentButton })
            idle.IsEnabled = !busy;
        RecentButton.IsEnabled = !busy && viewModel.Recent.HasFiles;
        UpdatePageCommands();
    }

    /// <summary>Ctrl+Z / Ctrl+Y (spec 13.2–13.3): while a text box is edited, only its own steps (<see cref="UndoTextAware"/>).</summary>
    public void Undo() => UndoTextAware(redo: false);

    public void Redo() => UndoTextAware(redo: true);

    // ----- the status bar: page and zoom -----

    private void InitializeStatusBar()
    {
        PreviousPageButton.Click += (_, _) => viewModel.Navigation.PreviousPage();
        NextPageButton.Click += (_, _) => viewModel.Navigation.NextPage();
        PageBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { viewModel.Navigation.GoToPageNumber(PageBox.Text ?? ""); Canvas.Focus(); UpdateViewStatus(); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Escape) { Canvas.Focus(); e.Handled = true; }
        };
        PageBox.LostFocus += (_, _) => UpdateViewStatus();
        ZoomButton.Click += (_, _) => OpenZoomMenu();
    }

    /// <summary>The status bar's page and zoom (<see cref="NavigationViewModel"/>); a box being typed in keeps its text.</summary>
    private void UpdateViewStatus()
    {
        var navigation = viewModel.Navigation;
        if (!PageBox.IsKeyboardFocusWithin) PageBox.Text = navigation.PageNumberText;
        PageCountText.Text = navigation.PageCountText;
        if (!ZoomBox.IsKeyboardFocusWithin) ZoomBox.Text = navigation.ZoomText;
        ZoomText.Text = navigation.ZoomText;
        ContinuousButton.IsChecked = navigation.Mode == ViewMode.Continuous;
        SinglePageButton.IsChecked = navigation.Mode == ViewMode.SinglePage;
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
                case nameof(DisplayViewModel.FavoritesShown): FavoritesGroup.IsVisible = display.FavoritesShown; break;
                case nameof(DisplayViewModel.PageNumbers) or nameof(DisplayViewModel.NumberSize): Canvas.PageNumberSize = display.PageNumbers ? display.PageNumberDip : 0; break;
            }
        };
        FavoritesGroup.IsVisible = display.FavoritesShown;
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

    private void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closeConfirmed) { RememberWindowPlacement(); return; } // while the window still has its screen
        FinishTextEditing();
        CloseNote();
        switch (viewModel.Workflow.CloseStep(backup.IsRunning))
        {
            case CloseStep.Close:
                RememberWindowPlacement();
                return;
            case CloseStep.WaitForSave:
                e.Cancel = true; // a save is writing the file; close after it finishes
                return;
            case CloseStep.WaitForBackup:
                e.Cancel = true; // an automatic backup is writing: close when it is done
                Dispatcher.UIThread.Post(async () => { while (backup.IsRunning) await Task.Delay(50); Close(); });
                return;
        }
        e.Cancel = true;
        Dispatcher.UIThread.Post(async () =>
        {
            if (!await viewModel.Workflow.ConfirmDiscardChangesAsync()) return;
            closeConfirmed = true;
            Close();
        });
    }

    private void WindowClosed()
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
