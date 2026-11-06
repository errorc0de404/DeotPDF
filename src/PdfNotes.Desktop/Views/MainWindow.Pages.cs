using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Desktop.Canvas;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Services;
using PdfNotes.ViewModels;
using Page = PdfNotes.Core.Models.Page;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// The left page panel (spec 3.2-5, 11, the WPF MainWindow.Pages): thumbnails in document order. The rows, their
/// selection, the panel's toggles and the page commands are bound to <see cref="PagesViewModel"/> (R4e); this file has
/// the input that is the window's — dragging rows to reorder, the context menu, the list's keys — and draws thumbnails.
/// </summary>
public sealed partial class MainWindow : IPageThumbnailSource
{
    private Point pageDragStart;
    private bool pageDragArmed, pageDragging;
    private PageItemViewModel? pageDragPressed;
    private Guid[] pageDragIds = [];
    private const double DragDistance = 4;

    private PagesViewModel pages => viewModel.Pages;

    public IReadOnlyList<PageItemViewModel> PageThumbnails => pages.Items;
    public int ActivePageIndex => pages.ActivePageIndex;
    public Guid[] SelectedPageIds => pages.SelectedPageIds;
    public ListBox PageListBox => PageList;

    private void InitializePagePanel()
    {
        PageList.AddHandler(PointerPressedEvent, PageListPressed, RoutingStrategies.Tunnel);
        PageList.AddHandler(PointerMovedEvent, PageListMoved, RoutingStrategies.Tunnel);
        PageList.AddHandler(PointerReleasedEvent, PageListReleased, RoutingStrategies.Tunnel);
        PageList.PointerCaptureLost += (_, _) => FinishPageDrag(drop: false);
        PageList.ContextRequested += (_, e) =>
        {
            viewModel.Ribbon.Auto(RibbonTab.Page);
            OpenPageMenu();
            e.Handled = true;
        };
        PageList.KeyDown += PageListKeyDown;
        ScalingChanged += (_, _) => _ = pages.InvalidateThumbnailsAsync(); // another monitor scale: thumbnails at its pixels (D13e)
    }

    public bool PagePanelVisible => PagePanel.IsVisible;

    /// <summary>Shows or hides the page list (the handle and the 페이지 tab button agree) and remembers it (spec 3.2-5).</summary>
    public void SetPagePanelVisible(bool visible) => pages.PanelVisible = visible;

    /// <summary>A thumbnail: the PDF background rendered in the background, the notes drawn on top.</summary>
    async Task<PageThumbnail?> IPageThumbnailSource.RenderAsync(OpenDocument doc, DocumentModel model, Page page, double width, bool notesHidden, CancellationToken cancel)
    {
        double zoom = width / (page.Setup.Frame.DisplayWidthPt * Units.DipsPerPoint);
        var bitmap = await PageSnapshots.RenderAsync(doc, model, page, zoom, RenderScaling, notesHidden, viewModel.Display.DarkPages, cancel);
        if (bitmap is null) return null;
        var t = new ViewTransform(page.Setup.Frame, zoom, RenderScaling, new(0, 0));
        return new PageThumbnail(bitmap, t.PageWidthDip, t.PageHeightDip, Complete: true); // Skia lays text out at once
    }

    // ----- commands -----

    /// <summary>Blank page (spec 11.5): at the end (add) or after the active page (insert); without <paramref name="spec"/> the new page window asks.</summary>
    public bool AddBlankPage(bool insertAfterActive, NewPageSpec? spec = null) =>
        spec is null ? pages.AddBlankWithDialog(insertAfterActive) : pages.AddBlank(insertAfterActive, spec);

    /// <summary>The page menu opened last (tests).</summary>
    public ContextMenu? LastPageMenu { get; private set; }

    /// <summary>The page list's context menu (<see cref="PagesViewModel.Menu"/>).</summary>
    public ContextMenu OpenPageMenu()
    {
        var menu = new ContextMenu();
        foreach (var entry in pages.Menu())
        {
            if (entry.Text is null) { menu.Items.Add(new Separator()); continue; }
            var command = pages.CommandFor(entry.Command);
            menu.Items.Add(Menus.Item(entry.Text, () => command.Execute(null), enabled: entry.Enabled));
        }
        menu.Open(PageList);
        return LastPageMenu = menu;
    }

    /// <summary>페이지 크기·내용 축소 (spec 11.6–11.8).</summary>
    public bool ShowPageTransform(bool resize) => pages.TransformWithDialog(resize);

    /// <summary>이 페이지의 필기 삭제 (spec 11.11).</summary>
    public bool ClearSelectedPageNotes() => pages.ClearSelectedNotes();

    private void PageListKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == KeyModifiers.Meta;
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None) { pages.DeleteSelected(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { pages.Copy(); e.Handled = true; }
        else if (ctrl && e.Key == Key.V) { pages.Paste(); e.Handled = true; }
        else if (ctrl && e.Key == Key.A) { pages.SelectAll(); e.Handled = true; }
    }

    // ----- drag to reorder (spec 11.1–11.2): pointer capture, a drop line between rows -----

    private PageItemViewModel? ItemAt(PointerEventArgs e) =>
        (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as PageItemViewModel;

    private void PageListPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PageList).Properties.IsLeftButtonPressed) return;
        pageDragStart = e.GetPosition(PageList);
        var thumb = ItemAt(e);
        bool selected = thumb?.IsSelected == true;
        pageDragArmed = selected;
        pageDragPressed = null;
        // Page work shows the 페이지 tab (spec 3.3, D14g): a multi-selection or a drag; a plain click only goes to the page.
        if (thumb is not null && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || pages.MultiSelect))
            viewModel.Ribbon.Auto(RibbonTab.Page);
        // Pressing a page of a multi-selection keeps it so all of it can be dragged; a click without a drag selects that
        // page alone on release, as usual.
        if (selected && e.KeyModifiers == KeyModifiers.None && pages.Items.Count(t => t.IsSelected) > 1 && !pages.MultiSelect)
        {
            pageDragPressed = thumb;
            PageList.Focus();
            e.Handled = true;
        }
    }

    private void PageListMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(PageList);
        if (pageDragging) { ShowDropLine(DropIndex(p)); e.Handled = true; return; }
        if (!pageDragArmed || !e.GetCurrentPoint(PageList).Properties.IsLeftButtonPressed) return;
        if (Math.Abs(p.X - pageDragStart.X) < DragDistance && Math.Abs(p.Y - pageDragStart.Y) < DragDistance) return;
        pageDragArmed = false;
        pageDragIds = SelectedPageIds; // the list may change its selection while the pointer passes over rows
        pageDragging = true;
        e.Pointer.Capture(PageList);
        ShowDropLine(DropIndex(p));
        viewModel.Ribbon.Auto(RibbonTab.Page);
    }

    private void PageListReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (pageDragging)
        {
            int index = DropIndex(e.GetPosition(PageList));
            pageDragging = false;
            PageDropLine.IsVisible = false;
            e.Pointer.Capture(null);
            pages.MoveTo(pageDragIds, index);
            e.Handled = true;
        }
        else if (pageDragPressed is { } thumb)
        {
            pages.SelectOnly(thumb.PageId); // a plain click on a page of the selection
            e.Handled = true;
        }
        pageDragArmed = false;
        pageDragPressed = null;
    }

    private void FinishPageDrag(bool drop)
    {
        if (!pageDragging) return;
        pageDragging = false;
        PageDropLine.IsVisible = false;
        if (drop) pages.MoveTo(pageDragIds, pages.Items.Count);
    }

    /// <summary>Drag a page's row with the mouse onto another place (tests): the same path as a real drag.</summary>
    public bool MovePagesTo(Guid[] ids, int insertBefore) => pages.MoveTo(ids, insertBefore);

    private void ShowDropLine(int index)
    {
        var host = PageDropLine.Parent as Visual;
        Control? Row(int i) => i >= 0 && i < pages.Items.Count ? PageList.ContainerFromIndex(i) : null;
        double? y = null;
        if (Row(index) is { IsVisible: true } row && row.TranslatePoint(new Point(0, 0), host!) is { } top) y = top.Y;
        else if (Row(pages.Items.Count - 1) is { IsVisible: true } last && last.TranslatePoint(new Point(0, last.Bounds.Height), host!) is { } bottom) y = bottom.Y;
        if (y is not { } at) { PageDropLine.IsVisible = false; return; }
        Avalonia.Controls.Canvas.SetTop(PageDropLine, Math.Max(0, at - 1.5));
        PageDropLine.Width = Math.Max(0, PageList.Bounds.Width - 16);
        PageDropLine.IsVisible = true;
    }

    /// <summary>Insertion index for a drop: before the row under the point, or after it when below its middle.</summary>
    public int DropIndex(Point listPoint)
    {
        for (int i = 0; i < pages.Items.Count; i++)
        {
            if (PageList.ContainerFromIndex(i) is not { IsVisible: true } row || row.TranslatePoint(new Point(0, 0), PageList) is not { } top) continue;
            if (listPoint.Y < top.Y + row.Bounds.Height / 2) return i;
        }
        return pages.Items.Count;
    }
}
