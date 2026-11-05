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
/// The left page panel (spec 3.2-5, 11, the WPF MainWindow.Pages): thumbnails in document order. A click goes to a page
/// and makes it active; Ctrl/Shift or the multi-select toggle select several; dragging moves the selected pages keeping
/// their order. The context menu, the 페이지 tab and Ctrl+C/V/Delete (with the list focused) run the page commands of
/// <see cref="PagesViewModel"/>; every structure change is one Undo unit.
/// </summary>
public sealed partial class MainWindow : IPageListView, IPageThumbnailSource
{
    private Point pageDragStart;
    private bool pageDragArmed, pageDragging;
    private PageItemViewModel? pageDragPressed;
    private Guid[] pageDragIds = [];
    private ScrollViewer pageScroller = null!;
    private const double DragDistance = 4;

    private PagesViewModel pages => viewModel.Pages;

    public IReadOnlyList<PageItemViewModel> PageThumbnails => pages.Items;
    public int ActivePageIndex => pages.ActivePageIndex;
    public Guid[] SelectedPageIds => pages.SelectedPageIds;
    public ListBox PageListBox => PageList;

    IReadOnlyCollection<Guid> IPageListView.SelectedPageIds => [.. (PageList.SelectedItems?.OfType<PageItemViewModel>() ?? []).Select(t => t.PageId)];

    void IPageListView.Select(IReadOnlyCollection<Guid> ids)
    {
        PageList.SelectedItems?.Clear();
        foreach (var t in pages.Items.Where(t => ids.Contains(t.PageId))) PageList.SelectedItems?.Add(t);
    }

    void IPageListView.FollowActive(PageItemViewModel item)
    {
        if (PageList.SelectedItems?.Count > 1) return;
        PageList.SelectedItem = item;
        PageList.ContainerFromItem(item)?.BringIntoView();
    }

    IPageThumbnailSource IPageListView.Thumbnails => this;


    private void InitializePagePanel()
    {
        // The list and the 빈 페이지 추가 button below it scroll together (the WPF list held the button as its last row).
        var host = (Panel)PageList.Parent!;
        host.Children.Remove(PageList);
        var add = new Button { Name = "AddPageButton", Content = Strings.Get("page.addBlank"), Margin = new(6, 12, 6, 16), Padding = new(8), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        add.Click += (_, _) => AddBlankPage(false);
        ScrollViewer.SetVerticalScrollBarVisibility(PageList, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        pageScroller = new ScrollViewer { Name = "PageScroller", HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = new StackPanel { Children = { PageList, add } } };
        host.Children.Insert(0, pageScroller);

        PageList.ItemTemplate = new FuncDataTemplate<PageItemViewModel>((item, _) => Thumbnail(), supportsRecycling: true);
        PageList.ItemsSource = pages.Items;
        PageList.SelectionChanged += (_, _) =>
        {
            pages.SelectionChanged();
            if (pages.Syncing) return;
            if (PageList.SelectedItems?.Count == 1 && PageList.SelectedItem is PageItemViewModel thumb) pages.Activate(thumb.PageId, scroll: true);
        };
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
        Canvas.ViewChanged += (_, _) => pages.FollowView();
        pages.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PagesViewModel.PanelVisible)) ShowPagePanel(pages.PanelVisible); };
        MultiSelectButton.Click += (_, _) =>
        {
            PageList.SelectionMode = MultiSelectButton.IsChecked == true ? SelectionMode.Multiple | SelectionMode.Toggle : SelectionMode.Multiple;
            viewModel.Ribbon.Auto(RibbonTab.Page);
        };
        PagePanelHandle.Click += (_, _) => SetPagePanelVisible(!PagePanel.IsVisible);
        ScalingChanged += (_, _) => _ = pages.InvalidateThumbnailsAsync(); // another monitor scale: thumbnails at its pixels (D13e)
        ShowPagePanel(pages.PanelVisible);
    }

    /// <summary>One row: the thumbnail in a frame (the accent while it is the active page) and its number.</summary>
    private static Control Thumbnail()
    {
        var image = new Image { Stretch = Stretch.Fill };
        image.Bind(Image.SourceProperty, new Binding(nameof(PageItemViewModel.Image)));
        image.Bind(WidthProperty, new Binding(nameof(PageItemViewModel.ImageWidth)));
        image.Bind(HeightProperty, new Binding(nameof(PageItemViewModel.ImageHeight)));
        var frame = new Border { Name = "Frame", BorderThickness = new(1), HorizontalAlignment = HorizontalAlignment.Center, Child = image };
        frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("Paper"));
        frame.Bind(Border.BorderThicknessProperty, new Binding(nameof(PageItemViewModel.IsActive)) { Converter = new FuncValueConverter<bool, Thickness>(on => new(on ? 2 : 1)) });
        var number = new TextBlock { Name = "Number", HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 3, 0, 0) };
        number.Bind(TextBlock.TextProperty, new Binding(nameof(PageItemViewModel.Number)));
        number.Bind(TextBlock.FontSizeProperty, number.GetResourceObservable("FontSize.Caption"));
        number.Bind(TextBlock.FontWeightProperty, new Binding(nameof(PageItemViewModel.IsActive)) { Converter = new FuncValueConverter<bool, FontWeight>(on => on ? FontWeight.Bold : FontWeight.Normal) });
        // The colors are looked up when the binding converts: bound again when the theme or the accent changes (system theme, A6).
        void BindColors()
        {
            frame.Bind(Border.BorderBrushProperty, new Binding(nameof(PageItemViewModel.IsActive)) { Converter = new FuncValueConverter<bool, IBrush?>(on => Brush(frame, on ? "Accent" : "Page.Border")) });
            number.Bind(TextBlock.ForegroundProperty, new Binding(nameof(PageItemViewModel.IsActive)) { Converter = new FuncValueConverter<bool, IBrush?>(on => Brush(number, on ? "Accent" : "Text.Muted")) });
        }
        BindColors();
        frame.ActualThemeVariantChanged += (_, _) => BindColors();
        frame.ResourcesChanged += (_, _) => BindColors();
        return new StackPanel { Margin = new(2, 6, 2, 2), Children = { frame, number } };
    }

    private static IBrush? Brush(Control control, string key) =>
        control.TryFindResource(key, control.ActualThemeVariant, out var value) ? value as IBrush : Application.Current?.TryFindResource(key, out var app) == true ? app as IBrush : null;

    public bool PagePanelVisible => PagePanel.IsVisible;

    /// <summary>Shows or hides the page list (the handle and the 페이지 tab button agree) and remembers it (spec 3.2-5).</summary>
    public void SetPagePanelVisible(bool visible)
    {
        pages.PanelVisible = visible;
        ShowPagePanel(visible);
    }

    private void ShowPagePanel(bool visible)
    {
        PagePanelButton.IsChecked = visible;
        PagePanel.IsVisible = visible;
        PagePanelHandleIcon.Glyph = visible ? "ChevronLeft" : "ChevronRight"; // ‹ hides, › shows
        ToolTip.SetTip(PagePanelHandle, visible ? Strings.Get("page.hideList") : Strings.Get("page.list"));
        PagePanelHandle.Classes.Set("closed", !visible); // the accent color while the list is hidden
    }

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

    /// <summary>Blank page (spec 11.5): at the end (add) or after the active page (insert), sized like the reference page by default.</summary>
    public bool AddBlankPage(bool insertAfterActive, NewPageSpec? spec = null)
    {
        if (pages.BlankReference(insertAfterActive) is not { } reference) return false;
        if (spec is null)
        {
            var (w, h) = PageEdits.DisplayedSize(reference.Setup);
            var dialog = new NewNoteDialog(insertAfterActive ? Strings.Get("page.insertBlank") : Strings.Get("page.addBlank"),
                (Units.PointsToMillimeters(w), Units.PointsToMillimeters(h)), reference.Setup.Background?.Kind ?? PageBackgroundKind.Blank);
            if (!dialogs.ShowWindow(dialog) || dialog.Result is null) return false;
            spec = dialog.Result;
        }
        return pages.AddBlank(insertAfterActive, spec);
    }

    /// <summary>The page menu opened last (tests).</summary>
    public ContextMenu? LastPageMenu { get; private set; }

    /// <summary>The page list's context menu (<see cref="PagesViewModel.Menu"/>).</summary>
    public ContextMenu OpenPageMenu()
    {
        var menu = new ContextMenu();
        foreach (var entry in pages.Menu())
        {
            if (entry.Text is null) { menu.Items.Add(new Separator()); continue; }
            var command = entry.Command;
            menu.Items.Add(Menus.Item(entry.Text, () => _ = RunPageMenuAsync(command), enabled: entry.Enabled));
        }
        menu.Open(PageList);
        return LastPageMenu = menu;
    }

    private async Task RunPageMenuAsync(PageCommand command)
    {
        switch (command)
        {
            case PageCommand.InsertBlank: AddBlankPage(true); break;
            case PageCommand.CopyWithNotes: pages.Copy(PageCopyMode.WithAnnotations); break;
            case PageCommand.CopyPageOnly: pages.Copy(PageCopyMode.PageOnly); break;
            case PageCommand.Paste: pages.Paste(); break;
            case PageCommand.Delete: pages.DeleteSelected(); break;
            case PageCommand.ClearNotes: ClearSelectedPageNotes(); break;
            case PageCommand.Export: await ExportPagesAsync(); break;
            case PageCommand.RotateRight: pages.RotateSelected(true); break;
            case PageCommand.RotateLeft: pages.RotateSelected(false); break;
            case PageCommand.Shrink: ShowPageTransform(false); break;
            case PageCommand.Resize: ShowPageTransform(true); break;
        }
    }

    /// <summary>페이지 크기·내용 축소 (spec 11.6–11.8): the window with a preview of the active page; the targets are fixed when it opens.</summary>
    public bool ShowPageTransform(bool resize)
    {
        if (Document is not { } doc || busy) return false;
        var model = doc.Session.Current;
        var active = model.Pages[ActivePageIndex];
        var targets = Enum.GetValues<PageApplyScope>().ToDictionary(scope => scope, pages.Targets);
        double zoom = 700 / (Math.Max(active.Setup.Frame.DisplayWidthPt, active.Setup.Frame.DisplayHeightPt) * Units.DipsPerPoint);
        var dialog = new PageTransformDialog(resize, active.Setup, scope => targets[scope].Select(id => model.GetPage(id).Setup).ToArray(),
            PageSnapshots.Render(doc, model, active, zoom, RenderScaling));
        return dialogs.ShowWindow(dialog) && dialog.Result is { } request && pages.ApplyTransform(resize, request with { TargetPageIds = targets[request.Scope] });
    }

    /// <summary>
    /// 이 페이지의 필기 삭제 (spec 11.11): every annotation of the selected pages, locked ones too, after one confirmation, as one
    /// Undo unit. True when something was deleted.
    /// </summary>
    public bool ClearSelectedPageNotes()
    {
        if (Document is not { } doc) return false;
        FinishTextEditing();
        var ids = SelectedPageIds;
        var selected = ids.Select(doc.Session.Current.GetPage).ToList();
        int count = selected.Sum(p => p.Annotations.Count), locked = selected.Sum(p => p.Annotations.Count(a => a.Locked));
        if (count == 0) { viewModel.Status = Strings.Get("clearNotes.nothing"); return false; }
        string which = selected.Count == 1 ? Strings.Get("clearNotes.thisPage") : Strings.Format("clearNotes.selectedPages", ("count", selected.Count));
        string lockedNote = locked > 0 ? Strings.Format("clearNotes.lockedIncluded", ("count", locked)) : "";
        if (!Confirm(Strings.Format("clearNotes.confirm", ("which", which), ("count", count), ("locked", lockedNote)), Editor.Platform.NoticeKind.Warning)) return false;
        selectTool.SetSelection([]);
        bool done = pages.Run(m => DocumentEdits.ClearPages(m, ids));
        if (done) viewModel.Status = Strings.Format("clearNotes.done", ("count", count));
        return done;
    }

    private void PageListKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == KeyModifiers.Meta;
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None) { pages.DeleteSelected(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { pages.Copy(); e.Handled = true; }
        else if (ctrl && e.Key == Key.V) { pages.Paste(); e.Handled = true; }
        else if (ctrl && e.Key == Key.A) { PageList.SelectAll(); e.Handled = true; }
    }

    // ----- drag to reorder (spec 11.1–11.2): pointer capture, a drop line between rows -----

    private PageItemViewModel? ItemAt(PointerEventArgs e) =>
        (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as PageItemViewModel;

    private void PageListPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PageList).Properties.IsLeftButtonPressed) return;
        pageDragStart = e.GetPosition(PageList);
        var thumb = ItemAt(e);
        bool selected = thumb is not null && PageList.SelectedItems?.Contains(thumb) == true;
        pageDragArmed = selected;
        pageDragPressed = null;
        // Page work shows the 페이지 tab (spec 3.3, D14g): a multi-selection or a drag; a plain click only goes to the page.
        if (thumb is not null && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || MultiSelectButton.IsChecked == true))
            viewModel.Ribbon.Auto(RibbonTab.Page);
        // Pressing a page of a multi-selection keeps it so all of it can be dragged; a click without a drag selects that
        // page alone on release, as usual.
        if (selected && e.KeyModifiers == KeyModifiers.None && PageList.SelectedItems?.Count > 1 && MultiSelectButton.IsChecked != true)
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
            PageList.SelectedItem = thumb; // a plain click on a page of the selection
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
