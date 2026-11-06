using System.Globalization;
using PdfNotes.Core.Geometry;
using PdfNotes.Editor.Services;

namespace PdfNotes.ViewModels;

/// <summary>
/// Ctrl+F search (spec 12; P1 V3): PDF text and text boxes, result count, previous/next. The results are temporary
/// marks the view draws on the overlay (never in the document or a saved PDF). The search runs in the background after
/// <see cref="Delay"/>; a new query or a document change cancels the run in progress and starts again, keeping the
/// current result when it still exists.
/// </summary>
public sealed class SearchViewModel(MainViewModel owner, IMainView view) : ObservableObject
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);

    private IReadOnlyList<SearchHit> hits = [];
    private Dictionary<Guid, List<int>> byPage = [];
    private int index = -1;
    private CancellationTokenSource? run;
    private IDisposable? timer;
    private bool keepNext;
    private bool isOpen, isSearching, matchCase;
    private string query = "";
    private string countText = "";

    public bool IsOpen { get => isOpen; private set => SetProperty(ref isOpen, value); }

    /// <summary>The search box text; a change searches again after <see cref="Delay"/>.</summary>
    public string Query
    {
        get => query;
        set { if (SetProperty(ref query, value ?? "")) Schedule(keep: false); }
    }

    /// <summary>Aa: capitals and small letters differ while on (off by default, not remembered).</summary>
    public bool MatchCase
    {
        get => matchCase;
        set { if (SetProperty(ref matchCase, value)) Schedule(keep: false); }
    }

    public IReadOnlyList<SearchHit> Hits => hits;

    /// <summary>The current result, or -1.</summary>
    public int Index => index;

    public SearchHit? CurrentHit => index >= 0 && index < hits.Count ? hits[index] : null;

    /// <summary>True while a search waits for its delay or runs.</summary>
    public bool IsSearching { get => isSearching; private set => SetProperty(ref isSearching, value); }

    /// <summary>`3/12`, `0/0` without results, empty without a query.</summary>
    public string CountText { get => countText; private set => SetProperty(ref countText, value); }

    /// <summary>The run in progress (done when nothing runs). Tests wait on it.</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Indexes into <see cref="Hits"/> of the results on a page, in order.</summary>
    public IReadOnlyList<int> HitsOn(Guid pageId) => byPage.TryGetValue(pageId, out var list) ? list : [];

    public void Open() => IsOpen = true;

    public void Close()
    {
        Cancel();
        IsOpen = false;
        Apply([], null);
    }

    public void Next() => Step(+1);
    public void Previous() => Step(-1);

    /// <summary>Ctrl+F or 보기 › 찾기: the text being edited is finished, then the bar opens (the view gives it the keyboard).</summary>
    public Command OpenCommand => field ??= new(() => { view.FinishTextEditing(); Open(); });

    public Command NextCommand => field ??= new(Next);
    public Command PreviousCommand => field ??= new(Previous);
    public Command CloseCommand => field ??= new(Close);

    /// <summary>Enter (Shift+Enter) in the search box: right after typing it searches at once, otherwise it steps.</summary>
    public void Enter(bool backwards)
    {
        if (IsSearching) SearchNow();
        else if (backwards) Previous();
        else Next();
    }

    private void Step(int step)
    {
        if (hits.Count == 0) return;
        GoTo(index < 0 ? (step > 0 ? 0 : hits.Count - 1) : (index + step + hits.Count) % hits.Count);
    }

    /// <summary>
    /// Restarts the search after <see cref="Delay"/>. <paramref name="keep"/>: the document changed, so the current
    /// result stays selected when it still exists and the view does not jump.
    /// </summary>
    public void Schedule(bool keep)
    {
        if (!IsOpen) return;
        run?.Cancel();
        keepNext = keep && (keepNext || index >= 0);
        IsSearching = true;
        timer?.Dispose();
        IDisposable? mine = null;
        mine = timer = owner.After(Delay, () =>
        {
            if (!ReferenceEquals(timer, mine)) return;
            timer = null;
            Running = StartAsync();
        });
    }

    /// <summary>Searches at once, without the delay.</summary>
    public void SearchNow()
    {
        timer?.Dispose();
        timer = null;
        Running = StartAsync();
    }

    /// <summary>Another document is shown: its results replace the old ones.</summary>
    public void DocumentReplaced()
    {
        run?.Cancel();
        Apply([], null);
        Schedule(keep: false);
    }

    /// <summary>Stops a waiting or running search (closing the bar or the window).</summary>
    public void Cancel()
    {
        run?.Cancel();
        timer?.Dispose();
        timer = null;
        IsSearching = false;
    }

    private async Task StartAsync()
    {
        run?.Cancel();
        var mine = new CancellationTokenSource();
        run = mine;
        bool keep = keepNext; keepNext = false;
        var previous = CurrentHit;
        string text = query;
        bool exact = matchCase;
        if (owner.Document is not { } doc || TextSearch.NormalizeQuery(text).Length == 0)
        {
            IsSearching = false;
            Apply([], null);
            return;
        }
        IsSearching = true;
        var model = doc.Session.Current;
        var glyphs = owner.Glyphs;
        try
        {
            var found = await Task.Run(() => DocumentSearch.Run(model, glyphs.Glyphs, text, mine.Token, exact), mine.Token);
            if (!ReferenceEquals(run, mine) || mine.IsCancellationRequested) return;
            IsSearching = false;
            Apply(found, keep ? previous : null);
            if (!keep && hits.Count > 0)
            {
                // A new query starts at the page in view.
                int from = 0, current = view.Viewport.CurrentPageIndex;
                for (int i = 0; i < hits.Count; i++)
                    if (model.IndexOfPage(hits[i].PageId) >= current) { from = i; break; }
                GoTo(from);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { } // the document was closed while its text was being read
    }

    /// <summary>
    /// Shows <paramref name="found"/>. With <paramref name="keepNear"/> the current result stays the same text (same
    /// page and box, nearest start) or moves to the result that follows it; the view does not move.
    /// </summary>
    private void Apply(IReadOnlyList<SearchHit> found, SearchHit? keepNear)
    {
        hits = found;
        byPage = found.Select((h, i) => (h, i)).GroupBy(x => x.h.PageId).ToDictionary(g => g.Key, g => g.Select(x => x.i).ToList());
        index = -1;
        if (keepNear is { } old && found.Count > 0 && owner.Document is { } doc)
        {
            int best = -1;
            for (int i = 0; i < found.Count; i++)
                if (found[i].PageId == old.PageId && found[i].BoxId == old.BoxId && Math.Abs(found[i].Start - old.Start) <= Math.Max(4, old.End - old.Start) &&
                    (best < 0 || Math.Abs(found[i].Start - old.Start) < Math.Abs(found[best].Start - old.Start))) best = i;
            if (best < 0)
            {
                // The result is gone (edited text, deleted page): the next one in reading order.
                int oldPage = doc.Session.Current.IndexOfPage(old.PageId);
                best = 0;
                if (oldPage >= 0)
                    for (int i = 0; i < found.Count; i++)
                        if (doc.Session.Current.IndexOfPage(found[i].PageId) >= oldPage) { best = i; break; }
            }
            index = best;
        }
        Changed();
    }

    private void GoTo(int target)
    {
        if (owner.Document is not { } doc || target < 0 || target >= hits.Count) return;
        index = target;
        var hit = hits[target];
        int page = doc.Session.Current.IndexOfPage(hit.PageId);
        if (page >= 0 && !view.Viewport.IsInView(page, hit.Quads))
        {
            var points = hit.Quads.SelectMany(q => new[] { q.UpperLeft, q.LowerRight }).ToArray();
            view.Viewport.ScrollToPagePoint(page, new(points.Average(p => p.X), points.Average(p => p.Y)));
        }
        Changed();
    }

    private void Changed()
    {
        CountText = hits.Count == 0 ? (TextSearch.NormalizeQuery(query).Length == 0 ? "" : "0/0")
            : string.Create(CultureInfo.CurrentCulture, $"{(index < 0 ? 0 : index + 1)}/{hits.Count}");
        OnPropertyChanged(nameof(Hits));
        OnPropertyChanged(nameof(Index));
        OnPropertyChanged(nameof(CurrentHit));
        view.Viewport.RefreshOverlays();
    }
}
