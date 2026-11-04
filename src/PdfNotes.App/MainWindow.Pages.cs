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

/// <summary>
/// Left page panel (spec 3.2-5, 11): visible by default, PowerPoint-like thumbnails in document order. Click goes to a
/// page and makes it active (insert target); Ctrl/Shift or the multi-select toggle select several. Drag reorders the
/// selected pages keeping their relative order. Context menu and Ctrl+C/V/Delete (when the panel has focus) add,
/// insert, copy (page + annotations or page only), paste after the active page, delete and rotate. Every structure
/// change is one Undo unit; page ids are kept so Undo restores the same pages.
/// </summary>
public partial class MainWindow : IPageListView, IPageThumbnailSource
{
    private Point pageDragStart;
    private bool pageDragArmed, pageDragging;
    private PageItemViewModel? pageDragPressed;
    private Guid[] pageDragIds = [];

    private PagesViewModel pages => viewModel.Pages;
    private IReadOnlyList<PageItemViewModel> pageThumbs => pages.Items;

    public IReadOnlyList<PageItemViewModel> PageThumbnails => pages.Items;
    public bool HasPageClipboard => pages.HasPageClipboard;

    /// <summary>The active page: insert target and paste position (spec 11.5). Follows the view when the panel is idle.</summary>
    public int ActivePageIndex => pages.ActivePageIndex;

    /// <summary>Pages selected in the panel (at least the active page), in document order.</summary>
    public Guid[] SelectedPageIds => pages.SelectedPageIds;

    // ----- the page list for the view model (P1 V5) -----

    IPageListView IMainView.Pages => this;

    IReadOnlyCollection<Guid> IPageListView.SelectedPageIds => [.. PageList.SelectedItems.OfType<PageItemViewModel>().Select(t => t.PageId)];

    void IPageListView.Select(IReadOnlyCollection<Guid> ids)
    {
        PageList.SelectedItems.Clear();
        foreach (var t in pageThumbs.Where(t => ids.Contains(t.PageId))) PageList.SelectedItems.Add(t);
    }

    void IPageListView.FollowActive(PageItemViewModel item)
    {
        if (PageList.SelectedItems.Count > 1) return;
        PageList.SelectedItem = item;
        PageList.ScrollIntoView(item);
    }

    IPageThumbnailSource IPageListView.Thumbnails => this;

    private void RefreshPagePanel() => pages.Refresh();
    private void SelectPages(IEnumerable<Guid> ids) => pages.SelectPages(ids);

    private void InitializePagePanel()
    {
        var add = new Button { Content = Strings.Get("page.addBlank"), Margin = new Thickness(6, 12, 6, 16), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Stretch };
        add.Click += (_, _) => AddBlankPage(false);
        PageList.ItemsSource = new CompositeCollection { new CollectionContainer { Collection = pages.Items }, add };
        PageList.SelectionChanged += (_, _) =>
        {
            if (pages.Syncing) return;
            if (PageList.SelectedItems.Count == 1 && PageList.SelectedItem is PageItemViewModel thumb) pages.Activate(thumb.PageId, scroll: true);
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
        View.ViewChanged += (_, _) => pages.FollowView();
        // Text layouts built in the background (AnnotationPainter.DeferTextLayout): the page and thumbnails catch up.
        Action laidOut = () =>
        {
            View.RefreshOverlays();
            PositionTextEditor();
            pages.ScheduleThumbnails();
        };
        AnnotationPainter.TextLaidOut += laidOut;
        Closed += (_, _) => AnnotationPainter.TextLaidOut -= laidOut;
        pages.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PagesViewModel.PanelVisible)) ShowPagePanel(pages.PanelVisible); };
        ShowPagePanel(pages.PanelVisible);
    }

    private void PagePanelToggled(object sender, RoutedEventArgs e) => SetPagePanelVisible(PagePanelButton.IsChecked == true);

    /// <summary>The handle on the panel's edge (2026-10-14 user request): hides the list, or shows it again from the window's left edge.</summary>
    private void PagePanelHandleClick(object sender, RoutedEventArgs e) => SetPagePanelVisible(PagePanel.Visibility != Visibility.Visible);

    public bool PagePanelVisible => PagePanel.Visibility == Visibility.Visible;

    /// <summary>Shows or hides the page list (handle and page tab button agree) and remembers it (spec 3.2-5).</summary>
    public void SetPagePanelVisible(bool visible)
    {
        pages.PanelVisible = visible;
        ShowPagePanel(visible); // also when unchanged: the tab button may have been toggled to the same value
    }

    private void ShowPagePanel(bool visible)
    {
        PagePanelButton.IsChecked = visible;
        PagePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PagePanelHandleIcon.Text = visible ? "" : ""; // ‹ hides, › shows
        PagePanelHandle.ToolTip = visible ? Strings.Get("page.hideList") : Strings.Get("page.list");
        PagePanelHandle.Tag = visible ? null : "Closed"; // accent color while the list is hidden
    }

    private void MultiSelectToggled(object sender, RoutedEventArgs e)
    {
        PageList.SelectionMode = MultiSelectButton.IsChecked == true ? SelectionMode.Multiple : SelectionMode.Extended;
        AutoRibbonTab(RibbonTab.Page);
    }

    /// <summary>Moved to a monitor with another scale: thumbnails are rendered again at its pixels (D13e; the canvas does its own pages).</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _ = pages.InvalidateThumbnailsAsync();
    }

    /// <summary>A thumbnail: the PDF background rendered in the background, the notes drawn on top (<see cref="IPageThumbnailSource"/>).</summary>
    async Task<PageThumbnail?> IPageThumbnailSource.RenderAsync(OpenDocument doc, DocumentModel model, Page page, double width, bool notesHidden, CancellationToken cancel)
    {
        var frame = page.Setup.Frame;
        double zoom = width / (frame.DisplayWidthPt * Units.DipsPerPoint);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double ppp = zoom * Units.DipsPerPoint * dpi;
        BitmapSource? background = null;
        try
        {
            var rendered = await Task.Run(() => doc.Renderer.Render(page.Setup, ppp, null, cancel), cancel);
            background = BitmapSource.Create(rendered.Width, rendered.Height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32, null, rendered.Bgra, rendered.Stride);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception) when (!cancel.IsCancellationRequested) { } // an unrenderable page keeps a white thumbnail
        if (cancel.IsCancellationRequested) return null;
        var transform = new ViewTransform(frame, zoom, dpi, new(0, 0));
        var visual = new DrawingVisual();
        bool complete = true; // text boxes still being laid out: drawn again after TextLaidOut
        using (var context = visual.RenderOpen())
        {
            var area = new Rect(0, 0, transform.PageWidthDip, transform.PageHeightDip);
            context.DrawRectangle(Theme.Brush("Paper"), null, area);
            if (background is not null) context.DrawImage(background, area);
            context.PushClip(new RectangleGeometry(area));
            foreach (var a in notesHidden ? [] : page.Annotations)
            {
                if (a is ImageAnnotation image && model.Resources.TryGetValue(image.ResourceId, out var resource)) AnnotationPainter.PaintImage(context, image, resource, transform);
                else complete &= AnnotationPainter.Paint(context, a, transform, background: true);
            }
            context.Pop();
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(transform.PageWidthDip * dpi), (int)Math.Ceiling(transform.PageHeightDip * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return new PageThumbnail(bitmap, transform.PageWidthDip, transform.PageHeightDip, complete);
    }

    // ----- commands (also used by the validation harness; the view model runs them) -----

    /// <summary>Runs a page command after finishing text input; refuses (with a message) when the rule forbids it.</summary>
    private bool RunPageCommand(Func<DocumentModel, EditCommand> build) => pages.Run(build);

    /// <summary>Deletes the selected pages (spec 11.2). The last page, or all pages at once, cannot be deleted.</summary>
    public bool DeleteSelectedPages() => pages.DeleteSelected();

    public bool RotateSelectedPages(bool clockwise) => pages.RotateSelected(clockwise);

    /// <summary>Moves the selected pages before the page now at <paramref name="insertBefore"/> (Count = end), keeping their order.</summary>
    public bool MoveSelectedPages(int insertBefore) => pages.MoveTo(SelectedPageIds, insertBefore);

    /// <summary>Copies the selected pages (spec 11.4): with their notes, or this once without them.</summary>
    public bool CopySelectedPages(PageCopyMode mode = PageCopyMode.WithAnnotations) => pages.Copy(mode);

    /// <summary>Validation harness: completes a paste of another window's pages.</summary>
    public Task<bool>? LastForeignPagePaste => pages.LastForeignPaste;

    /// <summary>Pastes copied pages after the active page (spec 11.4): another window's, else this window's own copy.</summary>
    public bool PastePages() => pages.Paste();

    /// <summary>Blank page (spec 11.5): at the end (add) or after the active page (insert), sized like the reference page by default.</summary>
    public bool AddBlankPage(bool insertAfterActive, NewPageSpec? spec = null)
    {
        if (pages.BlankReference(insertAfterActive) is not { } reference) return false;
        if (spec is null)
        {
            var (w, h) = PageEdits.DisplayedSize(reference.Setup);
            var dialog = new NewNoteDialog(insertAfterActive ? Strings.Get("page.insertBlank") : Strings.Get("page.addBlank"), (Units.PointsToMillimeters(w), Units.PointsToMillimeters(h)),
                reference.Setup.Background?.Kind ?? PageBackgroundKind.Blank) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is null) return false;
            spec = dialog.Result;
        }
        return pages.AddBlank(insertAfterActive, spec);
    }

    /// <summary>The page panel's context menu (<see cref="PagesViewModel.Menu"/>).</summary>
    public ContextMenu PageMenu()
    {
        var menu = new ContextMenu();
        foreach (var entry in pages.Menu())
        {
            if (entry.Text is null) { menu.Items.Add(new Separator()); continue; }
            var item = new MenuItem { Header = entry.Text, IsEnabled = entry.Enabled };
            var command = entry.Command;
            item.Click += async (_, _) => await RunPageMenuAsync(command);
            menu.Items.Add(item);
        }
        return menu;
    }

    private async Task RunPageMenuAsync(PageCommand command)
    {
        switch (command)
        {
            case PageCommand.InsertBlank: AddBlankPage(true); break;
            case PageCommand.CopyWithNotes: CopySelectedPages(PageCopyMode.WithAnnotations); break;
            case PageCommand.CopyPageOnly: CopySelectedPages(PageCopyMode.PageOnly); break;
            case PageCommand.Paste: PastePages(); break;
            case PageCommand.Delete: DeleteSelectedPages(); break;
            case PageCommand.ClearNotes: ClearSelectedPageNotes(); break;
            case PageCommand.Export: await ExportPagesAsync(); break;
            case PageCommand.RotateRight: RotateSelectedPages(true); break;
            case PageCommand.RotateLeft: RotateSelectedPages(false); break;
            case PageCommand.Shrink: ShowPageTransform(false); break;
            case PageCommand.Resize: ShowPageTransform(true); break;
        }
    }

    public Guid[] PageTargets(PageApplyScope scope) => pages.Targets(scope);

    public bool ApplyPageTransform(bool resize, PageTransformRequest request) => pages.ApplyTransform(resize, request);

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
        if (drop) pages.MoveTo(pageDragIds, index);
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

    private PageItemViewModel? ItemAt(Point listPoint) =>
        PageList.InputHitTest(listPoint) is DependencyObject hit && ItemsControl.ContainerFromElement(PageList, hit) is ListBoxItem item ? item.DataContext as PageItemViewModel : null;
}
