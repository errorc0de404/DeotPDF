using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Rendering;

namespace PdfNotes.Editor.Layout;

/// <summary>
/// Identifies one render result. The page setup is part of the key, so a result rendered before a page changed
/// (rotation, size, background) can never be applied to the changed page. Region null = whole page.
/// </summary>
public sealed record RenderKey(Guid PageId, PageSetup Setup, double PixelsPerPoint, PixelRect? Region)
{
    public bool IsDetail => Region is not null;
}

/// <summary>Decides which bitmaps the current view needs, most important first. Pure function of view state.</summary>
public static class RenderPlanner
{
    /// <summary>Whole-page bitmaps are capped at this many pixels (32 MiB BGRA); higher zoom uses viewport tiles.</summary>
    public const long MaxBasePixels = 8_000_000;

    /// <summary>Detail regions are aligned to this grid so small scrolls reuse the same tile request.</summary>
    public const int DetailGridPixels = 512;

    /// <summary>Device pixels per page point at a zoom and DPI scale.</summary>
    public static double TargetPixelsPerPoint(double zoom, double dpiScale) => zoom * Units.DipsPerPoint * dpiScale;

    /// <summary>Resolution of the whole-page bitmap: the target, but never more than <see cref="MaxBasePixels"/>.</summary>
    public static double BasePixelsPerPoint(PageSetup setup, double target)
    {
        double cap = Math.Sqrt(MaxBasePixels / (setup.WidthPt * setup.HeightPt));
        return Math.Min(target, cap);
    }

    public static RenderKey BaseKey(Page page, double target) =>
        new(page.Id, page.Setup, BasePixelsPerPoint(page.Setup, target), null);

    /// <summary>
    /// Ordered requests: visible pages (whole page), then detail tiles of visible pages when the zoom exceeds the
    /// whole-page cap, then adjacent pages for smooth scrolling. Nothing else is rendered.
    /// </summary>
    public static IReadOnlyList<RenderKey> Plan(DocumentLayout layout, IReadOnlyList<Page> pages, Point2 offset,
        double viewportWidth, double viewportHeight, double dpiScale)
    {
        double target = TargetPixelsPerPoint(layout.Zoom, dpiScale);
        var viewport = new Rect2(offset.X, offset.Y, viewportWidth, viewportHeight);
        var visible = layout.VisiblePages(viewport);
        var result = new List<RenderKey>();
        foreach (int i in visible) result.Add(BaseKey(pages[i], target));
        foreach (int i in visible)
            if (DetailKey(layout, pages[i], i, viewport, dpiScale) is { } detail) result.Add(detail);
        foreach (int i in Adjacent(layout, visible))
            result.Add(BaseKey(pages[i], target));
        return result;
    }

    /// <summary>Tile covering the visible part of a page at full target resolution, or null when the base suffices.</summary>
    public static RenderKey? DetailKey(DocumentLayout layout, Page page, int index, Rect2 viewport, double dpiScale)
    {
        double target = TargetPixelsPerPoint(layout.Zoom, dpiScale);
        if (target <= BasePixelsPerPoint(page.Setup, target) * 1.0001) return null;
        var rect = layout.PageRect(index);
        double left = Math.Max(viewport.X, rect.X) - rect.X, top = Math.Max(viewport.Y, rect.Y) - rect.Y;
        double right = Math.Min(viewport.X + viewport.Width, rect.X + rect.Width) - rect.X;
        double bottom = Math.Min(viewport.Y + viewport.Height, rect.Y + rect.Height) - rect.Y;
        if (right <= left || bottom <= top) return null;
        var (fullWidth, fullHeight) = PageRenderer.PixelSize(page.Setup, target);
        int x0 = Snap(left * dpiScale, false), y0 = Snap(top * dpiScale, false);
        int x1 = Math.Min(fullWidth, Snap(right * dpiScale, true)), y1 = Math.Min(fullHeight, Snap(bottom * dpiScale, true));
        if (x1 <= x0 || y1 <= y0) return null;
        return new(page.Id, page.Setup, target, new PixelRect(x0, y0, x1 - x0, y1 - y0));
    }

    private static int Snap(double pixels, bool up)
    {
        double cells = pixels / DetailGridPixels;
        return (int)(up ? Math.Ceiling(cells) : Math.Floor(cells)) * DetailGridPixels;
    }

    private static IEnumerable<int> Adjacent(DocumentLayout layout, IReadOnlyList<int> visible)
    {
        int first, last;
        if (layout.Mode == ViewMode.SinglePage) first = last = layout.SinglePageIndex;
        else if (visible.Count > 0) (first, last) = (visible[0], visible[^1]);
        else yield break;
        if (last + 1 < layout.PageCount) yield return last + 1;
        if (first - 1 >= 0) yield return first - 1;
    }
}

/// <summary>Least-recently-used cache bounded by total byte size. Not thread-safe; used on the UI thread.</summary>
public sealed class LruByteCache<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value, long Bytes)>> map = new();
    private readonly LinkedList<(TKey Key, TValue Value, long Bytes)> order = new();

    public LruByteCache(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }
    public long Bytes { get; private set; }
    public int Count => map.Count;
    public IEnumerable<TKey> Keys => order.Select(e => e.Key);

    public bool Contains(TKey key) => map.ContainsKey(key);

    public bool TryGet(TKey key, out TValue value)
    {
        if (map.TryGetValue(key, out var node))
        {
            order.Remove(node);
            order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>Adds or replaces, then evicts least-recently-used entries until within budget (the new one stays).</summary>
    public void Add(TKey key, TValue value, long bytes)
    {
        Remove(key);
        map[key] = order.AddFirst((key, value, bytes));
        Bytes += bytes;
        while (Bytes > MaxBytes && order.Count > 1)
        {
            var last = order.Last!;
            order.RemoveLast();
            map.Remove(last.Value.Key);
            Bytes -= last.Value.Bytes;
        }
    }

    public bool Remove(TKey key)
    {
        if (!map.Remove(key, out var node)) return false;
        order.Remove(node);
        Bytes -= node.Value.Bytes;
        return true;
    }

    public void Clear()
    {
        map.Clear();
        order.Clear();
        Bytes = 0;
    }
}
