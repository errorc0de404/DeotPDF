using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdfNotes.Pdf.Rendering;

namespace PdfNotes.App.Canvas;

/// <summary>A finished render delivered on the UI thread. Bitmap is null when the page failed to render.</summary>
public sealed record RenderResult(RenderKey Key, BitmapSource? Bitmap, string? Error);

/// <summary>
/// Renders page bitmaps on one background thread (PDFium is serialized anyway) for one open document.
/// <see cref="Request"/> replaces the whole pending queue, so requests for pages that scrolled away are dropped
/// before they start. Finished bitmaps go into a byte-bounded LRU cache and are announced on the UI thread.
/// </summary>
public sealed class PageRenderScheduler : IDisposable
{
    /// <summary>Main-view cache budget. Spec 16.2 allows 256 MiB for page and thumbnail caches together.</summary>
    public const long DefaultCacheBytes = 192L * 1024 * 1024;

    private readonly PageRenderer renderer;
    private readonly Dispatcher dispatcher;
    private readonly object gate = new();
    private readonly LruByteCache<RenderKey, BitmapSource> cache;
    private readonly Dictionary<RenderKey, string> failures = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Thread worker;
    private List<RenderKey> pending = [];
    private RenderKey? running;
    private readonly HashSet<RenderKey> delivering = new();
    private CancellationTokenSource? runningCancellation;
    private bool disposed;

    public PageRenderScheduler(PageRenderer renderer, Dispatcher dispatcher, long cacheBytes = DefaultCacheBytes)
    {
        this.renderer = renderer;
        this.dispatcher = dispatcher;
        cache = new(cacheBytes);
        worker = new Thread(Run) { IsBackground = true, Name = "PdfNotes page renderer", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    /// <summary>Raised on the UI thread after a result is cached (or failed).</summary>
    public event Action<RenderResult>? Completed;

    /// <summary>Raised on the UI thread when the queue becomes empty and nothing is rendering.</summary>
    public event Action? Idle;

    public int RendersStarted { get; private set; }
    public int RendersCompleted { get; private set; }
    public int RequestsDropped { get; private set; }
    public long CacheBytes => cache.Bytes;
    public int CacheCount => cache.Count;
    public IEnumerable<RenderKey> CachedKeys => cache.Keys;

    public bool IsIdle { get { lock (gate) return pending.Count == 0 && running is null && delivering.Count == 0; } }

    public bool TryGet(RenderKey key, out BitmapSource bitmap) => cache.TryGet(key, out bitmap);

    public bool HasFailed(RenderKey key, out string error) => failures.TryGetValue(key, out error!);

    /// <summary>
    /// Best cached whole-page bitmap for this page and setup: the closest resolution, preferring sharper ones.
    /// Used as a temporary picture while the exact resolution renders.
    /// </summary>
    public (RenderKey Key, BitmapSource Bitmap)? BestBase(RenderKey wanted)
    {
        RenderKey? best = null;
        double bestScore = double.PositiveInfinity;
        foreach (var key in cache.Keys)
        {
            if (key.IsDetail || key.PageId != wanted.PageId || !key.Setup.Equals(wanted.Setup)) continue;
            double ratio = key.PixelsPerPoint / wanted.PixelsPerPoint;
            double score = ratio >= 1 ? ratio - 1 : (1 / ratio - 1) * 2; // blurrier is worse than sharper
            if (score < bestScore) (best, bestScore) = (key, score);
        }
        return best is not null && cache.TryGet(best, out var bitmap) ? (best, bitmap) : null;
    }

    /// <summary>Replaces the queue with <paramref name="wanted"/> (highest priority first). Call on the UI thread.</summary>
    public void Request(IReadOnlyList<RenderKey> wanted)
    {
        var fresh = wanted.Distinct().Where(k => !cache.Contains(k) && !failures.ContainsKey(k)).ToList();
        lock (gate)
        {
            fresh.RemoveAll(delivering.Contains);
            ObjectDisposedException.ThrowIf(disposed, this);
            RequestsDropped += pending.Count(k => !fresh.Contains(k));
            if (running is not null && !fresh.Contains(running)) runningCancellation?.Cancel();
            fresh.Remove(running!);
            pending = fresh;
        }
        signal.Release();
    }

    private void Run()
    {
        var token = shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            try { signal.Wait(token); }
            catch (OperationCanceledException) { return; }
            while (true)
            {
                RenderKey key;
                CancellationTokenSource cancellation;
                lock (gate)
                {
                    if (disposed) return;
                    if (pending.Count == 0) { running = null; break; }
                    key = pending[0];
                    pending.RemoveAt(0);
                    running = key;
                    cancellation = runningCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    RendersStarted++;
                }
                RenderResult? result = null;
                try
                {
                    var page = renderer.Render(key.Setup, key.PixelsPerPoint, key.Region, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    var bitmap = BitmapSource.Create(page.Width, page.Height, 96, 96, PixelFormats.Pbgra32, null, page.Bgra, page.Stride);
                    bitmap.Freeze();
                    result = new(key, bitmap, null);
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { return; }
                catch (Exception e) { result = new(key, null, e.Message); }
                finally
                {
                    lock (gate)
                    {
                        running = null;
                        runningCancellation = null;
                        if (result is not null) delivering.Add(key);
                    }
                    cancellation.Dispose();
                }
                if (result is not null) dispatcher.BeginInvoke(DispatcherPriority.Background, () => Deliver(result));
            }
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (!disposed && IsIdle) Idle?.Invoke(); });
        }
    }

    private void Deliver(RenderResult result)
    {
        if (disposed) return;
        lock (gate) delivering.Remove(result.Key);
        RendersCompleted++;
        if (result.Bitmap is { } bitmap)
            cache.Add(result.Key, bitmap, (long)bitmap.PixelWidth * bitmap.PixelHeight * 4);
        else
            failures[result.Key] = result.Error ?? "";
        Completed?.Invoke(result);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            pending.Clear();
            runningCancellation?.Cancel();
        }
        shutdown.Cancel();
        worker.Join(TimeSpan.FromSeconds(5));
        cache.Clear();
        failures.Clear();
        Completed = null;
        Idle = null;
    }
}
