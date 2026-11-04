using PdfNotes.Pdf.Reading;

namespace PdfNotes.ViewModels;

/// <summary>
/// Outline (bookmarks) above the page list (spec 24.4, D13h; P1 V2): off by default, its own toggle in the panel header,
/// the split height remembered. Items point at source pages, so they follow page moves and rotations; an item whose
/// page was deleted is dimmed and does nothing. The view builds the tree and highlights <see cref="CurrentItem"/>.
/// </summary>
public sealed class OutlineViewModel(MainViewModel owner) : ObservableObject
{
    private PdfOutlineItem[] items = [];
    private bool isVisible;

    /// <summary>The outline of the open document.</summary>
    public IReadOnlyList<PdfOutlineItem> Items => items;

    /// <summary>True when the document has an outline (the toggle is enabled).</summary>
    public bool IsAvailable => items.Length > 0;

    public bool IsVisible { get => isVisible; private set => SetProperty(ref isVisible, value); }

    /// <summary>The remembered height of the outline part.</summary>
    public double Height => owner.Settings.OutlineHeight;

    /// <summary>Reads the outline of a newly shown document and shows it as the setting says.</summary>
    public void Load()
    {
        try { items = owner.Document?.Sources.Outline() ?? []; }
        catch (InvalidDataException) { items = []; }
        isVisible = owner.Settings.OutlineVisible && IsAvailable;
        OnPropertyChanged(nameof(Items)); // the view builds the new tree with its visibility and highlight
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsVisible));
    }

    /// <summary>Shows or hides the outline and remembers it (app setting).</summary>
    public void SetVisible(bool visible)
    {
        visible &= IsAvailable;
        IsVisible = visible;
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
}
