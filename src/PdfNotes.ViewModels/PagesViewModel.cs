using System.Collections.ObjectModel;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.ViewModels;

/// <summary>One row of the page panel: number, thumbnail and whether it is the active page (spec 11.1, 11.5).</summary>
public sealed class PageItemViewModel(Guid pageId) : ObservableObject
{
    private int number;
    private bool active;
    private object? image;
    private double width = PagesViewModel.ThumbnailWidth, height = PagesViewModel.ThumbnailWidth * 1.414;

    public Guid PageId { get; } = pageId;
    public int Number { get => number; set => SetProperty(ref number, value); }
    public bool IsActive { get => active; set => SetProperty(ref active, value); }

    /// <summary>The thumbnail, a platform image made by <see cref="IPageThumbnailSource"/> (null until drawn).</summary>
    public object? Image { get => image; set => SetProperty(ref image, value); }

    public double ImageWidth { get => width; set => SetProperty(ref width, value); }
    public double ImageHeight { get => height; set => SetProperty(ref height, value); }

    /// <summary>The model page the image shows (reference); a changed page gets a new image. Null: draw again.</summary>
    public Page? Rendered { get; set; }
}

/// <summary>A drawn thumbnail: the platform image, its size in DIPs, and whether every text box was laid out.</summary>
public sealed record PageThumbnail(object Image, double Width, double Height, bool Complete);

/// <summary>Draws page thumbnails (the PDF background and the app's notes) at the screen's pixels (P1 V5).</summary>
public interface IPageThumbnailSource
{
    /// <summary>
    /// The thumbnail of <paramref name="page"/>, <paramref name="width"/> DIPs wide, without notes when
    /// <paramref name="notesHidden"/>; null when cancelled. An unrenderable PDF page gives a white thumbnail.
    /// </summary>
    Task<PageThumbnail?> RenderAsync(OpenDocument document, DocumentModel model, Page page, double width, bool notesHidden, CancellationToken cancel);
}

/// <summary>The page list as the view model uses it: which rows are selected, and showing a selection.</summary>
public interface IPageListView
{
    /// <summary>The pages selected in the list (any order; may be empty).</summary>
    IReadOnlyCollection<Guid> SelectedPageIds { get; }

    /// <summary>Selects exactly these rows (without making one active: the view model does that).</summary>
    void Select(IReadOnlyCollection<Guid> ids);

    /// <summary>The view scrolled to another page: select that row when at most one row is selected, and bring it into view.</summary>
    void FollowActive(PageItemViewModel item);

    IPageThumbnailSource Thumbnails { get; }
}

/// <summary>What the resize and shrink dialog asks for (spec 11.6–11.8, D10d); the target IDs are fixed when it opens.</summary>
public sealed record PageTransformRequest(PageApplyScope Scope, double Percent, double WidthMm, double HeightMm, PageResizeMode Mode, int Alignment)
{
    public Point2? FreePosition { get; init; }
    public Guid[]? TargetPageIds { get; init; }
}

/// <summary>The commands of the page panel's context menu and the 페이지 tab (spec 11).</summary>
public enum PageCommand { InsertBlank, CopyWithNotes, CopyPageOnly, Paste, Delete, ClearNotes, Export, RotateRight, RotateLeft, Shrink, Resize }

/// <summary>A context menu row; <see cref="Text"/> null is a separator.</summary>
public sealed record PageMenuEntry(PageCommand Command, string? Text, bool Enabled = true)
{
    public static PageMenuEntry Separator { get; } = new(default, null);
}

/// <summary>
/// The page panel (spec 3.2-5, 11; P1 V5): rows in document order with thumbnails, the active page (insert target and
/// paste position, following the view when the panel is idle), the selection, and the page commands — add, insert,
/// copy (page + notes or page only), paste after the active page, delete, rotate, move, resize and shrink. Every
/// structure change is one Undo unit; page IDs are kept so Undo restores the same pages. Dragging rows is the view's.
/// </summary>
public sealed class PagesViewModel : ObservableObject
{
    /// <summary>Thumbnail width in DIPs; the height follows the displayed page.</summary>
    public const double ThumbnailWidth = 132;

    private readonly MainViewModel owner;
    private Texts texts => owner.Texts;
    private readonly IMainView view;
    private readonly IEditorDialogs dialogs;
    private readonly IEditorClipboard clipboard;
    private readonly ObservableCollection<PageItemViewModel> items = [];
    private PageClipboard? pageClipboard;
    private Guid? activePageId;
    private IDisposable? thumbTimer;
    private CancellationTokenSource? thumbCancel;
    private bool panelVisible;

    public PagesViewModel(MainViewModel owner, IMainView view, IEditorDialogs dialogs, IEditorClipboard clipboard)
    {
        this.owner = owner;
        this.view = view;
        this.dialogs = dialogs;
        this.clipboard = clipboard;
        panelVisible = owner.Settings.PagePanelVisible;
    }

    private OpenDocument? Document => owner.Document;
    private IPageListView List => view.Pages;

    public ObservableCollection<PageItemViewModel> Items => items;

    /// <summary>The view follows the panel rows (selection and scrolling) without that being a user's click.</summary>
    public bool Syncing { get; private set; }

    // ----- the panel -----

    /// <summary>The page list is shown (app setting, spec 3.2-5; the handle and the 페이지 tab button agree).</summary>
    public bool PanelVisible
    {
        get => panelVisible;
        set
        {
            if (!SetProperty(ref panelVisible, value)) return;
            owner.Settings.PagePanelVisible = value;
            owner.SaveSettings();
        }
    }

    /// <summary>The active page: insert target and paste position (spec 11.5). Follows the view when the panel is idle.</summary>
    public int ActivePageIndex =>
        Document is { } d && activePageId is { } id && d.Session.Current.IndexOfPage(id) is >= 0 and var i ? i : view.Viewport.CurrentPageIndex;

    /// <summary>Pages selected in the panel (at least the active page), in document order.</summary>
    public Guid[] SelectedPageIds
    {
        get
        {
            if (Document is not { } doc) return [];
            var selected = List.SelectedPageIds.ToHashSet();
            var pages = doc.Session.Current.Pages;
            if (selected.Count == 0 && pages.Count > 0) selected.Add(pages[Math.Clamp(ActivePageIndex, 0, pages.Count - 1)].Id);
            return [.. pages.Where(p => selected.Contains(p.Id)).Select(p => p.Id)];
        }
    }

    /// <summary>A new or opened document: no rows, no active page, no copied pages of the old one.</summary>
    public void DocumentReplaced()
    {
        activePageId = null;
        pageClipboard = null;
        items.Clear();
        Refresh();
    }

    /// <summary>Rebuilds the rows after a document or structure change and schedules thumbnails for changed pages.</summary>
    public void Refresh()
    {
        if (Document is not { } doc) return;
        var pages = doc.Session.Current.Pages;
        Syncing = true;
        try
        {
            if (!items.Select(t => t.PageId).SequenceEqual(pages.Select(p => p.Id)))
            {
                var selected = List.SelectedPageIds.ToHashSet();
                var existing = items.ToDictionary(t => t.PageId);
                items.Clear();
                foreach (var page in pages) items.Add(existing.GetValueOrDefault(page.Id) ?? new PageItemViewModel(page.Id));
                List.Select([.. items.Where(t => selected.Contains(t.PageId)).Select(t => t.PageId)]);
            }
            if (activePageId is { } id && doc.Session.Current.IndexOfPage(id) < 0) activePageId = null;
            int active = ActivePageIndex;
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Number = i + 1;
                items[i].IsActive = i == active;
            }
        }
        finally { Syncing = false; }
        CommandsChanged();
        ScheduleThumbnails();
    }

    /// <summary>Makes a page active (a click in the list), scrolling the view to it when asked.</summary>
    public void Activate(Guid pageId, bool scroll)
    {
        if (Document is null || Document.Session.Current.IndexOfPage(pageId) is not (>= 0 and var index)) return;
        activePageId = pageId;
        foreach (var t in items) t.IsActive = t.PageId == pageId;
        if (scroll && view.Viewport.CurrentPageIndex != index) view.Viewport.GoToPage(index);
    }

    /// <summary>Scrolling the view moves the active page (a click in the panel already scrolled to the same page).</summary>
    public void FollowView()
    {
        if (Document is not { } doc || Syncing || doc.Session.Current.Pages.Count == 0) return;
        var pages = doc.Session.Current.Pages;
        var page = pages[Math.Clamp(view.Viewport.CurrentPageIndex, 0, pages.Count - 1)];
        if (activePageId == page.Id) return;
        activePageId = page.Id;
        Syncing = true;
        try
        {
            foreach (var t in items) t.IsActive = t.PageId == page.Id;
            if (items.FirstOrDefault(t => t.PageId == page.Id) is { } item) List.FollowActive(item);
        }
        finally { Syncing = false; }
    }

    /// <summary>Selects these pages in the list and makes the first one active (after a command made or moved them).</summary>
    public void SelectPages(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        Syncing = true;
        try { List.Select(set); }
        finally { Syncing = false; }
        if (set.Count > 0 && Document is { } doc) Activate(doc.Session.Current.Pages.First(p => set.Contains(p.Id)).Id, scroll: true);
        CommandsChanged();
    }

    /// <summary>The list's selection changed (the paste and delete commands may change).</summary>
    public void SelectionChanged() => CommandsChanged();

    // ----- thumbnails -----

    /// <summary>Draws the thumbnails of changed pages 250 ms after the last change (also after text boxes were laid out).</summary>
    public void ScheduleThumbnails()
    {
        thumbTimer?.Dispose();
        thumbTimer = owner.After(TimeSpan.FromMilliseconds(250), () => { thumbTimer = null; _ = RenderThumbnailsAsync(); });
    }

    /// <summary>Moved to a monitor with another scale (D13e): every thumbnail is drawn again at its pixels.</summary>
    public Task InvalidateThumbnailsAsync()
    {
        foreach (var t in items) t.Rendered = null;
        return RenderThumbnailsAsync();
    }

    /// <summary>Draws the thumbnails of pages whose model changed, one after another; a newer call cancels this one.</summary>
    public async Task RenderThumbnailsAsync()
    {
        if (Document is not { } doc) return;
        thumbCancel?.Cancel();
        var cancel = thumbCancel = new CancellationTokenSource();
        var model = doc.Session.Current;
        bool hidden = owner.Display.NotesHidden;
        foreach (var page in model.Pages.ToArray())
        {
            if (cancel.IsCancellationRequested || !ReferenceEquals(Document, doc)) return;
            var item = items.FirstOrDefault(t => t.PageId == page.Id);
            if (item is null || ReferenceEquals(item.Rendered, page)) continue;
            // A thumbnail drawn while its text boxes were laid out waits until all of them are ready (the view calls
            // ScheduleThumbnails when a layout arrives), so each arriving layout does not draw it again.
            if (item.Image is not null && item.Rendered is null && !hidden && !TextReady(page)) continue;
            PageThumbnail? thumbnail;
            try { thumbnail = await List.Thumbnails.RenderAsync(doc, model, page, ThumbnailWidth, hidden, cancel.Token); }
            catch (OperationCanceledException) { return; }
            if (thumbnail is null || cancel.IsCancellationRequested || !ReferenceEquals(Document, doc)) return;
            item.Image = thumbnail.Image;
            item.ImageWidth = thumbnail.Width;
            item.ImageHeight = thumbnail.Height;
            item.Rendered = thumbnail.Complete ? page : null;
            await Task.Yield();
        }
    }

    private static bool TextReady(Page page)
    {
        bool ready = true;
        foreach (var box in page.Annotations.OfType<TextBoxAnnotation>())
            if (!PdfNotes.Text.TextLayout.TryMeasure(box, out _)) { ready = false; _ = PdfNotes.Text.TextLayout.MeasureAsync(box, urgent: false); }
        return ready;
    }

    // ----- command state -----

    /// <summary>This window copied pages, or some window's pages are on the clipboard.</summary>
    public bool HasPageClipboard => pageClipboard is not null || ClipboardHasPages;

    /// <summary>Pages can be pasted: this window's copy (if it fits the document) or another window's.</summary>
    public bool CanPaste => Document is not null && (pageClipboard?.CanPasteInto(Document.Session.Current) == true || ClipboardHasPages);

    /// <summary>At least one page would be left.</summary>
    public bool CanDelete => Document is not null && SelectedPageIds.Length < Document.Session.Current.Pages.Count;

    /// <summary>A selected page has notes (이 페이지의 필기 삭제).</summary>
    public bool SelectionHasNotes => Document is { } doc && SelectedPageIds.Any(id => doc.Session.Current.GetPage(id).Annotations.Count > 0);

    /// <summary>The paste and delete commands may be enabled differently now (also when the window is activated: another window may have copied pages).</summary>
    public void CommandsChanged()
    {
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(CanDelete));
    }

    /// <summary>The page panel's context menu (spec 11), for the selected pages.</summary>
    public IReadOnlyList<PageMenuEntry> Menu()
    {
        bool many = SelectedPageIds.Length > 1;
        return
        [
            new(PageCommand.InsertBlank, texts.Get("page.insertBlank")),
            PageMenuEntry.Separator,
            new(PageCommand.CopyWithNotes, texts.Get("page.copyWithNotes")),
            new(PageCommand.CopyPageOnly, texts.Get("page.copyPageOnly")),
            new(PageCommand.Paste, texts.Get("command.paste"), CanPaste),
            new(PageCommand.Delete, texts.Get("command.delete"), CanDelete),
            new(PageCommand.ClearNotes, many ? texts.Get("page.clearSelectedNotes") : texts.Get("page.clearNotes"), SelectionHasNotes),
            new(PageCommand.Export, texts.Get("page.exportMenu")),
            PageMenuEntry.Separator,
            new(PageCommand.RotateRight, texts.Get("page.rotateRight")),
            new(PageCommand.RotateLeft, texts.Get("page.rotateLeft")),
            PageMenuEntry.Separator,
            new(PageCommand.Shrink, texts.Get("page.shrinkContent")),
            new(PageCommand.Resize, texts.Get("page.resize")),
        ];
    }

    // ----- commands -----

    /// <summary>Runs a page command after finishing text input; refuses (with a message) when the rule forbids it.</summary>
    public bool Run(Func<DocumentModel, EditCommand> build)
    {
        if (Document is null) return false;
        view.FinishTextEditing();
        view.CancelTool();
        try { return Document.Session.Execute(build(Document.Session.Current)); }
        catch (InvalidOperationException) { dialogs.Tell(texts.Get("page.lastPage"), NoticeKind.Information); return false; }
    }

    /// <summary>Deletes the selected pages after asking (spec 11.2). The last page, or all pages at once, cannot be deleted.</summary>
    public bool DeleteSelected()
    {
        var ids = SelectedPageIds;
        if (Document is not { } doc || ids.Length == 0) return false;
        if (ids.Length >= doc.Session.Current.Pages.Count)
        { dialogs.Tell(texts.Get("page.lastPage"), NoticeKind.Information); return false; }
        if (dialogs.Ask(texts.Format("page.deleteConfirm", ("count", ids.Length)), AskButtons.YesNo, NoticeKind.Question) != Answer.Yes) return false;
        int first = doc.Session.Current.IndexOfPage(ids[0]);
        bool done = Run(m => DocumentEdits.DeletePages(m, ids));
        if (done) SelectPages([doc.Session.Current.Pages[Math.Min(first, doc.Session.Current.Pages.Count - 1)].Id]);
        return done;
    }

    public bool RotateSelected(bool clockwise) => Run(m => PageEdits.RotatePages(m, SelectedPageIds, clockwise));

    /// <summary>Moves these pages before the page now at <paramref name="insertBefore"/> (Count = end), keeping their order.</summary>
    public bool MoveTo(Guid[] ids, int insertBefore)
    {
        bool done = Run(m => PageEdits.MovePages(m, ids, insertBefore));
        if (done) SelectPages(ids);
        return done;
    }

    /// <summary>A blank page (spec 11.5): at the end (add) or after the active page (insert).</summary>
    public bool AddBlank(bool insertAfterActive, NewPageSpec spec)
    {
        if (Document is not { } doc) return false;
        int index = insertAfterActive ? ActivePageIndex + 1 : doc.Session.Current.Pages.Count;
        bool done = Run(m => DocumentEdits.InsertPage(m, index, spec.ToPageSetup(), insertAfterActive ? "undo.insertBlankPage" : "undo.addBlankPage"));
        if (done) SelectPages([doc.Session.Current.Pages[index].Id]);
        return done;
    }

    /// <summary>The page a new blank page is sized like by default: the active page (insert) or the last one (add).</summary>
    public Page? BlankReference(bool insertAfterActive) =>
        Document?.Session.Current is not { Pages.Count: > 0 } model ? null : insertAfterActive ? model.Pages[ActivePageIndex] : model.Pages[^1];

    public Guid[] Targets(PageApplyScope scope) => Document is not { } doc ? [] : scope switch
    {
        PageApplyScope.Current => [doc.Session.Current.Pages[ActivePageIndex].Id],
        PageApplyScope.Selected => SelectedPageIds,
        PageApplyScope.All => [.. doc.Session.Current.Pages.Select(p => p.Id)],
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    /// <summary>Resizes the paper or shrinks the content of the request's pages (spec 11.6–11.8), one Undo unit.</summary>
    public bool ApplyTransform(bool resize, PageTransformRequest request)
    {
        if (Document is not { } doc) return false;
        var ids = request.TargetPageIds ?? Targets(request.Scope);
        try
        {
            PdfNotes.Pdf.Writing.PagePlacementValidation.Validate(doc.Sources, ids.Select(id => doc.Session.Current.GetPage(id).Setup));
            return Run(m => resize ? PageTransforms.Resize(m, ids, request.WidthMm, request.HeightMm, request.Mode, request.Alignment, request.FreePosition)
                : PageTransforms.Shrink(m, ids, request.Percent, request.Alignment, request.FreePosition));
        }
        catch (ArgumentException) { dialogs.Tell(texts.Get("page.badTransform"), NoticeKind.Information); return false; }
        catch (NotSupportedException error) { dialogs.Tell(texts.Get(error.Message), NoticeKind.Information); return false; } // Pdf reports a language pack key
    }

    // ----- page clipboard (spec 11.4, D14g) -----

    /// <summary>Some window's pages are on the clipboard (a cheap check: the packet is read only when pasting).</summary>
    private bool ClipboardHasPages
    {
        get
        {
            try { return clipboard.ContainsData(PageTransfer.Format); }
            catch (ClipboardUnavailableException) { return false; }
        }
    }

    /// <summary>The page packet on the clipboard, or null. Its PDF is empty while the copying window still writes it.</summary>
    private (Guid Session, byte[] Pdf)? PagePacket()
    {
        try { return clipboard.ContainsData(PageTransfer.Format) ? PageTransfer.Decode(clipboard.GetData(PageTransfer.Format)) : null; }
        catch (ClipboardUnavailableException) { return null; }
    }

    /// <summary>The copying session of pages another window put on the clipboard, or null (none, or this document's own copy).</summary>
    private Guid? ForeignPages() =>
        Document is not null && PagePacket() is { } packet && packet.Session != Document.Session.SessionId ? packet.Session : null;

    /// <summary>The PDF of <paramref name="session"/>'s copy, waiting up to 10 s while that window still writes it; null if the clipboard changed or it never came.</summary>
    private async Task<byte[]?> ForeignPagesPdfAsync(Guid session)
    {
        for (int i = 0; i < 200; i++)
        {
            if (PagePacket() is not { } packet || packet.Session != session) return null;
            if (packet.Pdf.Length > 0) return packet.Pdf;
            await Task.Delay(50);
        }
        return null;
    }

    /// <summary>
    /// Copies the selected pages. Ctrl+C and 복사 always take the pages with their notes; 페이지만 복사 leaves this app's
    /// notes out for that one copy (spec 11.4, D14g: no remembered mode).
    /// </summary>
    public bool Copy(PageCopyMode mode = PageCopyMode.WithAnnotations)
    {
        if (Document is not { } doc) return false;
        view.FinishTextEditing();
        var ids = SelectedPageIds;
        pageClipboard = PageClipboard.Capture(doc.Session.Current, ids, mode);
        owner.Status = pageClipboard.Pages.Length == 1 ? texts.Get("page.copiedOne") : texts.Format("page.copied", ("count", pageClipboard.Pages.Length));
        CommandsChanged();
        // This copy is the newest at once: another window's older pages on the clipboard must not be pasted instead.
        try { clipboard.SetData(PageTransfer.Format, PageTransfer.Encode(doc.Session.SessionId, [])); }
        catch (ClipboardUnavailableException) { }
        _ = PutOnClipboardAsync(doc, ids, mode == PageCopyMode.WithAnnotations);
        return true;
    }

    /// <summary>The pages as they are now, as an editable PDF on the system clipboard for other windows (spec 11.4, D14g).</summary>
    private async Task PutOnClipboardAsync(OpenDocument doc, IReadOnlyList<Guid> pageIds, bool withNotes)
    {
        try
        {
            var pdf = await doc.PagesPdfAsync(doc.Session.Current, pageIds.ToArray(), withNotes);
            if (!ReferenceEquals(Document, doc)) return;
            clipboard.SetData(PageTransfer.Format, PageTransfer.Encode(doc.Session.SessionId, pdf));
            CommandsChanged();
        }
        catch (Exception e) when (e is ClipboardUnavailableException or InvalidDataException or IOException or InvalidOperationException) { } // this window still pastes them
    }

    /// <summary>Completes a paste of another window's pages (validation harness and tests).</summary>
    public Task<bool>? LastForeignPaste { get; private set; }

    /// <summary>
    /// Pastes copied pages after the active page, in their copied order (spec 11.4): another window's pages when they are
    /// on the clipboard (pasted in the background), else this window's own copy.
    /// </summary>
    public bool Paste()
    {
        if (ForeignPages() is { } session) { LastForeignPaste = PasteForeignAsync(session); return true; }
        if (Document is null || pageClipboard is not { } copied || !copied.CanPasteInto(Document.Session.Current)) return false;
        Guid[] ids = [];
        int at = ActivePageIndex + 1;
        bool done = Run(m => { var pasted = copied.Paste(m, at); ids = pasted.PageIds; return pasted.Command; });
        if (done) SelectPages(ids);
        return done;
    }

    /// <summary>
    /// Pastes another window's pages after the active page (spec 11.4, D14g), like importing pages from a PDF (11.9): their
    /// look at the moment they were copied, this app's notes with new IDs, one Undo unit.
    /// </summary>
    private async Task<bool> PasteForeignAsync(Guid session)
    {
        if (Document is not { } target) return false;
        string file = Path.Combine(Path.GetTempPath(), $"PdfNotes-pages-{Guid.NewGuid():N}.pdf");
        PdfSourceDocument? source = null;
        try
        {
            if (await ForeignPagesPdfAsync(session) is not { } pdf) throw new InvalidDataException("The copied pages did not arrive.");
            await File.WriteAllBytesAsync(file, pdf);
            source = await Task.Run(() => PdfSourceDocument.Open(file, allowPermissionRestricted: true));
            var opened = source;
            var import = await Task.Run(() => target.PrepareImport(opened, Enumerable.Range(0, opened.Pages.Count).ToArray()));
            if (!ReferenceEquals(Document, target)) { import.Pdf?.Dispose(); return false; }
            view.FinishTextEditing();
            view.CancelTool();
            var ids = target.InsertImported(import, Math.Clamp(ActivePageIndex + 1, 0, target.Session.Current.Pages.Count));
            SelectPages(ids);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PdfOpenException or InvalidDataException or InvalidOperationException)
        {
            dialogs.Tell(texts.Get("clipboard.pasteFailed"), NoticeKind.Warning);
            return false;
        }
        finally
        {
            source?.Dispose();
            try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
