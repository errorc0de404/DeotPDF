using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Tests.Core;

/// <summary>D08 lasso (spec 5.4–5.5): automatic closing, any painted area inside selects, even-odd, locks.</summary>
public sealed class LassoTests
{
    private static readonly RgbColor Red = new(220, 40, 40);

    private static InkStroke Ink(double x0, double y0, double x1, double y1, double width = 2, bool locked = false, Affine? transform = null) =>
        new(Guid.NewGuid(), locked, transform ?? Affine.Identity, [new(x0, y0), new(x1, y1)], width, Red, 1);

    private static Page PageOf(params Annotation[] annotations) => Page.Create(TestModels.A4()) with { Annotations = [.. annotations] };

    /// <summary>Fixed-size text bounds so the test does not depend on WPF layout.</summary>
    private static Rect2 TextBounds(TextBoxAnnotation t) => new(0, 0, t.WidthPt, 20);

    private static Point2[] Rect(double x0, double y0, double x1, double y1) => [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    [Fact]
    public void OpenPathIsClosedFromItsEndBackToItsStart()
    {
        var inside = Ink(40, 50, 60, 50);
        // Three sides of a square drawn left, bottom, right; the missing top is the closing segment.
        Point2[] open = [new(0, 0), new(0, 100), new(100, 100), new(100, 0)];
        Assert.Equal([inside.Id], Lasso.Select(PageOf(inside), open, TextBounds));
    }

    [Fact]
    public void AnyPaintedAreaInsideSelectsButNearMissesDoNot()
    {
        var inside = Ink(20, 50, 80, 50, width: 4);
        var centerlineInsideButWidthOut = Ink(20, 97, 80, 97, width: 8); // reaches y = 101
        var crossing = Ink(50, 20, 150, 20);
        var outside = Ink(200, 200, 220, 220);
        var nearMiss = Ink(20, 101.5, 80, 101.5, width: 2); // painted from y = 100.5: just outside
        var arrow = new LineAnnotation(Guid.NewGuid(), false, Affine.Identity, LineKind.Straight, new(20, 80), new(96, 80), [], 2,
            DashStyle.Solid, false, true, Red, 1); // the head is 4.5w = 9pt wide but its tip stays inside
        var arrowOut = arrow with { Id = Guid.NewGuid(), Start = new(20, 97), End = new(90, 97) }; // body ends at y 98, the head at y 101.5
        var widthOnly = Ink(20, 101, 80, 101, width: 4); // centerline outside, its width reaches y = 99
        var page = PageOf(inside, centerlineInsideButWidthOut, crossing, outside, arrow, arrowOut, nearMiss, widthOnly);
        var selected = Lasso.Select(page, Rect(0, 0, 100, 100), TextBounds);
        Assert.Equal([inside.Id, centerlineInsideButWidthOut.Id, crossing.Id, arrow.Id, arrowOut.Id, widthOnly.Id], selected);
    }

    [Fact]
    public void CurvesCountByTheirPaintedCurveNotTheirControlPoints()
    {
        // The control point (50, 0) is inside the second lasso, but the painted curve bends only to y = 24.
        var curve = new LineAnnotation(Guid.NewGuid(), false, Affine.Identity, LineKind.Quadratic, new(10, 50), new(90, 50), [new(50, 0)], 2,
            DashStyle.Dash, false, false, Red, 1);
        var page = PageOf(curve);
        Assert.Equal([curve.Id], Lasso.Select(page, Rect(0, 20, 100, 100), TextBounds));
        Assert.Empty(Lasso.Select(page, Rect(0, 0, 100, 23), TextBounds));
    }

    [Fact]
    public void SelfCrossingLassoUsesEvenOdd()
    {
        // Outer square, then an inner square in the same direction: the ring is odd (inside), the middle even (outside).
        Point2[] path = [.. Rect(0, 0, 200, 200), new(0, 0), .. Rect(50, 50, 150, 150), new(50, 50)];
        var ring = Ink(20, 90, 20, 110);
        var middle = Ink(90, 100, 110, 100);
        var page = PageOf(ring, middle);
        Assert.Equal([ring.Id], Lasso.Select(page, path, TextBounds));

        // The same loop drawn twice: every point is crossed twice, so nothing is inside.
        Point2[] twice = [.. Rect(0, 0, 200, 200), new(0, 0), .. Rect(0, 0, 200, 200)];
        Assert.Empty(Lasso.Select(page, twice, TextBounds));
    }

    [Fact]
    public void LockedAnnotationsAreNeverSelectedAndBoxesUseTheirTransformedRectangle()
    {
        var locked = Ink(40, 40, 60, 60, locked: true);
        var resource = TestModels.PngResource();
        var image = new ImageAnnotation(Guid.NewGuid(), false, Affine.Rigid(Math.PI / 4, 100, 20), resource.Id, new(0, 0, 50, 50));
        var text = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Translation(20, 150), "가나", [new TextRun(0, 2, "Malgun Gothic", 12, Red, false, false, false, false)],
            TextAlignment.Left, 60, TextBoxStyle.Default, []);
        var page = PageOf(locked, image, text);
        // The image rotated by 45° is a diamond (100,20)-(135.4,55.4)-(100,90.7)-(64.6,55.4).
        Assert.Equal([image.Id], Lasso.Select(page, Rect(0, 0, 140, 100), TextBounds));
        Assert.Equal([image.Id], Lasso.Select(page, Rect(110, 40, 140, 70), TextBounds));
        Assert.Empty(Lasso.Select(page, Rect(64, 20, 75, 30), TextBounds)); // inside its bounding box, outside the diamond
        Assert.Equal([text.Id], Lasso.Select(page, Rect(10, 140, 90, 180), TextBounds));
        Assert.Equal([text.Id], Lasso.Select(page, Rect(75, 140, 95, 180), TextBounds)); // the box rectangle, not just its glyphs
        Assert.Empty(Lasso.Select(page, Rect(85, 140, 95, 180), TextBounds));
    }

    [Fact]
    public void RotatedStrokesAndErasedPiecesAreJudgedByTheirRealShape()
    {
        // A 100pt stroke turned 45°: its axis-aligned bounds are large, its painted diagonal is thin.
        var diagonal = Ink(0, 0, 100, 0, width: 2, transform: Affine.Rigid(Math.PI / 4, 10, 10));
        Point2[] band = [new(10, 0), new(12, -2), new(102, 88), new(100, 90)]; // 7–10pt beside it, inside its bounding box
        Assert.Equal([diagonal.Id], Lasso.Select(PageOf(diagonal), [new(-2, 6), new(6, -2), new(94, 86), new(86, 94)], TextBounds));
        Assert.Empty(Lasso.Select(PageOf(diagonal), band, TextBounds));

        var model = DocumentModel.Create([TestModels.A4()]);
        var stroke = Ink(0, 50, 200, 50, width: 4);
        model = model with { Pages = [model.Pages[0] with { Annotations = [stroke] }] };
        var gesture = new EraseGesture(model, model.Pages[0].Id, EraserMode.Area, 20);
        gesture.AddPoint(new(100, 0)); gesture.AddPoint(new(100, 100));
        var session = new DocumentSession(model);
        session.Execute(gesture.Commit());
        var pieces = session.Current.Pages[0].Annotations;
        Assert.Equal(2, pieces.Count);
        // The left piece ends at x = 90, the right one starts at 110.
        var left = pieces.Single(a => AnnotationGeometry.Hit(a, new(10, 50), 0)).Id;
        Assert.Equal([left], Lasso.Select(session.Current.Pages[0], Rect(-10, 40, 95, 60), TextBounds));
        Assert.Equal(2, Lasso.Select(session.Current.Pages[0], Rect(85, 40, 115, 60), TextBounds).Count);
        Assert.Empty(Lasso.Select(session.Current.Pages[0], Rect(92, 40, 108, 60), TextBounds)); // only the erased gap
    }

    [Fact]
    public void TooFewPointsSelectNothing() =>
        Assert.Empty(Lasso.Select(PageOf(Ink(1, 1, 2, 2)), [new(0, 0), new(10, 10)], TextBounds));
}
