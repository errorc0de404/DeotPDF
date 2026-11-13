using System.Collections.ObjectModel;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Session;
using PdfNotes.Editor.Tools;

namespace PdfNotes.ViewModels;

/// <summary>How AI writing is shown (MCP_LIVE_PLAN 6): a view state only, back to <see cref="Shown"/> for each document.</summary>
public enum AiNotesView { Shown, Hidden, Emphasized }

/// <summary>
/// One AI request of this run (MCP_LIVE_PLAN 6.1): the annotations it made (erasing "what it added" uses these, not the
/// batch id, which a later request may take over), when, from whom, and how many of its changes only Undo takes back
/// (the user's writing it changed or removed, page changes).
/// </summary>
public sealed record AiRun(string BatchId, DateTimeOffset Time, string Client, IReadOnlyList<Guid> Created, int UndoOnlyChanges)
{
    /// <summary>The run a request's operations make, given the model before them.</summary>
    public static AiRun From(string batchId, DateTimeOffset time, string client, DocumentModel before, IReadOnlyList<EditOperation> operations)
    {
        var created = operations.OfType<InsertAnnotationOperation>().Select(o => o.Annotation.Id)
            .Where(id => before.FindAnnotation(id) is null).Distinct().ToList();
        var touched = new HashSet<Guid>();
        int pages = 0;
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case ReplaceAnnotationOperation r: touched.Add(r.After.Id); break;
                case RemoveAnnotationOperation r: touched.Add(r.Annotation.Id); break;
                case InsertPageOperation or RemovePageOperation or ReplacePageSetupOperation: pages++; break;
            }
        }
        int users = touched.Count(id => !created.Contains(id) && before.FindAnnotation(id) is not null && !Authorship.IsAi(before, id));
        return new(batchId, time, client, created, users + pages);
    }
}

/// <summary>A row of the work list: one batch of AI writing still in the document, with this run's record when it has one.</summary>
public sealed class AiWorkItem : ObservableObject
{
    private bool isChecked;

    internal AiWorkItem(string batchId, AiRun? run, IReadOnlyList<Guid> ids, int firstPage, Rect2 bounds, string title, string detail, Action<AiWorkItem> go)
    {
        BatchId = batchId;
        Run = run;
        Ids = ids;
        FirstPage = firstPage;
        Bounds = bounds;
        Title = title;
        Detail = detail;
        GoCommand = new(() => go(this));
    }

    public string BatchId { get; }
    public AiRun? Run { get; }

    /// <summary>The AI writing of this batch now in the document.</summary>
    public IReadOnlyList<Guid> Ids { get; }

    /// <summary>Index of the first page with writing of this batch.</summary>
    public int FirstPage { get; }

    /// <summary>The writing's bounds on that page (canonical points).</summary>
    public Rect2 Bounds { get; }

    public string Title { get; }
    public string Detail { get; }

    /// <summary>A request of this run that changed the user's writing or the pages (only Undo takes those back).</summary>
    public bool Warning => Run is { UndoOnlyChanges: > 0 };

    public bool IsChecked { get => isChecked; set => SetProperty(ref isChecked, value); }

    public Command GoCommand { get; }
}

/// <summary>
/// The AI tab (MCP_LIVE_PLAN 6): show, hide or outline AI writing, erase it on this page or everywhere, select it, take
/// back what the last request added, and the work list beside the document. AI writing is what carries an <c>ai</c> tag;
/// it becomes the user's own once they change it (Authorship). Nothing here is saved except through the document.
/// </summary>
public sealed class AiNotesViewModel : ObservableObject
{
    private readonly MainViewModel main;
    private readonly IEditorDialogs dialogs;
    private readonly IDocumentViewport viewport;
    private readonly List<AiRun> runs = [];
    private AiNotesView view;
    private int count;
    private bool panelVisible;

    public AiNotesViewModel(MainViewModel main, IEditorDialogs dialogs, IDocumentViewport viewport)
    {
        this.main = main;
        this.dialogs = dialogs;
        this.viewport = viewport;
        ShowCommand = new(() => SetView(AiNotesView.Shown));
        HideCommand = new(() => SetView(AiNotesView.Hidden));
        EmphasizeCommand = new(() => SetView(AiNotesView.Emphasized));
        ErasePageCommand = new(ErasePage, () => count > 0);
        EraseAllCommand = new(EraseAllAsync, () => count > 0);
        SelectPageCommand = new(SelectPage, () => count > 0);
        EraseLastCommand = new(EraseLast, () => runs.Count > 0);
        TogglePanelCommand = new(() => PanelVisible = !PanelVisible);
        EraseCheckedCommand = new(EraseChecked, () => Items.Any(i => i.IsChecked));
        AdoptCheckedCommand = new(AdoptChecked, () => Items.Any(i => i.IsChecked));
        main.DocumentShown += (_, _) =>
        {
            runs.Clear(); // this run's records belong to the document they were made in
            view = AiNotesView.Shown;
            RaiseView();
            Refresh();
        };
        main.DocumentChanged += Refresh;
    }

    /// <summary>Raised when the view state changes: the canvas and the thumbnails draw again.</summary>
    public event Action? ViewChanged;

    // ----- the view state -----

    public AiNotesView View => view;
    public bool IsShown => view == AiNotesView.Shown;
    public bool IsHidden => view == AiNotesView.Hidden;
    public bool IsEmphasized => view == AiNotesView.Emphasized;

    public Command ShowCommand { get; }
    public Command HideCommand { get; }
    public Command EmphasizeCommand { get; }

    /// <summary>
    /// Shows, hides or outlines AI writing. Hidden writing is passed over by selection, the lasso and the erasers
    /// (AnnotationMask) and drawing tools do not show it again; the status bar says it is hidden.
    /// </summary>
    public void SetView(AiNotesView value)
    {
        if (view == value) return;
        bool thumbnails = view == AiNotesView.Hidden || value == AiNotesView.Hidden;
        main.Editing.FinishTextEditing();
        main.Editing.CancelTool();
        view = value;
        RaiseView();
        if (value == AiNotesView.Hidden) main.Selection.Attached?.Revalidate(); // hidden writing leaves the selection
        viewport.RefreshOverlays();
        ViewChanged?.Invoke();
        if (thumbnails) Fire.AndReport(main.Pages.InvalidateThumbnailsAsync());
    }

    private void RaiseView()
    {
        OnPropertyChanged(nameof(View));
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(IsEmphasized));
    }

    /// <summary>True when <paramref name="annotation"/> is AI writing the view hides now.</summary>
    public bool Hides(DocumentModel model, Annotation annotation) => view == AiNotesView.Hidden && Authorship.IsAi(model, annotation.Id);

    /// <summary>A page as the screen draws it: without the AI writing while it is hidden.</summary>
    public Page Visible(DocumentModel model, Page page)
    {
        if (view != AiNotesView.Hidden || model.Tags.IsEmpty || !page.Annotations.Any(a => Authorship.IsAi(model, a.Id))) return page;
        return page with { Annotations = [.. page.Annotations.Where(a => !Authorship.IsAi(model, a.Id))] };
    }

    // ----- the tab -----

    /// <summary>AI writing in the document now.</summary>
    public int Count => count;

    /// <summary>The tab shows while an AI client is connected or the document has AI writing (or this run's records).</summary>
    public bool TabVisible => main.LiveLink?.IsConnected == true || count > 0 || runs.Count > 0;

    public Command ErasePageCommand { get; }
    public AsyncCommand EraseAllCommand { get; }
    public Command SelectPageCommand { get; }
    public Command EraseLastCommand { get; }

    /// <summary>Follows the document (each edit, Undo, Redo) and the connection.</summary>
    public void Refresh()
    {
        var model = main.Document?.Session.Current;
        int now = model is null ? 0 : Authorship.AiAnnotations(model).Count();
        if (now != count) { count = now; OnPropertyChanged(nameof(Count)); }
        OnPropertyChanged(nameof(TabVisible));
        ErasePageCommand.Refresh();
        EraseAllCommand.Refresh();
        SelectPageCommand.Refresh();
        EraseLastCommand.Refresh();
        if (panelVisible) BuildItems();
        if (!TabVisible && main.Ribbon?.Current == RibbonTab.Ai) main.Ribbon.Current = RibbonTab.Home;
    }

    /// <summary>A request of this run made the document's AI writing (LiveLinkViewModel after its commit).</summary>
    public void Record(AiRun run)
    {
        runs.Add(run);
        Refresh();
    }

    /// <summary>This run's requests, oldest first.</summary>
    public IReadOnlyList<AiRun> Runs => runs;

    private int CurrentPage(DocumentModel model) => Math.Clamp(viewport.CurrentPageIndex, 0, Math.Max(0, model.Pages.Count - 1));

    private bool Execute(EditCommand? command)
    {
        if (command is null || main.Document is not { } doc) return false;
        main.Editing.FinishTextEditing();
        main.Editing.CancelTool();
        try { doc.Session.Execute(command); }
        catch (EditConflictException) { return false; }
        main.Selection.Attached?.Revalidate();
        return true;
    }

    /// <summary>이 페이지의 AI 필기 지우기: at once, one Undo unit, locked ones too.</summary>
    public void ErasePage()
    {
        if (main.Document is not { } doc || doc.Session.Current.Pages.Count == 0) return;
        var model = doc.Session.Current;
        var ids = Authorship.AiAnnotations(model, model.Pages[CurrentPage(model)].Id).Select(x => x.Annotation.Id).ToList();
        if (ids.Count == 0) { main.Status = main.Texts.Get("ai.nothingOnPage"); return; }
        if (Execute(Authorship.Erase(model, ids))) main.Status = main.Texts.Format("ai.erased", ("count", ids.Count));
    }

    /// <summary>모든 AI 필기 지우기: after one confirmation ("AI 필기 N개(M쪽)를 지울까요?").</summary>
    public async Task EraseAllAsync()
    {
        if (main.Document is not { } doc) return;
        var model = doc.Session.Current;
        var all = Authorship.AiAnnotations(model).ToList();
        if (all.Count == 0) return;
        int pages = all.Select(x => x.Page.Id).Distinct().Count();
        if (await dialogs.AskAsync(main.Texts.Format("ai.eraseAllConfirm", ("count", all.Count), ("pages", pages)), AskButtons.YesNo, NoticeKind.Warning) != Answer.Yes) return;
        if (!ReferenceEquals(main.Document, doc)) return;
        var current = doc.Session.Current;
        var ids = Authorship.AiAnnotations(current).Select(x => x.Annotation.Id).ToList();
        if (Execute(Authorship.Erase(current, ids))) main.Status = main.Texts.Format("ai.erased", ("count", ids.Count));
    }

    /// <summary>모두 선택: the AI writing of this page, selected so the user can change it (hidden writing shows again).</summary>
    public void SelectPage()
    {
        if (main.Document is not { } doc || doc.Session.Current.Pages.Count == 0) return;
        if (view == AiNotesView.Hidden) SetView(AiNotesView.Shown);
        var model = doc.Session.Current;
        var ids = Authorship.AiAnnotations(model, model.Pages[CurrentPage(model)].Id).Where(x => !x.Annotation.Locked).Select(x => x.Annotation.Id).ToList();
        if (ids.Count == 0) { main.Status = main.Texts.Get("ai.nothingOnPage"); return; }
        main.ToolBar.SetTool(ToolKind.Select);
        main.Selection.Tool.SetSelection(ids);
    }

    /// <summary>
    /// 마지막 AI 작업이 추가한 필기 지우기: the writing the last request of this run made that is still AI writing goes (a new
    /// Undo unit). What it changed of the user's writing or the pages only Ctrl+Z takes back; the status says how many.
    /// </summary>
    public void EraseLast()
    {
        if (runs.Count == 0 || main.Document is not { } doc) return;
        var run = runs[^1];
        var model = doc.Session.Current;
        var ids = run.Created.Where(id => Authorship.IsAi(model, id)).ToList();
        bool erased = ids.Count > 0 && Execute(Authorship.Erase(model, ids));
        string text = erased ? main.Texts.Format("ai.erased", ("count", ids.Count)) : main.Texts.Get("ai.nothingAdded");
        if (run.UndoOnlyChanges > 0) text += " " + main.Texts.Format("ai.undoOnly", ("count", run.UndoOnlyChanges));
        main.Status = text;
        runs.RemoveAt(runs.Count - 1);
        Refresh();
    }

    // ----- the work list (MCP_LIVE_PLAN 6.1) -----

    public bool PanelVisible
    {
        get => panelVisible;
        set
        {
            if (!SetProperty(ref panelVisible, value)) return;
            if (value) BuildItems();
        }
    }

    public Command TogglePanelCommand { get; }

    /// <summary>The batches of AI writing in the document, newest of this run first, then older ones by page.</summary>
    public ObservableCollection<AiWorkItem> Items { get; } = [];

    /// <summary>The list has no rows (the panel says so).</summary>
    public bool ItemsEmpty => Items.Count == 0;

    public Command EraseCheckedCommand { get; }
    public Command AdoptCheckedCommand { get; }

    private void BuildItems()
    {
        var model = main.Document?.Session.Current;
        var checkedBatches = Items.Where(i => i.IsChecked).Select(i => i.BatchId).ToHashSet();
        foreach (var old in Items) old.PropertyChanged -= ItemChanged;
        Items.Clear();
        if (model is not null)
        {
            var batches = new Dictionary<string, List<(int PageIndex, Annotation Annotation)>>();
            for (int i = 0; i < model.Pages.Count; i++)
                foreach (var annotation in model.Pages[i].Annotations)
                    if (model.Tags.TryGetValue(annotation.Id, out var tag) && tag.Author == AnnotationTag.AiAuthor)
                    {
                        string key = tag.BatchId ?? "";
                        if (!batches.TryGetValue(key, out var list)) batches[key] = list = [];
                        list.Add((i, annotation));
                    }
            var byBatch = runs.GroupBy(r => r.BatchId).ToDictionary(g => g.Key, g => g.Last());
            var ordered = batches.OrderByDescending(b => byBatch.TryGetValue(b.Key, out var r) ? r.Time : DateTimeOffset.MinValue)
                .ThenBy(b => b.Value.Min(x => x.PageIndex));
            foreach (var (batch, entries) in ordered)
            {
                var run = byBatch.GetValueOrDefault(batch);
                int first = entries.Min(x => x.PageIndex);
                var onFirst = entries.Where(x => x.PageIndex == first).Select(x => x.Annotation).ToList();
                var bounds = AnnotationGeometry.Bounds(onFirst, PdfNotes.Text.TextLayout.Bounds);
                var pages = entries.Select(x => x.PageIndex + 1).Distinct().Order().ToList();
                string where = pages.Count <= 3 ? string.Join(", ", pages.Select(p => $"p.{p}")) : $"p.{pages[0]}–{pages[^1]}";
                string title = run is not null ? main.Texts.Format("ai.itemRun", ("client", run.Client), ("time", run.Time.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture))) : Short(batch);
                string detail = main.Texts.Format("ai.itemDetail", ("pages", where), ("count", entries.Count));
                var item = new AiWorkItem(batch, run, [.. entries.Select(x => x.Annotation.Id)], first, bounds, title, detail, Go) { IsChecked = checkedBatches.Contains(batch) };
                item.PropertyChanged += ItemChanged;
                Items.Add(item);
            }
        }
        OnPropertyChanged(nameof(ItemsEmpty));
        EraseCheckedCommand.Refresh();
        AdoptCheckedCommand.Refresh();
    }

    private string Short(string batch) => batch.Length == 0 ? main.Texts.Get("ai.itemUnknown") : batch.Length > 24 ? batch[..24] + "…" : batch;

    private void ItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AiWorkItem.IsChecked)) return;
        EraseCheckedCommand.Refresh();
        AdoptCheckedCommand.Refresh();
    }

    private void Go(AiWorkItem item)
    {
        if (main.Document is not { } doc || item.FirstPage >= doc.Session.Current.Pages.Count) return;
        viewport.ScrollToPagePoint(item.FirstPage, new Point2(item.Bounds.X + item.Bounds.Width / 2, item.Bounds.Y + item.Bounds.Height / 2));
        Flashed?.Invoke(item.FirstPage, item.Bounds);
    }

    /// <summary>A place to mark for a moment after going to a work item (the canvas draws it like <c>live flash</c>).</summary>
    public event Action<int, Rect2>? Flashed;

    /// <summary>
    /// [지우기] on the checked rows: their AI writing goes, one Undo unit. A row of this run erases what its request made
    /// (still AI writing); an older row erases its batch.
    /// </summary>
    public void EraseChecked()
    {
        if (main.Document is not { } doc) return;
        var model = doc.Session.Current;
        var ids = Items.Where(i => i.IsChecked).SelectMany(i => i.Run is { } run ? run.Created.Where(id => Authorship.IsAi(model, id)) : i.Ids).Distinct().ToList();
        if (Execute(Authorship.Erase(model, ids))) main.Status = main.Texts.Format("ai.erased", ("count", ids.Count));
    }

    /// <summary>[내 필기로] on the checked rows: their AI writing becomes the user's own (the tags go).</summary>
    public void AdoptChecked()
    {
        if (main.Document is not { } doc) return;
        var ids = Items.Where(i => i.IsChecked).SelectMany(i => i.Ids).Distinct().ToList();
        if (Execute(Authorship.Adopt(doc.Session.Current, ids))) main.Status = main.Texts.Format("ai.adopted", ("count", ids.Count));
    }
}
