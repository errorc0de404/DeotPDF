using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PdfNotes.Editor.Localization;
using PdfNotes.Pdf.Reading;
using PdfNotes.ViewModels;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// The outline above the page list (spec 24.4), the Ctrl+F search bar (spec 12), the loading card, other apps'
/// annotations (14.6) and the new-version button (roadmap 1.1) — the state is the view model's, these show it.
/// </summary>
public sealed partial class MainWindow
{
    // ----- outline -----

    public IReadOnlyList<PdfOutlineItem> Outline => viewModel.Outline.Items;
    public bool OutlineShown => OutlineTree.IsVisible;
    public TreeView OutlineView => OutlineTree;

    private bool syncingOutline;

    private void InitializeOutline()
    {
        OutlineButton.Click += (_, _) => SetOutlineVisible(OutlineButton.IsChecked == true);
        // A click (not on the expander) or Enter goes to the item; arrow keys only move the highlight.
        OutlineTree.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || (e.Source as Avalonia.Visual)?.FindAncestorOfType<Avalonia.Controls.Primitives.ToggleButton>(true) is not null) return;
            if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<TreeViewItem>(true) is { Tag: PdfOutlineItem item }) FollowOutline(item);
        }, RoutingStrategies.Bubble, handledEventsToo: true); // once per click (Tunnel | Bubble would run it twice)
        OutlineTree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && OutlineTree.SelectedItem is TreeViewItem { Tag: PdfOutlineItem item }) { FollowOutline(item); e.Handled = true; }
        };
        OutlineSplitter.DragCompleted += (_, _) => viewModel.Outline.RememberHeight(PagePanelBody.RowDefinitions[0].ActualHeight);
        Canvas.ViewChanged += (_, _) => HighlightOutline();
        viewModel.Outline.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OutlineViewModel.Items)) BuildOutline();
            else if (e.PropertyName == nameof(OutlineViewModel.IsVisible)) ShowOutline(viewModel.Outline.IsVisible);
            else if (e.PropertyName == nameof(OutlineViewModel.IsAvailable)) OutlineButton.IsEnabled = viewModel.Outline.IsAvailable;
        };
        OutlineButton.IsEnabled = viewModel.Outline.IsAvailable;
    }

    private void BuildOutline()
    {
        OutlineTree.Items.Clear();
        foreach (var item in viewModel.Outline.Items) OutlineTree.Items.Add(Node(item, 0));
        OutlineButton.IsEnabled = viewModel.Outline.IsAvailable;
        ShowOutline(viewModel.Outline.IsVisible);
        RefreshOutline();
    }

    private static TreeViewItem Node(PdfOutlineItem item, int depth)
    {
        var node = new TreeViewItem { Header = new TextBlock { Text = item.Title, TextTrimming = TextTrimming.CharacterEllipsis }, Tag = item, IsExpanded = depth == 0 };
        ToolTip.SetTip(node, item.Title);
        foreach (var child in item.Children) node.Items.Add(Node(child, depth + 1));
        return node;
    }

    /// <summary>Shows or hides the outline and remembers it (app setting).</summary>
    public void SetOutlineVisible(bool visible)
    {
        viewModel.Outline.SetVisible(visible);
        ShowOutline(viewModel.Outline.IsVisible); // also when unchanged: the toggle may have been clicked without an outline
    }

    private void ShowOutline(bool visible)
    {
        OutlineButton.IsChecked = visible;
        OutlineTree.IsVisible = OutlineSplitter.IsVisible = visible;
        PagePanelBody.RowDefinitions[0].Height = visible ? new GridLength(viewModel.Outline.Height) : new GridLength(0);
        if (visible) HighlightOutline();
    }

    /// <summary>After page edits: items of deleted pages are dimmed (they do nothing).</summary>
    private void RefreshOutline()
    {
        foreach (var node in Nodes(OutlineTree.Items))
            if (node.Tag is PdfOutlineItem item && node.Header is TextBlock text) text.Opacity = viewModel.Outline.Reachable(item) ? 1 : 0.45;
        HighlightOutline();
    }

    private static IEnumerable<TreeViewItem> Nodes(ItemCollection items)
    {
        foreach (var node in items.OfType<TreeViewItem>())
        {
            yield return node;
            foreach (var child in Nodes(node.Items)) yield return child;
        }
    }

    /// <summary>A click on an item goes to its destination (remembered for Alt+←, like a link).</summary>
    public void FollowOutline(PdfOutlineItem item)
    {
        if (viewModel.Outline.Target(item) is { } page) viewModel.Navigation.JumpTo(page, item.TargetPoint);
    }

    /// <summary>Highlights the last item (in outline order) that points at the current page or a page before it.</summary>
    private void HighlightOutline()
    {
        if (!OutlineShown || Document is null || syncingOutline || viewModel.Outline.CurrentItem(Canvas.CurrentPageIndex) is not { } item) return;
        var best = Nodes(OutlineTree.Items).FirstOrDefault(n => ReferenceEquals(n.Tag, item));
        if (best is null || best.IsSelected) return;
        syncingOutline = true;
        try { OutlineTree.SelectedItem = best; } // only a highlight: following happens on a click or Enter
        finally { syncingOutline = false; }
    }

    // ----- search -----

    public bool IsSearchOpen => viewModel.Search.IsOpen;
    public TextBox SearchField => SearchBox;

    private void InitializeSearch()
    {
        var search = viewModel.Search;
        search.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SearchViewModel.IsOpen): SearchBar.IsVisible = search.IsOpen; break;
                case nameof(SearchViewModel.CountText): SearchCount.Text = search.CountText; break;
            }
        };
        SearchBox.TextChanged += (_, _) => search.Query = SearchBox.Text ?? "";
        SearchCaseButton.Click += (_, _) => search.MatchCase = SearchCaseButton.IsChecked == true;
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.F3) { search.Enter(backwards: e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseSearch(); e.Handled = true; }
        };
        SearchPreviousButton.Click += (_, _) => search.Previous();
        SearchNextButton.Click += (_, _) => search.Next();
        SearchCloseButton.Click += (_, _) => CloseSearch();
    }

    public void OpenSearch()
    {
        FinishTextEditing();
        viewModel.Search.Open();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void CloseSearch()
    {
        viewModel.Search.Close();
        Canvas.Focus();
    }

    // ----- the loading card -----

    public bool LoadingShown => viewModel.Loading.IsShown;

    private void InitializeLoading() => viewModel.Loading.PropertyChanged += (_, e) =>
    {
        var loading = viewModel.Loading;
        switch (e.PropertyName)
        {
            case nameof(LoadingViewModel.BlocksInput): LoadingOverlay.IsHitTestVisible = loading.BlocksInput; break;
            case nameof(LoadingViewModel.IsShown): LoadingOverlay.IsVisible = loading.IsShown; break;
            case nameof(LoadingViewModel.Text): LoadingText.Text = loading.Text; break;
        }
    };

    /// <summary>The card waits until the first pages are drawn: the render queue is idle once the canvas asked for them.</summary>
    void IMainView.WaitForFirstPages()
    {
        void Check()
        {
            if (!viewModel.Loading.WaitsForPages) return;
            if (queue is null || queue.IsIdle) viewModel.Loading.PagesDrawn();
            else DispatcherTimer.RunOnce(Check, TimeSpan.FromMilliseconds(50));
        }
        Dispatcher.UIThread.Post(Check, DispatcherPriority.Background);
    }

    // ----- other apps' annotations (spec 14.6, D14g) -----

    public int ForeignAnnotationCount => viewModel.Foreign.Count;

    private void InitializeForeign() => viewModel.Foreign.PropertyChanged += (_, _) => UpdatePageCommands();

    /// <summary>다른 프로그램 주석 편집: converts and tells how many became editable. Returns that number.</summary>
    public Task<int> ConvertForeignAsync() => viewModel.Foreign.ConvertFromRibbonAsync();

    // ----- a new version (roadmap 1.1) -----

    /// <summary>The start-up check, run by <see cref="App"/> after the window is shown (test windows skip it).</summary>
    public Task CheckForUpdatesAtStartAsync() => viewModel.Update.CheckAtStartAsync();
}
