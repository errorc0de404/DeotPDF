using PdfNotes.App.Canvas;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Tests.App;

public class DocumentLayoutTests
{
    private static readonly PageFrame A4 = new(595.28, 841.89, 0);

    /// <summary>Mixed sizes and all rotations (canonical size + rotation).</summary>
    private static readonly PageFrame[] Mixed =
    [
        new(595.28, 841.89, 0), new(554, 712, 90), new(419.53, 595.28, 180), new(770, 560, 270), new(750, 1080, 0)
    ];

    private static PageFrame[] Fifty() => Enumerable.Repeat(A4, 50).ToArray();

    [Fact]
    public void ContinuousPagesAreStackedCenteredWithGaps()
    {
        var layout = new DocumentLayout(Mixed, 1, ViewMode.Continuous, 0, 900, 700);
        double scale = Units.DipsPerPoint;
        double maxWidth = Mixed.Max(f => f.DisplayWidthPt) * scale;
        Assert.Equal(Math.Max(900, maxWidth + 2 * DocumentLayout.MarginDip), layout.ExtentWidth, 9);
        double y = DocumentLayout.MarginDip;
        for (int i = 0; i < Mixed.Length; i++)
        {
            var r = layout.PageRect(i);
            Assert.Equal(y, r.Y, 9);
            Assert.Equal(Mixed[i].DisplayWidthPt * scale, r.Width, 9);   // rotated pages swap width/height
            Assert.Equal(Mixed[i].DisplayHeightPt * scale, r.Height, 9);
            Assert.Equal(layout.ExtentWidth / 2, r.X + r.Width / 2, 9);
            y += r.Height + DocumentLayout.PageGapDip;
        }
        Assert.Equal(y - DocumentLayout.PageGapDip + DocumentLayout.MarginDip, layout.ExtentHeight, 9);
    }

    [Fact]
    public void OnlyPagesIntersectingTheViewportAreVisible()
    {
        var layout = new DocumentLayout(Fifty(), 1, ViewMode.Continuous, 0, 1000, 800);
        var page30 = layout.PageRect(29);
        var viewport = new Rect2(0, page30.Y + 10, 1000, 800);
        var visible = layout.VisiblePages(viewport);
        Assert.InRange(visible.Count, 1, 2);
        Assert.Equal(29, visible[0]);
        Assert.Equal(29, layout.CurrentPage(viewport));
        // Zoomed far out many pages fit, but still only those in view.
        var small = new DocumentLayout(Fifty(), 0.1, ViewMode.Continuous, 0, 1000, 800);
        Assert.Equal(Enumerable.Range(0, 7), small.VisiblePages(new(0, 0, 1000, 800)));
    }

    [Fact]
    public void SinglePageModeLaysOutOnlyTheCurrentPageCentered()
    {
        var layout = new DocumentLayout(Mixed, 0.5, ViewMode.SinglePage, 3, 1200, 900);
        Assert.False(layout.IsLaidOut(0));
        Assert.True(layout.IsLaidOut(3));
        var r = layout.PageRect(3);
        Assert.Equal((1200 - r.Width) / 2, r.X, 9);
        Assert.Equal((900 - r.Height) / 2, r.Y, 9);
        Assert.Equal([3], layout.VisiblePages(new(0, 0, 1200, 900)));
        Assert.Equal(3, layout.CurrentPage(new(0, 0, 1200, 900)));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.PageRect(0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ZoomAnchorKeepsThePagePointUnderTheCursor(int pageIndex)
    {
        foreach (var (from, to) in new[] { (1.0, 2.5), (2.5, 0.1), (0.37, 8.0) })
        {
            var before = new DocumentLayout(Mixed, from, ViewMode.Continuous, 0, 1000, 700);
            var rect = before.PageRect(pageIndex);
            var offset = before.ClampOffset(new(rect.X - 100, rect.Y - 120), 1000, 700);
            var cursor = new Point2(rect.X - offset.X + rect.Width * 0.3, rect.Y - offset.Y + Math.Min(rect.Height * 0.4, 500));
            var anchor = before.Anchor(cursor, offset);
            Assert.Equal(pageIndex, anchor.PageIndex);

            var after = new DocumentLayout(Mixed, to, ViewMode.Continuous, 0, 1000, 700);
            var newOffset = after.OffsetForAnchor(anchor, cursor);
            var again = after.Anchor(cursor, newOffset);
            Assert.Equal(pageIndex, again.PageIndex);
            Assert.Equal(anchor.PagePoint.X, again.PagePoint.X, 6);
            Assert.Equal(anchor.PagePoint.Y, again.PagePoint.Y, 6);
        }
    }

    [Fact]
    public void HitTestReturnsCanonicalPointOfRotatedPage()
    {
        var layout = new DocumentLayout(Mixed, 2, ViewMode.Continuous, 0, 1000, 700);
        var r = layout.PageRect(1); // 90°: canonical (0,0) is displayed at the top-right corner
        var offset = new Point2(0, r.Y - 50);
        var topRight = new Point2(r.X + r.Width - offset.X - 1e-9, r.Y - offset.Y + 1e-9);
        var hit = layout.HitTest(topRight, offset);
        Assert.NotNull(hit);
        Assert.Equal(1, hit.Value.PageIndex);
        Assert.Equal(0, hit.Value.PagePoint.X, 6);
        Assert.Equal(0, hit.Value.PagePoint.Y, 6);
        Assert.Null(layout.HitTest(new(1, 1), offset)); // margin, outside every page
    }

    [Fact]
    public void FitZoomsUseDisplayedPageSize()
    {
        double width = DocumentLayout.FitWidthZoom(A4, 1000);
        Assert.Equal((1000 - 2 * DocumentLayout.MarginDip) / (595.28 * Units.DipsPerPoint), width, 9);
        var turned = new PageFrame(595.28, 841.89, 90);
        Assert.Equal((1000 - 2 * DocumentLayout.MarginDip) / (841.89 * Units.DipsPerPoint), DocumentLayout.FitWidthZoom(turned, 1000), 9);
        double page = DocumentLayout.FitPageZoom(A4, 1000, 600);
        Assert.Equal((600 - 2 * DocumentLayout.MarginDip) / (841.89 * Units.DipsPerPoint), page, 9);
        Assert.Equal(ZoomLimits.Minimum, DocumentLayout.FitWidthZoom(A4, 10));
        Assert.Equal(ZoomLimits.Maximum, DocumentLayout.FitWidthZoom(new PageFrame(20, 20, 0), 5000));
    }
}

public class RenderPlanningTests
{
    private static Page[] Pages(int count) =>
        Enumerable.Range(0, count).Select(_ => Page.Create(NewPageSpec.Default.ToPageSetup())).ToArray();

    [Fact]
    public void PlanRequestsVisiblePagesThenNeighboursOnly()
    {
        var pages = Pages(50);
        var layout = new DocumentLayout(pages.Select(p => p.Setup.Frame).ToArray(), 1, ViewMode.Continuous, 0, 1000, 800);
        var offset = new Point2(0, layout.PageRect(29).Y + 10);
        var plan = RenderPlanner.Plan(layout, pages, offset, 1000, 800, 1.25);
        var visible = layout.VisiblePages(new(offset.X, offset.Y, 1000, 800));
        Assert.Equal(visible.Count + 2, plan.Count);
        Assert.Equal(visible.Select(i => pages[i].Id), plan.Take(visible.Count).Select(k => k.PageId));
        Assert.Contains(plan, k => k.PageId == pages[visible[^1] + 1].Id);
        Assert.Contains(plan, k => k.PageId == pages[visible[0] - 1].Id);
        Assert.All(plan, k => Assert.Null(k.Region));
        Assert.All(plan, k => Assert.Equal(RenderPlanner.TargetPixelsPerPoint(1, 1.25), k.PixelsPerPoint, 12));
    }

    [Fact]
    public void HighZoomCapsTheWholePageAndAddsAGridAlignedViewportTile()
    {
        var pages = Pages(3);
        var layout = new DocumentLayout(pages.Select(p => p.Setup.Frame).ToArray(), 8, ViewMode.Continuous, 0, 1600, 1000);
        var rect = layout.PageRect(1);
        var offset = new Point2(rect.X + 2000, rect.Y + 3000);
        var plan = RenderPlanner.Plan(layout, pages, offset, 1600, 1000, 2);
        var baseKey = plan.First(k => k.PageId == pages[1].Id && k.Region is null);
        var setup = pages[1].Setup;
        Assert.InRange(setup.WidthPt * setup.HeightPt * baseKey.PixelsPerPoint * baseKey.PixelsPerPoint, 0, RenderPlanner.MaxBasePixels * 1.0001);
        var detail = Assert.Single(plan, k => k.Region is not null);
        double target = RenderPlanner.TargetPixelsPerPoint(8, 2);
        Assert.Equal(target, detail.PixelsPerPoint, 12);
        var region = detail.Region!.Value;
        Assert.Equal(0, region.X % RenderPlanner.DetailGridPixels);
        Assert.Equal(0, region.Y % RenderPlanner.DetailGridPixels);
        // The tile covers the visible part of the page (in device pixels) and is bounded by the viewport size.
        Assert.True(region.X <= 2000 * 2 && region.X + region.Width >= (2000 + 1600) * 2);
        Assert.True(region.Y <= 3000 * 2 && region.Y + region.Height >= (3000 + 1000) * 2);
        Assert.InRange(region.Area, 1, (1600 * 2 + 1024L) * (1000 * 2 + 1024L));
    }

    [Fact]
    public void KeysOfAChangedPageSetupDoNotMatch()
    {
        var page = Pages(1)[0];
        var rotated = page with { Setup = page.Setup with { Rotation = 90 } };
        Assert.NotEqual(RenderPlanner.BaseKey(page, 1), RenderPlanner.BaseKey(rotated, 1));
        Assert.Equal(RenderPlanner.BaseKey(page, 1), RenderPlanner.BaseKey(page with { }, 1));
    }

    [Fact]
    public void LruCacheEvictsLeastRecentlyUsedWithinBudget()
    {
        var cache = new LruByteCache<string, int>(100);
        cache.Add("a", 1, 40);
        cache.Add("b", 2, 40);
        Assert.True(cache.TryGet("a", out _)); // a is now most recent
        cache.Add("c", 3, 40);                 // evicts b
        Assert.False(cache.Contains("b"));
        Assert.True(cache.Contains("a") && cache.Contains("c"));
        Assert.Equal(80, cache.Bytes);
        cache.Add("huge", 4, 500);             // larger than the budget: kept alone
        Assert.Equal(["huge"], cache.Keys);
        Assert.Equal(500, cache.Bytes);
        Assert.True(cache.Remove("huge"));
        Assert.Equal(0, cache.Bytes);
    }
}
