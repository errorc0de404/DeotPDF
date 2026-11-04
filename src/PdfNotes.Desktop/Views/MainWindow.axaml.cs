using Avalonia.Controls;
using Avalonia.Threading;
using PdfNotes.Core.Models;
using Page = PdfNotes.Core.Models.Page;
using PdfNotes.Desktop.Canvas;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Session;
using PdfNotes.ViewModels;
using SkiaSharp;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// The main window of the Avalonia app (roadmap P4). Like the WPF window it is a view of the P1 <see cref="MainViewModel"/>
/// (<see cref="IMainView"/>): the view model owns the document, the tools' state, the styles and the commands; this
/// window shows them and turns platform input into editor calls. A3: canvas tools and keys; A4: text boxes; A5: ribbon.
/// </summary>
public sealed partial class MainWindow : Window, IMainView, IPageListView, IPageThumbnailSource, IWindowCoordinator
{
    private readonly MainViewModel viewModel;
    private readonly DesktopDialogs dialogs;
    private PageRenderQueue<SKImage>? queue;

    public MainWindow() : this(AppSettings.Load(), saveSettings: null) { }

    /// <param name="saveSettings">How settings are stored (tests pass one that does nothing).</param>
    public MainWindow(AppSettings settings, Action<AppSettings>? saveSettings)
    {
        InitializeComponent();
        dialogs = new DesktopDialogs(this);
        viewModel = new MainViewModel(this, dialogs, settings, "DeotPDF", saveSettings: saveSettings ?? (s => s.Save()), clipboard: new MemoryClipboard());
        viewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Title)) Title = viewModel.Title; else if (e.PropertyName == nameof(MainViewModel.Status)) StatusText.Text = viewModel.Status; };
        Canvas.ViewChanged += (_, _) => ZoomText.Text = $"{Math.Round(Canvas.Zoom * 100)}%";
        InitializeTools();
        InitializeText();
        InitializeKeys();
        Closed += (_, _) => CloseDocument();
        viewModel.Workflow.Replace(OpenDocument.CreateNote(NewPageSpec.Default));
    }

    public MainViewModel ViewModel => viewModel;
    public DocumentCanvas DocumentCanvas => Canvas;
    public OpenDocument? Document => viewModel.Document;
    private AppSettings settings => viewModel.Settings;

    // ----- test hooks: replace modal UI when set (as the WPF window's) -----

    /// <summary>Answers questions instead of a dialog.</summary>
    public Func<string, AskButtons, Answer>? PromptOverride { get => dialogs.PromptOverride; set => dialogs.PromptOverride = value; }

    /// <summary>Returns the save path (null = cancelled) instead of the save dialog.</summary>
    public Func<SavePathRequest, string?>? SaveDialogOverride { get => dialogs.SaveDialogOverride; set => dialogs.SaveDialogOverride = value; }

    /// <summary>Receives messages instead of a dialog.</summary>
    public Action<string>? MessageSink { get => dialogs.MessageSink; set => dialogs.MessageSink = value; }

    // ----- document lifetime -----

    /// <summary>Opens a PDF (spec 4.2). On failure the current document stays.</summary>
    public Task<bool> OpenFileAsync(string path) => viewModel.Workflow.OpenFileAsync(path);

    /// <summary>Ctrl+S / Save As (spec 14.1).</summary>
    public Task<bool> SaveAsync(bool saveAs) => viewModel.Workflow.SaveAsync(saveAs);

    public Task<bool> SaveToAsync(string path) => viewModel.Workflow.SaveToAsync(path);

    private async Task OpenWithDialogAsync()
    {
        if (await dialogs.ChooseOpenPathAsync() is { } path) await OpenFileAsync(path);
    }

    /// <summary>Shows a document made elsewhere (tests: one built in memory).</summary>
    public void Show(OpenDocument opened) => viewModel.Workflow.Replace(opened);

    void IMainView.DocumentLeaving(OpenDocument? previous) { }

    void IMainView.DocumentReplaced(OpenDocument? previous, OpenDocument next)
    {
        var previousQueue = queue;
        queue = new PageRenderQueue<SKImage>(next.Renderer, PageImages.FromPdfium, action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));
        Canvas.SetDocument(next.Session.Current.Pages, queue);
        selectTool.Select(null);
        previousQueue?.Dispose();
        previous?.Dispose();
        Title = viewModel.Title;
        StatusText.Text = viewModel.Status;
    }

    void IMainView.DocumentChanged()
    {
        if (Document is not { } doc) return;
        Canvas.UpdatePages(doc.Session.Current.Pages);
        selectTool.Revalidate();
    }

    void IMainView.FinishTextEditing() => FinishTextEditing();
    void IMainView.CancelTool() => Canvas.CancelTool();
    void IMainView.WaitForFirstPages() { }
    void IMainView.Opened(string path) { }
    void IMainView.Saved(string path) { }
    bool IMainView.ShowOpenElsewhere(string path) => false;
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

    // The page list and its thumbnails come with A5.
    IReadOnlyCollection<Guid> IPageListView.SelectedPageIds => [];
    void IPageListView.Select(IReadOnlyCollection<Guid> ids) { }
    void IPageListView.FollowActive(PageItemViewModel item) { }
    IPageThumbnailSource IPageListView.Thumbnails => this;
    Task<PageThumbnail?> IPageThumbnailSource.RenderAsync(OpenDocument document, DocumentModel model, Page page, double width, bool notesHidden, CancellationToken cancel) =>
        Task.FromResult<PageThumbnail?>(null);

    bool IWindowCoordinator.InputPending => Canvas.IsToolActive || dialogs.IsOpen;

    private void CloseDocument()
    {
        Canvas.Detach();
        queue?.Dispose();
        queue = null;
        viewModel.Workflow.Detach()?.Dispose();
    }
}
