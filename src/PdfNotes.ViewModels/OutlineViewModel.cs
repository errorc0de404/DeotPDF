using PdfNotes.Pdf.Reading;

namespace PdfNotes.ViewModels;

/// <summary>One item of the outline tree: its title, children, whether it is open, and dimmed when its page was deleted.</summary>
public sealed class OutlineNodeViewModel : ObservableObject
{
    private bool expanded;
    private double opacity = 1;

    public OutlineNodeViewModel(PdfOutlineItem item, int depth)
    {
        Item = item;
        expanded = depth == 0;
        Children = [.. item.Children.Select(c => new OutlineNodeViewModel(c, depth + 1))];
    }

    public PdfOutlineItem Item { get; }
    public string Title => Item.Title;
    public IReadOnlyList<OutlineNodeViewModel> Children { get; }

    /// <summary>The first level is open, the rest closed.</summary>
    public bool IsExpanded { get => expanded; set => SetProperty(ref expanded, value); }

    /// <summary>1, or dimmed for an item whose page was deleted (it does nothing).</summary>
    public double Opacity { get => opacity; set => SetProperty(ref opacity, value); }
}

/// <summary>
/// Outline (bookmarks) above the page list (spec 24.4, D13h; P1 V2, R4e): off by default, its own toggle in the panel
/// header, the split height remembered. Items point at source pages, so they follow page moves and rotations; an item
/// whose page was deleted is dimmed and does nothing. The item of the page in view is highlighted.
/// </summary>
public sealed class OutlineViewModel(MainViewModel owner) : ObservableObject
{
    private PdfOutlineItem[] items = [];
    private OutlineNodeViewModel[] nodes = [];
    private OutlineNodeViewModel? highlighted;
    private bool isVisible;

    /// <summary>The outline of the open document.</summary>
    public IReadOnlyList<PdfOutlineItem> Items => items;

    /// <summary>The tree the view shows.</summary>
    public IReadOnlyList<OutlineNodeViewModel> Nodes => nodes;

    /// <summary>True when the document has an outline (the toggle is enabled).</summary>
    public bool IsAvailable => items.Length > 0;

    public bool IsVisible
    {
        get => isVisible;
        private set
        {
            if (!SetProperty(ref isVisible, value)) return;
            if (value) Highlight();
        }
    }

    /// <summary>The highlighted item (only a highlight: following happens on a click or Enter).</summary>
    public OutlineNodeViewModel? Highlighted { get => highlighted; set => SetProperty(ref highlighted, value); }

    /// <summary>The remembered height of the outline part.</summary>
    public double Height => owner.Settings.OutlineHeight;

    /// <summary>The panel header's toggle.</summary>
    public Command ToggleCommand => field ??= new(() => SetVisible(!IsVisible), () => IsAvailable);

    /// <summary>Reads the outline of a newly shown document and shows it as the setting says.</summary>
    public void Load()
    {
        try { items = owner.Document?.Sources.Outline() ?? []; }
        catch (InvalidDataException) { items = []; }
        nodes = [.. items.Select(i => new OutlineNodeViewModel(i, 0))];
        highlighted = null;
        isVisible = owner.Settings.OutlineVisible && IsAvailable;
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(Nodes));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsVisible));
        ToggleCommand.Refresh();
        Refresh();
    }

    /// <summary>Shows or hides the outline and remembers it (app setting).</summary>
    public void SetVisible(bool visible)
    {
        visible &= IsAvailable;
        IsVisible = visible;
        OnPropertyChanged(nameof(IsVisible)); // also when unchanged: the toggle may have been clicked without an outline
        owner.Settings.OutlineVisible = visible;
        owner.SaveSettings();
    }

    /// <summary>The user dragged the splitter: heights below 40 are not remembered.</summary>
    public void RememberHeight(double height)
    {
        if (height < 40) return;
        owner.Settings.OutlineHeight = height;
        owner.SaveSettings();
    }

    /// <summary>After page edits: items of deleted pages are dimmed; the highlight follows.</summary>
    public void Refresh()
    {
        foreach (var node in AllNodes(nodes)) node.Opacity = Reachable(node.Item) ? 1 : 0.45;
        Highlight();
    }

    /// <summary>Highlights the last item (in outline order) that points at the page in view or a page before it.</summary>
    public void Highlight()
    {
        if (!IsVisible || owner.Document is null || CurrentItem(owner.Navigation.CurrentPageIndex) is not { } item) return;
        if (AllNodes(nodes).FirstOrDefault(n => ReferenceEquals(n.Item, item)) is { } node) Highlighted = node;
    }

    /// <summary>A click (not on the expander) or Enter: goes to the item's destination, remembered for Alt+← like a link.</summary>
    public void Follow(PdfOutlineItem item)
    {
        if (Target(item) is { } page) owner.Navigation.JumpTo(page, item.TargetPoint);
    }

    /// <summary>The page index the item goes to, or null (no destination, or its page was deleted).</summary>
    public int? Target(PdfOutlineItem item) => owner.PageShowing(item.TargetPage);

    /// <summary>False for an item whose page was deleted (shown dimmed).</summary>
    public bool Reachable(PdfOutlineItem item) => item.TargetPage is null || Target(item) is not null;

    /// <summary>The last item (in outline order) that points at <paramref name="currentPage"/> or a page before it.</summary>
    public PdfOutlineItem? CurrentItem(int currentPage)
    {
        PdfOutlineItem? best = null; int bestPage = -1;
        foreach (var item in All(items))
            if (Target(item) is { } page && page <= currentPage && page >= bestPage) { best = item; bestPage = page; }
        return best;
    }

    /// <summary>Items in outline order (depth first).</summary>
    public static IEnumerable<PdfOutlineItem> All(IEnumerable<PdfOutlineItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in All(item.Children)) yield return child;
        }
    }

    private static IEnumerable<OutlineNodeViewModel> AllNodes(IEnumerable<OutlineNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in AllNodes(node.Children)) yield return child;
        }
    }
}
