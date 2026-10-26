using PdfNotes.Core.Geometry;

namespace PdfNotes.Tests.Core;

/// <summary>D04: freehand smoothing/simplification (spec 7.2) and stroke hit geometry.</summary>
public sealed class StrokeGeometryTests
{
    [Fact]
    public void SmoothingKeepsEndPointsAndSharpCornersAndReducesJitter()
    {
        // A jittery horizontal line, then a sharp 90° turn downwards.
        var raw = new List<Point2>();
        for (int i = 0; i <= 20; i++) raw.Add(new(i * 2, i % 2 == 0 ? 0 : 0.4));
        var corner = raw[^1];
        for (int i = 1; i <= 10; i++) raw.Add(new(corner.X, corner.Y + i * 2));

        var smooth = StrokeGeometry.Smooth(raw);
        Assert.Equal(raw.Count, smooth.Length);
        Assert.Equal(raw[0], smooth[0]);
        Assert.Equal(raw[^1], smooth[^1]);
        Assert.Equal(corner, smooth[20]); // the user's turn is not rounded away
        double rawJitter = raw.Take(20).Skip(1).Max(p => Math.Abs(p.Y - 0.2));
        double smoothJitter = smooth.Take(19).Skip(2).Max(p => Math.Abs(p.Y - 0.2));
        Assert.True(smoothJitter < rawJitter / 3, $"{smoothJitter} vs {rawJitter}");
        Assert.All(smooth.Zip(raw), p => Assert.True(p.First.DistanceTo(p.Second) <= 0.5));
    }

    [Fact]
    public void SimplificationStaysWithinToleranceAndKeepsEnds()
    {
        var arc = Enumerable.Range(0, 200).Select(i => new Point2(100 * Math.Cos(i * Math.PI / 400), 100 * Math.Sin(i * Math.PI / 400))).ToList();
        const double tolerance = 0.05;
        var simple = StrokeGeometry.Simplify(arc, tolerance);
        Assert.True(simple.Length < arc.Count / 3, $"{simple.Length} points kept");
        Assert.Equal(arc[0], simple[0]);
        Assert.Equal(arc[^1], simple[^1]);
        Assert.All(arc, p => Assert.True(StrokeGeometry.DistanceToPolyline(simple, p) <= tolerance + 1e-9));
    }

    [Fact]
    public void AClickBecomesASinglePointDot()
    {
        Assert.Single(StrokeGeometry.Finish([new(5, 5)], 0.05));
        Assert.Single(StrokeGeometry.Finish([new(5, 5), new(5, 5)], 0.05));
        Assert.Equal(2, StrokeGeometry.Finish([new(5, 5), new(5.2, 5)], 0.05).Length);
    }

    [Fact]
    public void BoundsAndDistanceIncludeTheStrokeWidth()
    {
        Point2[] points = [new(10, 10), new(30, 10)];
        var b = StrokeGeometry.Bounds(points, 4);
        Assert.Equal(new Rect2(8, 8, 24, 4), b);
        Assert.Equal(3, StrokeGeometry.DistanceToPolyline(points, new(20, 13)), 12);
        Assert.Equal(5, StrokeGeometry.DistanceToPolyline(points, new(35, 10)), 12);
        Assert.Equal(5, StrokeGeometry.DistanceToPolyline([new(0, 0)], new(3, 4)), 12);
    }
}
