using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdfNotes.App.Canvas;
using PdfNotes.App.Views;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.App.Themes;
using Page = PdfNotes.Core.Models.Page;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.App;

/// <summary>One row of the page panel: number, thumbnail and whether it is the active page (spec 11.1, 11.5).</summary>
public sealed class PageThumb(Guid pageId) : INotifyPropertyChanged
{
    private int number;
    private bool active;
    private ImageSource? image;
    private double width = PageThumbs.Width, height = PageThumbs.Width * 1.414;

    public Guid PageId { get; } = pageId;
    public int Number { get => number; set => Set(ref number, value); }
    public bool IsActive { get => active; set => Set(ref active, value); }
    public ImageSource? Image { get => image; set => Set(ref image, value); }
    public double ImageWidth { get => width; set => Set(ref width, value); }
    public double ImageHeight { get => height; set => Set(ref height, value); }

    /// <summary>The model page the image shows (reference); a changed page gets a new image.</summary>
    internal Page? Rendered { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}

public static class PageThumbs
{
    /// <summary>Thumbnail width in DIPs; height follows the displayed page.</summary>
    public const double Width = 132;
}

/// <summary>
/// Left page panel (spec 3.2-5, 11): visible by default, PowerPoint-like thumbnails in document order. Click goes to a
/// page and makes it active (insert target); Ctrl/Shift or the multi-select toggle select several. Drag reorders the
/// selected pages keeping their relative order. Context menu and Ctrl+C/V/Delete (when the panel has focus) add,
/// insert, copy (page + annotations or page only), paste after the active page, delete and rotate. Every structure
/// change is one Undo unit; page ids are kept so Undo restores the same pages.
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<PageThumb> pageThumbs = [];
    private PageClipboard? pageClipboard;
    private Guid? activePageId;
    private bool syncingPages;
    private DispatcherTimer? thumbTimer;
    private CancellationTokenSource? thumbCancel;
    private Point pageDragStart;
    private bool pageDragArmed, pageDragging;
    private PageThumb? pageDragPressed;
    private Guid[] pageDragIds = [];

    public IReadOnlyList<PageThumb> PageThumbnails => pageThumbs;
    public bool HasPageClipboard => pageClipboard is not null || ClipboardHasPages;

    /// <summary>Some window's pages are on the clipboard (a cheap check: the packet is read only when pasting).</summary>
    private bool ClipboardHasPages
    {
        get
        {
            try { return clipboard.ContainsData(PageTransfer.Format); }
            catch (ClipboardUnavailableException) { return false; }
        }
    }

    /// <summary>The page packet on the clipboard (spec 11.4, D14g), or null. Its PDF is empty while the copying window still writes it.</summary>
    private (Guid Session, byte[] Pdf)? PagePacket()
    {
        try { return clipboard.ContainsData(PageTransfer.Format) ? PageTransfer.Decode(clipboard.GetData(PageTransfer.Format)) : null; }
        catch (ClipboardUnavailableException) { return null; }
    }

    /// <summary>The copying session of pages another window put on the clipboard, or null (none, or this document's own copy).</summary>
    private Guid? ForeignPages() =>
        document is not null && PagePacket() is { } packet && packet.Session != document.Session.SessionId ? packet.Session : null;

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

    /// <summary>Pages can be pasted: this window's copy (if it fits the document) or another window's.</summary>
    private bool CanPastePages => document is not null && (pageClipboard?.CanPasteInto(document.Session.Current) == true || ClipboardHasPages);

    /// <summary>The active page: insert target and paste position (spec 11.5). Follows the view when the panel is idle.</summary>
    public int ActivePageIndex => document is { } d && activePageId is { } id && d.Session.Current.IndexOfPage(id) is >= 0 and var i ? i : View.CurrentPageIndex;

    /// <summary>Pages selected in the panel (at least the active page), in document order.</summary>
    public Guid[] SelectedPageIds
    {
        get
        {
            if (document is null) return [];
            var selected = PageList.SelectedItems.OfType<PageThumb>().Select(t => t.PageId).ToHashSet();
            if (selected.Count == 0) selected.Add(document.Session.Current.Pages[ActivePageIndex].Id);
            return [.. document.Session.Current.Pages.Where(p => selected.Contains(p.Id)).Select(p => p.Id)];
        }
    }

    private void InitializePagePanel()
    {
        var add = new Button { Content = Strings.Get("page.addBlank"), Margin = new Thickness(6, 12, 6, 16), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Stretch };
        add.Click += (_, _) => AddBlankPage(false);
        PageList.ItemsSource = new CompositeCollection { new CollectionContainer { Collection = pageThumbs }, add };
        PageList.SelectionChanged += (_, _) =>
        {
            if (syncingPages) return;
            if (PageList.SelectedItems.Count == 1 && PageList.SelectedItem is PageThumb thumb) Activate(thumb.PageId, scroll: true);
        };
        PageList.PreviewMouseLeftButtonDown += PageListMouseDown;
        PageList.PreviewMouseMove += PageListMouseMove;
        PageList.PreviewMouseLeftButtonUp += PageListMouseUp;
        // The list's own handlers may release the capture before our mouse-up runs: a release with the button up is the drop.
        PageList.LostMouseCapture += (_, _) => FinishPageDrag(drop: Mouse.LeftButton == MouseButtonState.Released);
        // A placeholder menu: without one the first right-click opened nothing (found with real input, 2026-10-14).
        PageList.ContextMenu = new ContextMenu();
        PageList.ContextMenuOpening += (_, e) => { PageList.ContextMenu = PageMenu(); AutoRibbonTab(RibbonTab.Page); };
        // Page work shows the 페이지 tab (spec 3.3, D14g): a multi-selection (Ctrl/Shift, the multi-select mode) or a drag
        // below; a plain click only goes to the page and keeps the tab.
        PageList.PreviewMouseDown += (_, e) =>
        {
            if (ItemAt(e.GetPosition(PageList)) is not null && ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 || MultiSelectButton.IsChecked == true))
                AutoRibbonTab(RibbonTab.Page);
        };
        PageList.PreviewKeyDown += PageListKeyDown;
        View.ViewChanged += (_, _) => FollowViewPage();
        ShowPagePanel(settings.PagePanelVisible);
    }

    private void PagePanelToggled(object sender, RoutedEventArgs e) => SetPagePanelVisible(PagePanelButton.IsChecked == true);

    /// <summary>The handle on the panel's edge (2026-10-14 user request): hides the list, or shows it again from the window's left edge.</summary>
    private void PagePanelHandleClick(object sender, RoutedEventArgs e) => SetPagePanelVisible(PagePanel.Visibility != Visibility.Visible);

    public bool PagePanelVisible => PagePanel.Visibility == Visibility.Visible;

    /// <summary>Shows or hides the page list (handle and page tab button agree) and remembers it (spec 3.2-5).</summary>
    public void SetPagePanelVisible(bool visible)
    {
        ShowPagePanel(visible);
        settings.PagePanelVisible = visible;
        settings.Save();
    }

    private void ShowPagePanel(bool visible)
    {
        PagePanelButton.IsChecked = visible;
        PagePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PagePanelHandleIcon.Text = visible ? "\uE76B" : "\uE76C"; // ‹ hides, › shows
        PagePanelHandle.ToolTip = visible ? Strings.Get("page.hideList") : Strings.Get("page.list");
        PagePanelHandle.Tag = visible ? null : "Closed"; // accent color while the list is hidden
    }

    private void MultiSelectToggled(object sender, RoutedEventArgs e)
    {
        PageList.SelectionMode = MultiSelectButton.IsChecked == true ? SelectionMode.Multiple : SelectionMode.Extended;
        AutoRibbonTab(RibbonTab.Page);
    }

    /// <summary>Rebuilds the rows after a document or structure change and schedules thumbnails for changed pages.</summary>
    private void RefreshPagePanel()
    {
        if (document is null) return;
        var pages = document.Session.Current.Pages;
        syncingPages = true;
        try
        {
            var selected = PageList.SelectedItems.OfType<PageThumb>().Select(t => t.PageId).ToHashSet();
            var existing = pageThumbs.ToDictionary(t => t.PageId);
            if (!pageThumbs.Select(t => t.PageId).SequenceEqual(pages.Select(p => p.Id)))
            {
                pageThumbs.Clear();
                foreach (var page in pages) pageThumbs.Add(existing.GetValueOrDefault(page.Id) ?? new PageThumb(page.Id));
                foreach (var thumb in pageThumbs.Where(t => selected.Contains(t.PageId))) PageList.SelectedItems.Add(thumb);
            }
            if (activePageId is { } id && document.Session.Current.IndexOfPage(id) < 0) activePageId = null;
            for (int i = 0; i < pageThumbs.Count; i++)
            {
                pageThumbs[i].Number = i + 1;
                pageThumbs[i].IsActive = i == ActivePageIndex;
            }
        }
        finally { syncingPages = false; }
        UpdatePageCommands();
        thumbTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { thumbTimer!.Stop(); RenderThumbnails(); }, Dispatcher);
        thumbTimer.Stop(); thumbTimer.Start();
    }

    /// <summary>Thumbnails of pages whose model changed: PDF background in the background, annotations drawn on top.</summary>
    /// <summary>Moved to a monitor with another scale: thumbnails are rendered again at its pixels (D13e; the canvas does its own pages).</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        foreach (var thumb in pageThumbs) thumb.Rendered = null;
        RenderThumbnails();
    }

    private async void RenderThumbnails()
    {
        if (document is not { } doc) return;
        thumbCancel?.Cancel();
        var cancel = thumbCancel = new CancellationTokenSource();
        var model = doc.Session.Current;
        foreach (var page in model.Pages.ToArray())
        {
            if (cancel.IsCancelled() || !ReferenceEquals(document, doc)) return;
            var thumb = pageThumbs.FirstOrDefault(t => t.PageId == page.Id);
            if (thumb is null || ReferenceEquals(thumb.Rendered, page)) continue;
            var frame = page.Setup.Frame;
            double zoom = PageThumbs.Width / (frame.DisplayWidthPt * Units.DipsPerPoint);
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double ppp = zoom * Units.DipsPerPoint * dpi;
            BitmapSource? background = null;
            try
            {
                var rendered = await Task.Run(() => doc.Renderer.Render(page.Setup, ppp, null, cancel.Token), cancel.Token);
                background = BitmapSource.Create(rendered.Width, rendered.Height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32, null, rendered.Bgra, rendered.Stride);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) when (!cancel.IsCancelled()) { } // an unrenderable page keeps a white thumbnail
            if (cancel.IsCancelled() || !ReferenceEquals(document, doc)) return;
            var transform = new ViewTransform(frame, zoom, dpi, new(0, 0));
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                var area = new Rect(0, 0, transform.PageWidthDip, transform.PageHeightDip);
                context.DrawRectangle(Theme.Brush("Paper"), null, area);
                if (background is not null) context.DrawImage(background, area);
                context.PushClip(new RectangleGeometry(area));
                foreach (var a in NotesHidden ? [] : page.Annotations)
                {
                    if (a is ImageAnnotation image && model.Resources.TryGetValue(image.ResourceId, out var resource)) AnnotationPainter.PaintImage(context, image, resource, transform);
                    else AnnotationPainter.Paint(context, a, transform);
                }
                context.Pop();
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(transform.PageWidthDip * dpi), (int)Math.Ceiling(transform.PageHeightDip * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            thumb.Image = bitmap;
            thumb.ImageWidth = transform.PageWidthDip;
            thumb.ImageHeight = transform.PageHeightDip;
            thumb.Rendered = page;
            await Task.Yield();
        }
    }

    private void Activate(Guid pageId, bool scroll)
    {
        if (document is null || document.Session.Current.IndexOfPage(pageId) is not (>= 0 and var index)) return;
        activePageId = pageId;
        foreach (var t in pageThumbs) t.IsActive = t.PageId == pageId;
        if (scroll && View.CurrentPageIndex != index) View.GoToPage(index);
    }

    /// <summary>Scrolling the view moves the active page (a click in the panel already scrolled to the same page).</summary>
    private void FollowViewPage()
    {
        if (document is null || syncingPages) return;
        var page = document.Session.Current.Pages[Math.Clamp(View.CurrentPageIndex, 0, document.Session.Current.Pages.Count - 1)];
        if (activePageId == page.Id) return;
        activePageId = page.Id;
        syncingPages = true;
        try
        {
            foreach (var t in pageThumbs) t.IsActive = t.PageId == page.Id;
            if (pageThumbs.FirstOrDefault(t => t.PageId == page.Id) is { } thumb && PageList.SelectedItems.Count <= 1)
            {
                PageList.SelectedItem = thumb;
                PageList.ScrollIntoView(thumb);
            }
        }
        finally { syncingPages = false; }
    }

    private void SelectPages(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        syncingPages = true;
        try
        {
            PageList.SelectedItems.Clear();
            foreach (var t in pageThumbs.Where(t => set.Contains(t.PageId))) PageList.SelectedItems.Add(t);
        }
        finally { syncingPages = false; }
        if (set.Count > 0) Activate(document!.Session.Current.Pages.First(p => set.Contains(p.Id)).Id, scroll: true);
    }

    // ----- commands (also used by the validation harness) -----

    /// <summary>Runs a page command after finishing text input; refuses (with a message) when the rule forbids it.</summary>
    private bool RunPageCommand(Func<DocumentModel, EditCommand> build)
    {
        if (document is null) return false;
        FinishTextEditing();
        View.CancelTool();
        try { return document.Session.Execute(build(document.Session.Current)); }
        catch (InvalidOperationException) { ShowMessage(Strings.Get("page.lastPage"), MessageBoxImage.Information); return false; }
    }

    /// <summary>Deletes the selected pages (spec 11.2). The last page, or all pages at once, cannot be deleted.</summary>
    public bool DeleteSelectedPages()
    {
        var ids = SelectedPageIds;
        if (document is null || ids.Length == 0) return false;
        if (ids.Length >= document.Session.Current.Pages.Count)
        { ShowMessage(Strings.Get("page.lastPage"), MessageBoxImage.Information); return false; }
        if (Prompt(Strings.Format("page.deleteConfirm", ("count", ids.Length)), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        int first = document.Session.Current.IndexOfPage(ids[0]);
        bool done = RunPageCommand(m => DocumentEdits.DeletePages(m, ids));
        if (done) SelectPages([document.Session.Current.Pages[Math.Min(first, document.Session.Current.Pages.Count - 1)].Id]);
        return done;
    }

    public bool RotateSelectedPages(bool clockwise) => RunPageCommand(m => PageEdits.RotatePages(m, SelectedPageIds, clockwise));

    /// <summary>Moves the selected pages before the page now at <paramref name="insertBefore"/> (Count = end), keeping their order.</summary>
    public bool MoveSelectedPages(int insertBefore) => MovePagesTo(SelectedPageIds, insertBefore);

    private bool MovePagesTo(Guid[] ids, int insertBefore)
    {
        bool done = RunPageCommand(m => PageEdits.MovePages(m, ids, insertBefore));
        if (done) SelectPages(ids);
        return done;
    }

    /// <summary>
    /// Copies the selected pages. Ctrl+C and 복사 always take the pages with their notes; 페이지만 복사 leaves this app's
    /// notes out for that one copy (spec 11.4, D14g: no remembered mode).
    /// </summary>
    public bool CopySelectedPages(PageCopyMode mode = PageCopyMode.WithAnnotations)
    {
        if (document is null) return false;
        FinishTextEditing();
        pageClipboard = PageClipboard.Capture(document.Session.Current, SelectedPageIds, mode);
        StatusText.Text = pageClipboard.Pages.Length == 1 ? Strings.Get("page.copiedOne") : Strings.Format("page.copied", ("count", pageClipboard.Pages.Length));
        UpdatePageCommands();
        // This copy is the newest at once: another window's older pages on the clipboard must not be pasted instead.
        try { clipboard.SetData(PageTransfer.Format, PageTransfer.Encode(document.Session.SessionId, [])); }
        catch (ClipboardUnavailableException) { }
        _ = PutPagesOnClipboardAsync(document, SelectedPageIds, mode == PageCopyMode.WithAnnotations);
        return true;
    }

    /// <summary>The pages as they are now, as an editable PDF on the system clipboard for other windows (spec 11.4, D14g).</summary>
    private async Task PutPagesOnClipboardAsync(OpenDocument doc, IReadOnlyList<Guid> pageIds, bool withNotes)
    {
        try
        {
            var pdf = await doc.PagesPdfAsync(doc.Session.Current, pageIds.ToArray(), withNotes);
            if (!ReferenceEquals(document, doc)) return;
            clipboard.SetData(PageTransfer.Format, PageTransfer.Encode(doc.Session.SessionId, pdf));
            UpdatePageCommands();
        }
        catch (Exception e) when (e is ClipboardUnavailableException or InvalidDataException or IOException or InvalidOperationException) { } // this window still pastes them
    }

    /// <summary>Validation harness: completes a paste of another window's pages.</summary>
    public Task<bool>? LastForeignPagePaste { get; private set; }

    /// <summary>
    /// Pastes another window's pages after the active page (spec 11.4, D14g), like importing pages from a PDF (11.9): their
    /// look at the moment they were copied, this app's notes with new IDs, one Undo unit.
    /// </summary>
    private async Task<bool> PasteForeignPagesAsync(Guid session)
    {
        if (document is not { } target) return false;
        string file = Path.Combine(Path.GetTempPath(), $"PdfNotes-pages-{Guid.NewGuid():N}.pdf");
        PdfSourceDocument? source = null;
        try
        {
            if (await ForeignPagesPdfAsync(session) is not { } pdf) throw new InvalidDataException("The copied pages did not arrive.");
            await File.WriteAllBytesAsync(file, pdf);
            source = await Task.Run(() => PdfSourceDocument.Open(file, allowPermissionRestricted: true));
            var opened = source;
            var import = await Task.Run(() => target.PrepareImport(opened, Enumerable.Range(0, opened.Pages.Count).ToArray()));
            if (!ReferenceEquals(document, target)) { import.Pdf?.Dispose(); return false; }
            FinishTextEditing();
            View.CancelTool();
            var ids = target.InsertImported(import, Math.Clamp(ActivePageIndex + 1, 0, target.Session.Current.Pages.Count));
            SelectPages(ids);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PdfOpenException or InvalidDataException or InvalidOperationException)
        {
            ShowMessage(Strings.Get("clipboard.pasteFailed"), MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            source?.Dispose();
            try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Pastes copied pages after the active page, in their copied order (spec 11.4): another window's pages when they are
    /// on the clipboard (pasted in the background), else this window's own copy.
    /// </summary>
    public bool PastePages()
    {
        if (ForeignPages() is { } session) { LastForeignPagePaste = PasteForeignPagesAsync(session); return true; }
        if (document is null || pageClipboard is not { } clipboard || !clipboard.CanPasteInto(document.Session.Current)) return false;
        Guid[] ids = [];
        int at = ActivePageIndex + 1;
        bool done = RunPageCommand(m => { var pasted = clipboard.Paste(m, at); ids = pasted.PageIds; return pasted.Command; });
        if (done) SelectPages(ids);
        return done;
    }

    /// <summary>Blank page (spec 11.5): at the end (add) or after the active page (insert), sized like the reference page by default.</summary>
    public bool AddBlankPage(bool insertAfterActive, NewPageSpec? spec = null)
    {
        if (document is null) return false;
        var model = document.Session.Current;
        var reference = insertAfterActive ? model.Pages[ActivePageIndex] : model.Pages[^1];
        if (spec is null)
        {
            var (w, h) = PageEdits.DisplayedSize(reference.Setup);
            var dialog = new NewNoteDialog(insertAfterActive ? Strings.Get("page.insertBlank") : Strings.Get("page.addBlank"), (Units.PointsToMillimeters(w), Units.PointsToMillimeters(h)),
                reference.Setup.Background?.Kind ?? PageBackgroundKind.Blank) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is null) return false;
            spec = dialog.Result;
        }
        int index = insertAfterActive ? ActivePageIndex + 1 : model.Pages.Count;
        bool done = RunPageCommand(m => DocumentEdits.InsertPage(m, index, spec.ToPageSetup(), insertAfterActive ? "undo.insertBlankPage" : "undo.addBlankPage"));
        if (done) SelectPages([document.Session.Current.Pages[index].Id]);
        return done;
    }

    private ContextMenu PageMenu()
    {
        var menu = new ContextMenu();
        void Add(string text, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        bool canDelete = document is not null && SelectedPageIds.Length < document.Session.Current.Pages.Count;
        Add(Strings.Get("page.insertBlank"), () => AddBlankPage(true));
        menu.Items.Add(new Separator());
        Add(Strings.Get("page.copyWithNotes"), () => CopySelectedPages(PageCopyMode.WithAnnotations));
        Add(Strings.Get("page.copyPageOnly"), () => CopySelectedPages(PageCopyMode.PageOnly));
        Add(Strings.Get("command.paste"), () => PastePages(), CanPastePages);
        Add(Strings.Get("command.delete"), () => DeleteSelectedPages(), canDelete);
        bool hasNotes = document is not null && SelectedPageIds.Any(id => document.Session.Current.GetPage(id).Annotations.Count > 0);
        Add(SelectedPageIds.Length > 1 ? Strings.Get("page.clearSelectedNotes") : Strings.Get("page.clearNotes"), () => ClearSelectedPageNotes(), hasNotes);
        Add(Strings.Get("page.exportMenu"), async () => await ExportPagesAsync());
        menu.Items.Add(new Separator());
        Add(Strings.Get("page.rotateRight"), () => RotateSelectedPages(true));
        Add(Strings.Get("page.rotateLeft"), () => RotateSelectedPages(false));
        menu.Items.Add(new Separator());
        Add(Strings.Get("page.shrinkContent"), () => ShowPageTransform(false));
        Add(Strings.Get("page.resize"), () => ShowPageTransform(true));
        return menu;
    }

    public Guid[] PageTargets(PageApplyScope scope) => document is null ? [] : scope switch
    {
        PageApplyScope.Current => [document.Session.Current.Pages[ActivePageIndex].Id],
        PageApplyScope.Selected => SelectedPageIds,
        PageApplyScope.All => [.. document.Session.Current.Pages.Select(p => p.Id)],
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    public bool ApplyPageTransform(bool resize, PageTransformRequest request)
    {
        if (document is null) return false;
        var ids = request.TargetPageIds ?? PageTargets(request.Scope);
        try
        {
            PdfNotes.Pdf.Writing.PagePlacementValidation.Validate(document.Sources, ids.Select(id => document.Session.Current.GetPage(id).Setup));
            return RunPageCommand(m => resize ? PageTransforms.Resize(m, ids, request.WidthMm, request.HeightMm, request.Mode, request.Alignment, request.FreePosition)
                : PageTransforms.Shrink(m, ids, request.Percent, request.Alignment, request.FreePosition));
        }
        catch (ArgumentException) { ShowMessage(Strings.Get("page.badTransform"), MessageBoxImage.Information); return false; }
        catch (NotSupportedException error) { ShowMessage(Strings.Get(error.Message), MessageBoxImage.Information); return false; } // Pdf reports a language pack key
    }

    public void ShowPageTransform(bool resize)
    {
        if (document is null || busy) return;
        var model = document.Session.Current;
        var active = model.Pages[ActivePageIndex];
        var targets = Enum.GetValues<PageApplyScope>().ToDictionary(scope => scope, PageTargets);
        var dialog = new PageTransformDialog(resize, active.Setup,
            scope => targets[scope].Select(id => model.GetPage(id).Setup).ToArray(),
            PlacementImage(active, model)) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } request)
            ApplyPageTransform(resize, request with { TargetPageIds = targets[request.Scope] });
    }

    private ImageSource PlacementImage(Page page, DocumentModel model)
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; // the dialog opens over this window (same monitor)
        double zoom = 700 / (Math.Max(page.Setup.Frame.DisplayWidthPt, page.Setup.Frame.DisplayHeightPt) * Units.DipsPerPoint);
        var transform = new ViewTransform(page.Setup.Frame, zoom, dpi, new(0, 0));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var area = new Rect(0, 0, transform.PageWidthDip, transform.PageHeightDip);
            dc.DrawRectangle(Theme.Brush("Paper"), null, area);
            var rendered = document!.Renderer.Render(page.Setup, zoom * Units.DipsPerPoint * dpi, null, CancellationToken.None);
            var background = BitmapSource.Create(rendered.Width, rendered.Height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32, null, rendered.Bgra, rendered.Stride);
            dc.DrawImage(background, area); dc.PushClip(new RectangleGeometry(area));
            foreach (var a in page.Annotations)
                if (a is ImageAnnotation image && model.Resources.TryGetValue(image.ResourceId, out var resource)) AnnotationPainter.PaintImage(dc, image, resource, transform);
                else AnnotationPainter.Paint(dc, a, transform);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(transform.PageWidthDip * dpi), (int)Math.Ceiling(transform.PageHeightDip * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    private void ShrinkPageClick(object sender, RoutedEventArgs e) => ShowPageTransform(false);
    private void ResizePageClick(object sender, RoutedEventArgs e) => ShowPageTransform(true);

    private void PageListKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { DeleteSelectedPages(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { CopySelectedPages(); e.Handled = true; }
        else if (ctrl && e.Key == Key.V) { PastePages(); e.Handled = true; }
        else if (ctrl && e.Key == Key.A) { PageList.SelectAll(); e.Handled = true; }
    }

    // ----- drag to reorder (spec 11.1–11.2): mouse capture, a drop line between rows -----

    private void PageListMouseDown(object sender, MouseButtonEventArgs e)
    {
        pageDragStart = e.GetPosition(PageList);
        var thumb = ItemAt(pageDragStart);
        pageDragArmed = thumb is not null && PageList.SelectedItems.Contains(thumb);
        pageDragPressed = null;
        PageList.Focus();
        // Pressing a page of a multi-selection keeps the selection so all of it can be dragged; a click without a
        // drag selects that page alone on release, as usual.
        if (pageDragArmed && Keyboard.Modifiers == ModifierKeys.None && PageList.SelectedItems.Count > 1 && PageList.SelectionMode == SelectionMode.Extended)
        {
            pageDragPressed = thumb;
            e.Handled = true;
        }
    }

    private void PageListMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(PageList);
        if (pageDragging)
        {
            ShowDropLine(DropIndex(p));
            e.Handled = true;
            return;
        }
        if (!pageDragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        if (Math.Abs(p.X - pageDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - pageDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        pageDragArmed = false;
        // The list changes its selection while the mouse passes over rows: remember what is being dragged.
        pageDragIds = SelectedPageIds;
        pageDragging = PageList.CaptureMouse();
        if (pageDragging) { ShowDropLine(DropIndex(p)); AutoRibbonTab(RibbonTab.Page); }
    }

    private void PageListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (pageDragging)
        {
            FinishPageDrag(drop: true);
            e.Handled = true;
        }
        else if (pageDragPressed is { } thumb)
        {
            PageList.SelectedItem = thumb; // a plain click on a page of the selection
            e.Handled = true;
        }
        pageDragArmed = false;
        pageDragPressed = null;
    }

    /// <summary>Ends a page drag once: with <paramref name="drop"/> the selection moves to the drop line, otherwise nothing changes.</summary>
    private void FinishPageDrag(bool drop)
    {
        if (!pageDragging) return;
        pageDragging = false;
        int index = DropIndex(Mouse.GetPosition(PageList));
        PageDropLine.Visibility = Visibility.Collapsed;
        if (PageList.IsMouseCaptured) PageList.ReleaseMouseCapture();
        if (drop) MovePagesTo(pageDragIds, index);
    }

    private void ShowDropLine(int index)
    {
        double y;
        if (index < pageThumbs.Count && PageList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row && row.IsVisible)
            y = row.TranslatePoint(new Point(0, 0), PageList).Y;
        else if (PageList.ItemContainerGenerator.ContainerFromIndex(pageThumbs.Count - 1) is FrameworkElement last && last.IsVisible)
            y = last.TranslatePoint(new Point(0, last.ActualHeight), PageList).Y;
        else { PageDropLine.Visibility = Visibility.Collapsed; return; }
        System.Windows.Controls.Canvas.SetTop(PageDropLine, Math.Max(0, y - 1.5));
        PageDropLine.Width = PageList.ActualWidth - 16;
        PageDropLine.Visibility = Visibility.Visible;
    }

    /// <summary>Insertion index for a drop: before the row under the point, or after it when below its middle.</summary>
    public int DropIndex(Point listPoint)
    {
        for (int i = 0; i < pageThumbs.Count; i++)
        {
            if (PageList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row || !row.IsVisible) continue;
            var top = row.TranslatePoint(new Point(0, 0), PageList).Y;
            if (listPoint.Y < top + row.ActualHeight / 2) return i;
        }
        return pageThumbs.Count;
    }

    private PageThumb? ItemAt(Point listPoint) =>
        PageList.InputHitTest(listPoint) is DependencyObject hit && ItemsControl.ContainerFromElement(PageList, hit) is ListBoxItem item ? item.DataContext as PageThumb : null;
}

internal static class CancellationExtensions
{
    public static bool IsCancelled(this CancellationTokenSource source) => source.IsCancellationRequested;
}
