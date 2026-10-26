using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.App;

/// <summary>
/// Outline (bookmarks) above the page list (spec 24.4, D13h): off by default, its own toggle in the panel header, the
/// split height remembered. Items point at source pages, so they follow page moves and rotations; an item whose page
/// was deleted is dimmed and does nothing. The item of the current page is highlighted. Editing the outline is out of scope.
/// </summary>
public partial class MainWindow
{
    private PdfOutlineItem[] outline = [];

    /// <summary>The outline of the open document (validation harness).</summary>
    public IReadOnlyList<PdfOutlineItem> Outline => outline;
    public bool OutlineShown => OutlineTree.Visibility == Visibility.Visible;

    private void InitializeOutline()
    {
        // A click (not on the expander) or Enter goes to the item; arrow keys only move the highlight.
        OutlineTree.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemOf(d, out bool expander) is { Tag: PdfOutlineItem item } && !expander) FollowOutline(item);
        };
        OutlineTree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && OutlineTree.SelectedItem is TreeViewItem { Tag: PdfOutlineItem item }) { FollowOutline(item); e.Handled = true; }
        };
        View.ViewChanged += (_, _) => HighlightOutline();
    }

    private static TreeViewItem? ItemOf(DependencyObject d, out bool expander)
    {
        expander = false;
        for (var node = d; node is not null; node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ToggleButton) expander = true;
            if (node is TreeViewItem item) return item;
        }
        return null;
    }

    /// <summary>Reads the outline of a newly shown document and shows the panel part as the setting says.</summary>
    private void LoadOutline()
    {
        try { outline = document?.Sources.Outline() ?? []; }
        catch (InvalidDataException) { outline = []; }
        OutlineTree.Items.Clear();
        foreach (var item in outline) OutlineTree.Items.Add(Node(item, 0));
        OutlineButton.IsEnabled = outline.Length > 0;
        ShowOutline(settings.OutlineVisible && outline.Length > 0);
        RefreshOutline();
    }

    private TreeViewItem Node(PdfOutlineItem item, int depth)
    {
        var node = new TreeViewItem { Header = new TextBlock { Text = item.Title, TextTrimming = TextTrimming.CharacterEllipsis }, Tag = item, IsExpanded = depth == 0, ToolTip = item.Title };
        foreach (var child in item.Children) node.Items.Add(Node(child, depth + 1));
        return node;
    }

    private void OutlineToggled(object sender, RoutedEventArgs e) => SetOutlineVisible(OutlineButton.IsChecked == true);

    /// <summary>Shows or hides the outline and remembers it (app setting).</summary>
    public void SetOutlineVisible(bool visible)
    {
        visible &= outline.Length > 0;
        ShowOutline(visible);
        settings.OutlineVisible = visible;
        settings.Save();
    }

    private void ShowOutline(bool visible)
    {
        OutlineButton.IsChecked = visible;
        OutlineTree.Visibility = OutlineSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        OutlineRow.Height = visible ? new GridLength(settings.OutlineHeight) : new GridLength(0);
        if (visible) HighlightOutline();
    }

    private void OutlineSplitterDragged(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (OutlineRow.ActualHeight < 40) return;
        settings.OutlineHeight = OutlineRow.ActualHeight;
        settings.Save();
    }

    /// <summary>After page edits: items of deleted pages are dimmed (they do nothing).</summary>
    private void RefreshOutline()
    {
        foreach (var node in Nodes(OutlineTree.Items))
            if (node.Tag is PdfOutlineItem item && node.Header is TextBlock text)
                text.Opacity = item.TargetPage is null || TargetPage(item.TargetPage) is not null ? 1 : 0.45;
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
        if (TargetPage(item.TargetPage) is { } page) JumpTo(page, item.TargetPoint);
    }

    /// <summary>Highlights the last item (in outline order) that points at the current page or a page before it.</summary>
    private void HighlightOutline()
    {
        if (!OutlineShown || document is null) return;
        int current = View.CurrentPageIndex;
        TreeViewItem? best = null; int bestPage = -1;
        foreach (var node in Nodes(OutlineTree.Items))
            if (node.Tag is PdfOutlineItem item && TargetPage(item.TargetPage) is { } page && page <= current && page >= bestPage) { best = node; bestPage = page; }
        if (best is null || best.IsSelected) return;
        best.IsSelected = true; // only a highlight: following happens on a click or Enter
    }
}
