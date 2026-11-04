using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.App.Themes;

namespace PdfNotes.App;

/// <summary>
/// Ctrl+F search bar (spec 12): the query, the results and the current one are <see cref="SearchViewModel"/>; the
/// window shows the bar and draws the temporary result marks on the overlay.
/// </summary>
public partial class MainWindow
{
    public static int SearchDelayMs => (int)SearchViewModel.Delay.TotalMilliseconds;

    public IReadOnlyList<SearchHit> SearchHits => viewModel.Search.Hits;
    public int SearchIndex => viewModel.Search.Index;
    public SearchHit? CurrentSearchHit => viewModel.Search.CurrentHit;
    public bool IsSearchOpen => viewModel.Search.IsOpen;

    /// <summary>True while a search waits for its delay or runs (for the validation harness).</summary>
    public bool IsSearching => viewModel.Search.IsSearching;

    IDocumentViewport IMainView.Viewport => View;

    private void InitializeSearch() => viewModel.Search.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(SearchViewModel.IsOpen)) SearchBar.Visibility = viewModel.Search.IsOpen ? Visibility.Visible : Visibility.Collapsed;
    };

    private void FindExecuted(object sender, ExecutedRoutedEventArgs e) => OpenSearch();

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
        View.Focus();
    }

    public void SearchNext() => viewModel.Search.Next();
    public void SearchPrevious() => viewModel.Search.Previous();

    private void SearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.F3)
        {
            viewModel.Search.Enter(backwards: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
    }

    private void SearchPreviousClick(object sender, RoutedEventArgs e) => SearchPrevious();
    private void SearchNextClick(object sender, RoutedEventArgs e) => SearchNext();
    private void SearchCloseClick(object sender, RoutedEventArgs e) => CloseSearch();

    private bool IsPartlyInView(int page, IEnumerable<Quad> quads) => View.IsPartlyInView(page, quads);

    private static readonly Brush SearchFill = Theme.Brush("Overlay.Search");
    private static readonly Brush SearchCurrentFill = Theme.Brush("Overlay.SearchCurrent");
    private static readonly Pen SearchCurrentPen = Frozen(new Pen(Theme.Brush("Overlay.SearchCurrentEdge"), 1.5));

    /// <summary>Temporary result marks of one page, drawn on the overlay inside the page clip (partly visible results are cut at the paper edge).</summary>
    private void PaintSearch(DrawingContext context, Page page, ViewTransform transform)
    {
        var search = viewModel.Search;
        foreach (int i in search.HitsOn(page.Id))
        {
            var geometry = new StreamGeometry();
            using (var c = geometry.Open())
                foreach (var quad in search.Hits[i].Quads)
                {
                    var ring = TextSelection.Ring(quad).Select(transform.PageToDip.Transform).ToArray();
                    c.BeginFigure(new Point(ring[0].X, ring[0].Y), true, true);
                    c.PolyLineTo(ring.Skip(1).Select(p => new Point(p.X, p.Y)).ToArray(), true, true);
                }
            geometry.Freeze();
            bool current = i == search.Index;
            context.DrawGeometry(current ? SearchCurrentFill : SearchFill, current ? SearchCurrentPen : null, geometry);
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
