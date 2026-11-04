using PdfNotes.Core.Geometry;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Tools;
using A = PdfNotes.Editor.Input.PointerAction;

namespace PdfNotes.Tests.Editor;

/// <summary>P3 T3: the pen and touch decisions of spec 7.6, without a window (roadmap P3: no device to try them on).</summary>
public sealed class PointerRouterTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public void Advance(int ms) => Ticks += TimeSpan.FromMilliseconds(ms).Ticks;
    }

    private readonly Clock clock = new();
    private PointerRouterOptions options = new();
    private readonly PointerRouter router;

    public PointerRouterTests() => router = new(() => options, clock);

    private static CanvasPointer Pen(double x, double y, int id = 1, double pressure = .5, bool eraser = false) =>
        new(new(x, y), InputModifiers.None, Device: PointerDevice.Pen, Pressure: pressure, PointerId: id, Eraser: eraser);

    private static CanvasPointer Finger(double x, double y, int id = 10, double size = 8) =>
        new(new(x, y), InputModifiers.None, Device: PointerDevice.Touch, PointerId: id, ContactSize: size);

    [Fact]
    public void ThePenDrawsWithTheTool()
    {
        var down = Assert.IsType<A.ToolDown>(Assert.Single(router.Down(Pen(10, 10))));
        Assert.False(down.Erase);
        Assert.Equal(.5, down.Pointer.Pressure);
        Assert.IsType<A.ToolMove>(Assert.Single(router.Move(Pen(20, 12))));
        Assert.True(router.IsDrawing);
        Assert.IsType<A.ToolUp>(Assert.Single(router.Up(Pen(30, 14))));
        Assert.False(router.IsDrawing);
    }

    [Fact]
    public void TheEraserEndOrBarrelButtonErasesForThatStrokeOnly()
    {
        Assert.True(Assert.IsType<A.ToolDown>(Assert.Single(router.Down(Pen(10, 10, eraser: true)))).Erase);
        router.Up(Pen(20, 10, eraser: true));
        Assert.False(Assert.IsType<A.ToolDown>(Assert.Single(router.Down(Pen(10, 10)))).Erase);
    }

    [Fact]
    public void AFingerDragMovesThePageAfterTheTapSlop()
    {
        Assert.Empty(router.Down(Finger(100, 100)));
        Assert.Empty(router.Move(Finger(105, 104))); // inside the 10 DIP slop: maybe a tap
        var first = Assert.IsType<A.Pan>(Assert.Single(router.Move(Finger(100, 130))));
        Assert.Equal(new Point2(0, 30), first.Delta); // the whole way from the touch, so nothing is lost
        var next = Assert.IsType<A.Pan>(Assert.Single(router.Move(Finger(90, 150))));
        Assert.Equal(new Point2(-10, 20), next.Delta);
        Assert.Empty(router.Up(Finger(90, 150))); // the end of a drag is not a tap
        Assert.False(router.IsDrawing);
    }

    [Fact]
    public void AShortStillTouchIsATapAndTwoAreADoubleTap()
    {
        router.Down(Finger(50, 50));
        clock.Advance(120);
        var tap = Assert.IsType<A.Tap>(Assert.Single(router.Up(Finger(52, 51))));
        Assert.Equal(1, tap.Pointer.ClickCount);
        clock.Advance(200);
        router.Down(Finger(55, 52, id: 11));
        clock.Advance(100);
        Assert.Equal(2, Assert.IsType<A.Tap>(Assert.Single(router.Up(Finger(55, 52, id: 11)))).Pointer.ClickCount);
        // A third tap starts over.
        clock.Advance(100);
        router.Down(Finger(55, 52, id: 12));
        Assert.Equal(1, Assert.IsType<A.Tap>(Assert.Single(router.Up(Finger(55, 52, id: 12)))).Pointer.ClickCount);
    }

    [Fact]
    public void TapsFarApartOrSlowAreNotADoubleTap()
    {
        router.Down(Finger(50, 50)); Assert.Equal(1, ((A.Tap)router.Up(Finger(50, 50))[0]).Pointer.ClickCount);
        router.Down(Finger(150, 50, 11)); Assert.Equal(1, ((A.Tap)router.Up(Finger(150, 50, 11))[0]).Pointer.ClickCount);
        clock.Advance(600);
        router.Down(Finger(150, 50, 12)); Assert.Equal(1, ((A.Tap)router.Up(Finger(150, 50, 12))[0]).Pointer.ClickCount);
    }

    [Fact]
    public void AHeldFingerIsNotATap()
    {
        router.Down(Finger(50, 50));
        clock.Advance(800);
        Assert.Empty(router.Up(Finger(50, 50)));
    }

    [Fact]
    public void TwoFingersZoomAroundTheirMiddleAndMoveWithIt()
    {
        router.Down(Finger(100, 100, 10));
        Assert.Empty(router.Down(Finger(200, 100, 11)));
        // Spread to twice the distance around the same middle, one finger after the other: ×2 in all, and the
        // middle comes back where it was.
        var actions = router.Move(Finger(50, 100, 10)).Concat(router.Move(Finger(250, 100, 11))).ToArray();
        Assert.Equal(2, actions.OfType<A.Zoom>().Aggregate(1.0, (f, z) => f * z.Factor), 9);
        Assert.Equal(new Point2(150, 100), actions.OfType<A.Zoom>().Last().Center);
        Assert.Equal(0, actions.OfType<A.Pan>().Sum(p => p.Delta.X), 9);
        // Both fingers move down 40: the page moves 40 with them and the zoom comes back to where it was.
        var both = router.Move(Finger(50, 140, 10)).Concat(router.Move(Finger(250, 140, 11))).ToArray();
        Assert.Equal(40, both.OfType<A.Pan>().Sum(p => p.Delta.Y), 9);
        Assert.Equal(1, both.OfType<A.Zoom>().Aggregate(1.0, (f, z) => f * z.Factor), 9);
        // Lifting one finger ends the gesture; the other does not start dragging the page.
        Assert.Empty(router.Up(Finger(50, 140, 10)));
        Assert.Empty(router.Move(Finger(250, 300, 11)));
        Assert.Empty(router.Up(Finger(250, 300, 11)));
        // The next touch is a fresh start.
        router.Down(Finger(10, 10, 12));
        Assert.IsType<A.Tap>(Assert.Single(router.Up(Finger(10, 10, 12))));
    }

    [Fact]
    public void AThirdFingerIsIgnored()
    {
        router.Down(Finger(100, 100, 10));
        router.Down(Finger(200, 100, 11));
        Assert.Empty(router.Down(Finger(300, 300, 12)));
        Assert.Empty(router.Move(Finger(310, 330, 12)));
        Assert.Empty(router.Up(Finger(310, 330, 12)));
    }

    [Fact]
    public void WithFingerDrawingOneFingerDrawsAndASecondCancelsTheStroke()
    {
        options = options with { FingerDrawing = true };
        Assert.IsType<A.ToolDown>(Assert.Single(router.Down(Finger(10, 10))));
        Assert.IsType<A.ToolMove>(Assert.Single(router.Move(Finger(12, 30))));
        Assert.IsType<A.ToolCancel>(Assert.Single(router.Down(Finger(100, 10, 11)))); // two fingers: a zoom, not a stroke
        Assert.False(router.IsDrawing);
        router.Move(Finger(15, 30));
        Assert.Contains(router.Move(Finger(150, 10, 11)), a => a is A.Zoom);
        router.Up(Finger(15, 30));
        router.Up(Finger(150, 10, 11));
        // A single finger stroke ends with ToolUp.
        router.Down(Finger(10, 10, 12));
        Assert.IsType<A.ToolUp>(Assert.Single(router.Up(Finger(20, 10, 12))));
    }

    [Fact]
    public void TouchesWhileThePenIsNearAreThePalm()
    {
        router.PenInRange(true);
        Assert.Empty(router.Down(Finger(300, 300)));
        Assert.Empty(router.Move(Finger(300, 360)));
        Assert.Empty(router.Up(Finger(300, 360)));
        Assert.True(router.PenNear);
    }

    [Fact]
    public void TouchesJustAfterThePenLeftAreThePalmButLaterOnesAreNot()
    {
        router.Down(Pen(10, 10));
        router.Up(Pen(20, 10));
        router.PenInRange(false);
        clock.Advance(300);
        Assert.Empty(router.Down(Finger(300, 300)));
        Assert.Empty(router.Up(Finger(300, 300)));
        clock.Advance(300);
        router.Down(Finger(300, 300, 11));
        Assert.IsType<A.Tap>(Assert.Single(router.Up(Finger(300, 300, 11))));
    }

    [Fact]
    public void ALargeContactIsThePalmEvenWhenItGrows()
    {
        Assert.Empty(router.Down(Finger(300, 300, size: 45)));
        Assert.Empty(router.Up(Finger(300, 300, size: 45)));
        options = options with { FingerDrawing = true };
        Assert.IsType<A.ToolDown>(Assert.Single(router.Down(Finger(10, 10, 11))));
        Assert.IsType<A.ToolCancel>(Assert.Single(router.Move(Finger(12, 14, 11, size: 40)))); // the finger was the edge of a hand
        Assert.Empty(router.Up(Finger(12, 14, 11)));
    }

    [Fact]
    public void ThePenTakesOverFromAFingerStroke()
    {
        options = options with { FingerDrawing = true };
        router.Down(Finger(10, 10));
        var actions = router.Down(Pen(50, 50));
        Assert.IsType<A.ToolCancel>(actions[0]);
        Assert.IsType<A.ToolDown>(actions[1]);
        Assert.Empty(router.Move(Finger(15, 15))); // the finger is ignored from now on
        Assert.IsType<A.ToolUp>(Assert.Single(router.Up(Pen(60, 60))));
        Assert.Empty(router.Up(Finger(15, 15)));
    }

    [Fact]
    public void APenLeavingRangeWhileDownEndsItsStrokeWhereItWasLastSeen()
    {
        router.PenInRange(true);
        router.Down(Pen(10, 10));
        router.Move(Pen(40, 12));
        var up = Assert.IsType<A.ToolUp>(Assert.Single(router.PenInRange(false)));
        Assert.Equal(new Point2(40, 12), up.Pointer.Position);
        Assert.False(router.IsDrawing);
        Assert.Empty(router.Up(Pen(40, 12))); // a late lift report changes nothing
    }

    [Fact]
    public void ASecondPenOrAMouseIsNotRouted()
    {
        router.Down(Pen(10, 10, id: 1));
        Assert.Empty(router.Down(Pen(20, 20, id: 2)));
        Assert.Empty(router.Move(Pen(25, 20, id: 2)));
        Assert.Empty(router.Down(new CanvasPointer(new(1, 1), InputModifiers.None)));
    }

    [Fact]
    public void LostContactsDropTheirStroke()
    {
        router.Down(Pen(10, 10));
        Assert.IsType<A.ToolCancel>(Assert.Single(router.Cancel(1)));
        Assert.False(router.IsDrawing);
        options = options with { FingerDrawing = true };
        router.PenInRange(false);
        clock.Advance(600); // past the palm time after the pen
        router.Down(Finger(10, 10));
        Assert.IsType<A.ToolCancel>(Assert.Single(router.Reset()));
        Assert.Empty(router.Up(Finger(10, 10)));
    }

    [Fact]
    public void TapsReachOnlyTheSelectionTextAndNoteTools()
    {
        Assert.True(TouchRules.TapReachesTool(new SelectTool(null!, () => null)));
        Assert.True(TouchRules.TapReachesTool(new TextTool(null!, (_, _, _) => { }, _ => false)));
        Assert.True(TouchRules.TapReachesTool(new NoteTool(null!, (_, _) => { }, _ => false)));
        Assert.False(TouchRules.TapReachesTool(new StrokeTool(null!, () => null, () => StrokeStyle.DefaultPen, PdfNotes.Core.Models.AnnotationKind.Ink)));
        Assert.False(TouchRules.TapReachesTool(null));
    }
}
