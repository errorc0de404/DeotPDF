using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PdfNotes.App.Canvas;
using PdfNotes.App.Services;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.App.Themes;

namespace PdfNotes.App;

/// <summary>
/// Ctrl+F search bar (spec 12): PDF text and text boxes, result count, previous/next, temporary highlight on the
/// overlay (never in the document or a saved PDF). The search runs in the background; a new query or a document
/// change cancels the run in progress and starts again, keeping the current result when it still exists.
/// </summary>
public partial class MainWindow
{
    public const int SearchDelayMs = 150;
    private IReadOnlyList<SearchHit> searchHits = [];
    private Dictionary<Guid, List<int>> searchByPage = [];
    private int searchIndex = -1;
    private CancellationTokenSource? searchRun;
    private DispatcherTimer? searchTimer;
    private bool searchKeep;

    public IReadOnlyList<SearchHit> SearchHits => searchHits;
    public int SearchIndex => searchIndex;
    public SearchHit? CurrentSearchHit => searchIndex >= 0 && searchIndex < searchHits.Count ? searchHits[searchIndex] : null;
    public bool IsSearchOpen => SearchBar.Visibility == Visibility.Visible;

    /// <summary>True while a search waits for its delay or runs (for the validation harness).</summary>
    public bool IsSearching { get; private set; }

    private void FindExecuted(object sender, ExecutedRoutedEventArgs e) => OpenSearch();

    public void OpenSearch()
    {
        FinishTextEditing();
        SearchBar.Visibility = Visibility.Visible;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void CloseSearch()
    {
        searchRun?.Cancel();
        searchTimer?.Stop();
        IsSearching = false;
        SearchBar.Visibility = Visibility.Collapsed;
        ApplySearch([], null);
        View.Focus();
    }

    public void SearchNext() => StepSearch(+1);
    public void SearchPrevious() => StepSearch(-1);

    private void StepSearch(int step)
    {
        if (searchHits.Count == 0) return;
        GoToSearchHit(searchIndex < 0 ? (step > 0 ? 0 : searchHits.Count - 1) : (searchIndex + step + searchHits.Count) % searchHits.Count);
    }

    private void SearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ScheduleSearch(keep: false);

    private void SearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.F3)
        {
            if (IsSearching) { searchTimer?.Stop(); StartSearch(); } // Enter right after typing searches at once
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) SearchPrevious();
            else SearchNext();
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

    /// <summary>
    /// Restarts the search after <see cref="SearchDelayMs"/>. <paramref name="keep"/>: the document changed, so the
    /// current result stays selected when it still exists and the view does not jump.
    /// </summary>
    private void ScheduleSearch(bool keep)
    {
        if (!IsSearchOpen) return;
        searchRun?.Cancel();
        searchKeep = keep && (searchKeep || searchIndex >= 0);
        IsSearching = true;
        searchTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(SearchDelayMs), DispatcherPriority.Background, (_, _) => { searchTimer!.Stop(); StartSearch(); }, Dispatcher);
        searchTimer.Stop();
        searchTimer.Start();
    }

    private async void StartSearch()
    {
        searchRun?.Cancel();
        var run = new CancellationTokenSource();
        searchRun = run;
        bool keep = searchKeep; searchKeep = false;
        var previous = CurrentSearchHit;
        string query = SearchBox.Text;
        if (document is not { } doc || TextSearch.NormalizeQuery(query).Length == 0)
        {
            IsSearching = false;
            ApplySearch([], null);
            return;
        }
        IsSearching = true;
        var model = doc.Session.Current;
        var glyphs = GlyphSource;
        try
        {
            var hits = await Task.Run(() => DocumentSearch.Run(model, glyphs.Glyphs, query, run.Token), run.Token);
            if (!ReferenceEquals(searchRun, run) || run.IsCancellationRequested) return;
            IsSearching = false;
            ApplySearch(hits, keep ? previous : null);
            if (!keep && searchHits.Count > 0)
            {
                // A new query starts at the page in view.
                int from = 0;
                for (int i = 0; i < searchHits.Count; i++)
                    if (model.IndexOfPage(searchHits[i].PageId) >= View.CurrentPageIndex) { from = i; break; }
                GoToSearchHit(from);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { } // the document was closed while its text was being read
    }

    /// <summary>
    /// Shows <paramref name="hits"/>. With <paramref name="keepNear"/> the current result stays the same text (same page
    /// and box, nearest start) or moves to the result that follows it; the view does not move.
    /// </summary>
    private void ApplySearch(IReadOnlyList<SearchHit> hits, SearchHit? keepNear)
    {
        searchHits = hits;
        searchByPage = hits.Select((h, i) => (h, i)).GroupBy(x => x.h.PageId).ToDictionary(g => g.Key, g => g.Select(x => x.i).ToList());
        searchIndex = -1;
        if (keepNear is { } old && hits.Count > 0 && document is { } doc)
        {
            int best = -1;
            for (int i = 0; i < hits.Count; i++)
                if (hits[i].PageId == old.PageId && hits[i].BoxId == old.BoxId && Math.Abs(hits[i].Start - old.Start) <= Math.Max(4, old.End - old.Start) &&
                    (best < 0 || Math.Abs(hits[i].Start - old.Start) < Math.Abs(hits[best].Start - old.Start))) best = i;
            if (best < 0)
            {
                // The result is gone (edited text, deleted page): the next one in reading order.
                int oldPage = doc.Session.Current.IndexOfPage(old.PageId);
                best = 0;
                if (oldPage >= 0)
                    for (int i = 0; i < hits.Count; i++)
                        if (doc.Session.Current.IndexOfPage(hits[i].PageId) >= oldPage) { best = i; break; }
            }
            searchIndex = best;
        }
        UpdateSearchCount();
        View.RefreshOverlays();
    }

    private void GoToSearchHit(int index)
    {
        if (document is not { } doc || index < 0 || index >= searchHits.Count) return;
        searchIndex = index;
        var hit = searchHits[index];
        int page = doc.Session.Current.IndexOfPage(hit.PageId);
        if (page >= 0 && !IsInView(page, hit.Quads))
        {
            var points = hit.Quads.SelectMany(q => new[] { q.UpperLeft, q.LowerRight }).ToArray();
            View.ScrollToPagePoint(page, new(points.Average(p => p.X), points.Average(p => p.Y)));
        }
        UpdateSearchCount();
        View.RefreshOverlays();
    }

    private bool IsInView(int page, IEnumerable<Quad> quads)
    {
        if (View.Mode == ViewMode.SinglePage && page != View.CurrentPageIndex) return false;
        try
        {
            var m = View.PageTransform(page).PageToDip;
            var ring = quads.SelectMany(TextSelection.Ring).Select(m.Transform).ToArray();
            return ring.Min(p => p.X) >= 0 && ring.Min(p => p.Y) >= 0 && ring.Max(p => p.X) <= View.ViewportWidth && ring.Max(p => p.Y) <= View.ViewportHeight;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>Some part of <paramref name="quads"/> on page <paramref name="page"/> is in the viewport.</summary>
    private bool IsPartlyInView(int page, IEnumerable<Quad> quads)
    {
        if (View.Mode == ViewMode.SinglePage && page != View.CurrentPageIndex) return false;
        try
        {
            var m = View.PageTransform(page).PageToDip;
            var ring = quads.SelectMany(TextSelection.Ring).Select(m.Transform).ToArray();
            return ring.Max(p => p.X) >= 0 && ring.Max(p => p.Y) >= 0 && ring.Min(p => p.X) <= View.ViewportWidth && ring.Min(p => p.Y) <= View.ViewportHeight;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    private void UpdateSearchCount() =>
        SearchCount.Text = searchHits.Count == 0 ? (TextSearch.NormalizeQuery(SearchBox.Text).Length == 0 ? "" : "0/0")
            : string.Create(CultureInfo.CurrentCulture, $"{(searchIndex < 0 ? 0 : searchIndex + 1)}/{searchHits.Count}");

    private static readonly Brush SearchFill = Theme.Brush("Overlay.Search");
    private static readonly Brush SearchCurrentFill = Theme.Brush("Overlay.SearchCurrent");
    private static readonly Pen SearchCurrentPen = Frozen(new Pen(Theme.Brush("Overlay.SearchCurrentEdge"), 1.5));

    /// <summary>Temporary result marks of one page, drawn on the overlay inside the page clip (partly visible results are cut at the paper edge).</summary>
    private void PaintSearch(DrawingContext context, Page page, ViewTransform transform)
    {
        if (!searchByPage.TryGetValue(page.Id, out var indexes)) return;
        foreach (int i in indexes)
        {
            var geometry = new StreamGeometry();
            using (var c = geometry.Open())
                foreach (var quad in searchHits[i].Quads)
                {
                    var ring = TextSelection.Ring(quad).Select(transform.PageToDip.Transform).ToArray();
                    c.BeginFigure(new Point(ring[0].X, ring[0].Y), true, true);
                    c.PolyLineTo(ring.Skip(1).Select(p => new Point(p.X, p.Y)).ToArray(), true, true);
                }
            geometry.Freeze();
            bool current = i == searchIndex;
            context.DrawGeometry(current ? SearchCurrentFill : SearchFill, current ? SearchCurrentPen : null, geometry);
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
