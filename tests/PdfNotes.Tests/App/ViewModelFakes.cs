using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Layout;

namespace PdfNotes.Tests.App;

/// <summary>A clock that moves only when told; its timers fire on <see cref="Advance"/> (P1 view model tests).</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly List<Timer> timers = [];
    public DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, () => callback(state)) { Due = Now + dueTime };
        timers.Add(timer);
        return timer;
    }

    /// <summary>Timers waiting to fire.</summary>
    public int Pending => timers.Count;

    public void Advance(TimeSpan by)
    {
        Now += by;
        foreach (var t in timers.Where(t => t.Due <= Now).ToList()) { timers.Remove(t); t.Fire(); }
    }

    private sealed class Timer(ManualTime owner, Action fire) : ITimer
    {
        public DateTimeOffset Due;
        public void Fire() => fire();
        public bool Change(TimeSpan dueTime, TimeSpan period) { Due = owner.Now + dueTime; return true; }
        public void Dispose() => owner.timers.Remove(this);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>A canvas that records what it was asked; what is "in view" is decided by the test.</summary>
internal sealed class FakeViewport : IDocumentViewport
{
    public int CurrentPageIndex { get; set; }
    public int PageCount { get; set; } = 3;
    public double Zoom { get; set; } = 1;
    public ViewMode Mode { get; set; } = ViewMode.Continuous;
    public Point2 Center = new(100, 100);
    public PageAnchor? CenterAnchor => PageCount == 0 ? null : new PageAnchor(CurrentPageIndex, Center);
    public bool IsToolActive { get; set; }
    public Func<int, IEnumerable<Quad>, bool> InView = (page, _) => false;
    public readonly List<(int Page, Point2 Point)> Scrolls = [];
    public readonly List<string> Calls = [];
    public int Refreshes;

    public bool IsInView(int pageIndex, IEnumerable<Quad> quads) => InView(pageIndex, quads);
    public bool IsPartlyInView(int pageIndex, IEnumerable<Quad> quads) => InView(pageIndex, quads);
    public void ScrollToPagePoint(int pageIndex, Point2 point) { Scrolls.Add((pageIndex, point)); CurrentPageIndex = pageIndex; Center = point; }
    public void ShowPagePoint(int pageIndex, Point2 point) { Calls.Add($"show {pageIndex} {point.X},{point.Y}"); CurrentPageIndex = pageIndex; Center = point; }
    public void GoToPage(int pageIndex) { CurrentPageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, PageCount - 1)); Calls.Add($"page {CurrentPageIndex}"); }
    public void SetZoom(double zoom) { Zoom = zoom; Calls.Add($"zoom {zoom}"); }
    public void FitWidth() => Calls.Add("fitWidth");
    public void FitPage() => Calls.Add("fitPage");
    public void SetMode(ViewMode mode) { Mode = mode; Calls.Add($"mode {mode}"); }
    public void LineUp() => Calls.Add("lineUp");
    public void LineDown() => Calls.Add("lineDown");
    public bool ArrowSideways(int direction, bool repeat) { Calls.Add($"sideways {direction}"); return true; }
    public void RefreshOverlays() => Refreshes++;
}

/// <summary>
/// Thumbnails are strings naming the page (P1 V5); the list's selection is the rows' IsSelected (R4e), which
/// <see cref="Select"/> sets as clicks in the list would.
/// </summary>
internal sealed class FakePageList : IPageThumbnailSource
{
    public PagesViewModel? Pages;
    public readonly List<Guid> Drawn = [];

    public IReadOnlyCollection<Guid> Selected => [.. Pages!.Items.Where(t => t.IsSelected).Select(t => t.PageId)];
    public void Select(IReadOnlyCollection<Guid> ids) { foreach (var t in Pages!.Items) t.IsSelected = ids.Contains(t.PageId); }

    public Task<PageThumbnail?> RenderAsync(OpenDocument document, DocumentModel model, Page page, double width, bool notesHidden, CancellationToken cancel)
    {
        Drawn.Add(page.Id);
        return Task.FromResult<PageThumbnail?>(new($"{page.Id}:{page.Annotations.Count}:{notesHidden}", width, width * 2, Complete: true));
    }
}

/// <summary>A clipboard in memory.</summary>
internal sealed class FakeClipboard : IEditorClipboard
{
    public readonly Dictionary<string, string> Data = [];
    public bool ContainsData(string format) => Data.ContainsKey(format);
    public string? GetData(string format) => Data.GetValueOrDefault(format);
    public void SetData(string format, string data) { Data.Clear(); Data[format] = data; }
    public bool ContainsText() => false;
    public string GetText() => "";
    public void SetText(string text) { }
    public bool ContainsImage() => false;
    public byte[]? GetImagePng() => null;
}

/// <summary>A window whose pending input the test sets (P1 V6).</summary>
internal sealed class FakeWindow : IWindowCoordinator
{
    public bool InputPending { get; set; }
}

/// <summary>A screen that records what the commands asked of it (R4).</summary>
internal sealed class FakeAppShell : IAppShell
{
    public readonly List<bool> NotesHidden = [];
    public Task<string?> ChooseOpenPathAsync() => Task.FromResult<string?>(null);
    public NewPageSpec? ChooseNewNote() => null;
    public void ShowSettings() { }
    public void ShowRecentMenu() { }
    public void ShowZoomMenu() { }
    public void UndoOrRedo(bool redo) { }
    public void SetNotesHidden(bool hidden) => NotesHidden.Add(hidden);
    public void OpenSearch() { }
    public void OpenWebPage(string url) { }
    public Task InsertImageFromFileAsync() => Task.CompletedTask;
}
