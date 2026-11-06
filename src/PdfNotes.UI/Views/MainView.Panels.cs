using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PdfNotes.Editor.Localization;
using PdfNotes.Pdf.Reading;
using PdfNotes.ViewModels;

namespace PdfNotes.UI.Views;

/// <summary>
/// The outline above the page list (spec 24.4), the Ctrl+F search bar (spec 12), the loading card, other apps'
/// annotations (14.6) and the new-version button (roadmap 1.1) — the state is the view model's, these show it.
/// </summary>
public sealed partial class MainView
{
    // ----- outline (bound to OutlineViewModel, R4e) -----

    public IReadOnlyList<PdfOutlineItem> Outline => viewModel.Outline.Items;
    public bool OutlineShown => OutlineTree.IsVisible;
    public TreeView OutlineView => OutlineTree;

    private void InitializeOutline()
    {
        // A click (not on the expander) or Enter goes to the item; arrow keys only move the highlight.
        OutlineTree.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || (e.Source as Avalonia.Visual)?.FindAncestorOfType<Avalonia.Controls.Primitives.ToggleButton>(true) is not null) return;
            if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<TreeViewItem>(true) is { DataContext: OutlineNodeViewModel node }) FollowOutline(node.Item);
        }, RoutingStrategies.Bubble, handledEventsToo: true); // once per click (Tunnel | Bubble would run it twice)
        OutlineTree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && OutlineTree.SelectedItem is OutlineNodeViewModel node) { FollowOutline(node.Item); e.Handled = true; }
        };
        OutlineSplitter.DragCompleted += (_, _) => viewModel.Outline.RememberHeight(PagePanelBody.RowDefinitions[0].ActualHeight);
        // The splitter's row is the outline's height while it shows (a grid row cannot be bound).
        OutlineTree.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
                PagePanelBody.RowDefinitions[0].Height = OutlineTree.IsVisible ? new GridLength(viewModel.Outline.Height) : new GridLength(0);
        };
    }

    /// <summary>Shows or hides the outline and remembers it (app setting).</summary>
    public void SetOutlineVisible(bool visible) => viewModel.Outline.SetVisible(visible);

    /// <summary>A click on an item goes to its destination (remembered for Alt+←, like a link).</summary>
    public void FollowOutline(PdfOutlineItem item) => viewModel.Outline.Follow(item);

    // ----- search (bound to SearchViewModel, R4e) -----

    public bool IsSearchOpen => viewModel.Search.IsOpen;
    public TextBox SearchField => SearchBox;

    private void InitializeSearch()
    {
        var search = viewModel.Search;
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.F3) { search.Enter(backwards: e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseSearch(); e.Handled = true; }
        };
        // The bar takes the keyboard when it opens and gives it back to the pages when it closes.
        SearchBar.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (SearchBar.IsVisible) { SearchBox.Focus(); SearchBox.SelectAll(); }
            else Canvas.Focus();
        };
    }

    /// <summary>Ctrl+F: the bar opens (or, already open, takes the keyboard again).</summary>
    public void OpenSearch()
    {
        viewModel.Search.OpenCommand.Execute();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void CloseSearch() => viewModel.Search.Close();

    // ----- the loading card (bound to LoadingViewModel) -----

    public bool LoadingShown => viewModel.Loading.IsShown;

    /// <summary>The card waits until the first pages are drawn: the render queue is idle once the canvas asked for them.</summary>
    private void WaitForFirstPages()
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


    /// <summary>다른 프로그램 주석 편집: converts and tells how many became editable. Returns that number.</summary>
    public Task<int> ConvertForeignAsync() => viewModel.Foreign.ConvertFromRibbonAsync();

    // ----- a new version (roadmap 1.1) -----

    /// <summary>The start-up check, run by <see cref="App"/> after the window is shown (test windows skip it).</summary>
    public Task CheckForUpdatesAtStartAsync() => viewModel.Update.CheckAtStartAsync();
}
