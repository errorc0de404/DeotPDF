using PdfNotes.Core.Geometry;

namespace PdfNotes.Editor.Input;

/// <summary>What the view should do with pen or touch input (spec 7.6). The view carries these out; it decides nothing.</summary>
public abstract record PointerAction
{
    /// <summary>Press the tool like a left mouse button. <see cref="Erase"/>: use the eraser for this stroke instead.</summary>
    public sealed record ToolDown(CanvasPointer Pointer, bool Erase) : PointerAction;

    public sealed record ToolMove(CanvasPointer Pointer) : PointerAction;

    public sealed record ToolUp(CanvasPointer Pointer) : PointerAction;

    /// <summary>
    /// Drop the stroke being drawn without changing the document (a second finger, a palm, a lost contact). With
    /// <paramref name="KeepCapture"/> the contact stays the canvas's until it lifts: a pen's press opened a menu under it.
    /// </summary>
    public sealed record ToolCancel(bool KeepCapture = false) : PointerAction;

    /// <summary>Move the content by <see cref="Delta"/> DIPs (the content follows the finger).</summary>
    public sealed record Pan(Point2 Delta) : PointerAction;

    /// <summary>Zoom by <see cref="Factor"/> keeping the viewport point <see cref="Center"/> in place.</summary>
    public sealed record Zoom(double Factor, Point2 Center) : PointerAction;

    /// <summary>A finger tap: a click for tools that take one (<see cref="TouchRules.TapReachesTool"/>). ClickCount 2 on a double tap.</summary>
    public sealed record Tap(CanvasPointer Pointer) : PointerAction;

    /// <summary>A finger let go of the page while moving it: the page goes on at <see cref="Velocity"/> (DIP/s, the
    /// direction the content moves) and slows down by itself.</summary>
    public sealed record Fling(Point2 Velocity) : PointerAction;

    /// <summary>A finger (or the pen with a selection tool) held still on the page: the right-click menu at <see cref="Pointer"/> (Android has no right button).</summary>
    public sealed record Hold(CanvasPointer Pointer) : PointerAction;
}

/// <summary>Settings of <see cref="PointerRouter"/> (spec 7.6, app settings).</summary>
public sealed record PointerRouterOptions
{
    /// <summary>One finger draws like the mouse instead of moving the page (setting, default off).</summary>
    public bool FingerDrawing { get; init; }

    /// <summary>A finger moved farther than this (DIP) from where it touched is a drag, not a tap.</summary>
    public double TapSlopDip { get; init; } = 10;

    /// <summary>A finger held longer than this is not a tap.</summary>
    public TimeSpan TapTime { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>A second tap this soon and this close to the first is a double tap.</summary>
    public TimeSpan DoubleTapTime { get; init; } = TimeSpan.FromMilliseconds(500);
    public double DoubleTapSlopDip { get; init; } = 20;

    /// <summary>Palm rejection: touches while the pen is near and this long after it left are ignored.</summary>
    public TimeSpan PalmAfterPen { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Palm rejection: a contact this large (longer side, DIP) is a palm.</summary>
    public double PalmContactDip { get; init; } = 30;

    /// <summary>A finger held still this long is a long press (the context menu); a tap must be shorter.</summary>
    public TimeSpan HoldTime { get; init; } = TimeSpan.FromMilliseconds(550);

    /// <summary>A finger that lets go of the page faster than this (DIP/s) flings it.</summary>
    public double FlingMinDipPerSecond { get; init; } = 250;
}

/// <summary>
/// Pen and touch decisions (spec 7.6), the same on every platform: the pen draws (its eraser end or barrel button erases),
/// one finger moves the page and taps, two fingers zoom and move, a palm is ignored. The platform turns its events into
/// <see cref="CanvasPointer"/>s with <see cref="CanvasPointer.PointerId"/> and carries out the returned actions. Mouse
/// input does not come here.
/// </summary>
public sealed class PointerRouter(Func<PointerRouterOptions> options, TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    private enum TouchState { None, Pending, Drawing, Panning, Pinching, Finished }

    private sealed class Contact(CanvasPointer down, long ticks)
    {
        public CanvasPointer Down { get; } = down;
        public long DownTicks { get; } = ticks;
        public Point2 Last { get; set; } = down.Position;
    }

    private readonly Dictionary<int, Contact> touches = [];
    private readonly HashSet<int> ignored = [];
    private TouchState state;
    private int? penId;
    private CanvasPointer? lastPen;
    private long penDownTicks;
    private CanvasPointer? penPress;
    private bool penMoved;
    private bool penHeld;
    private bool penNear;
    private long penLeftTicks = long.MinValue;
    private (long Ticks, Point2 At)? lastTap;
    private double pinchDistance;
    private Point2 pinchCenter;
    private bool grabbing;
    private readonly Queue<(long Ticks, Point2 At)> panSamples = new();

    /// <summary>
    /// A finger that touches what the tool can take (a selected or pickable annotation, roadmap P5d) moves it instead of
    /// the page once it drags; a tap there stays a tap. Null: a finger always moves the page.
    /// </summary>
    public Func<CanvasPointer, bool>? FingerGrabs { get; set; }

    /// <summary>
    /// The pen held still here for <see cref="PointerRouterOptions.HoldTime"/> opens the right-click menu instead of
    /// pressing the tool (2026-11-08: the selection tools, so a pen alone reaches the menu). Null: the pen never holds.
    /// </summary>
    public Func<CanvasPointer, bool>? PenHolds { get; set; }

    /// <summary>
    /// A finger held still here presses the tool with a double click (a PDF word) and then drags it, instead of the menu
    /// (2026-11-08: text selection by touch). Null: a long press is always the menu.
    /// </summary>
    public Func<CanvasPointer, bool>? HoldSelects { get; set; }

    /// <summary>True while a pen or finger owns the tool (a stroke is in progress).</summary>
    public bool IsDrawing => penId is not null || state == TouchState.Drawing;

    /// <summary>The pen is close to the screen (hovering) or touching it.</summary>
    public bool PenNear => penNear || penId is not null;

    /// <summary>The pen came within range (or left it). Touches are ignored while it is near (palm rejection).</summary>
    public IReadOnlyList<PointerAction> PenInRange(bool inRange)
    {
        if (penNear && !inRange) penLeftTicks = clock.GetTimestamp();
        penNear = inRange;
        if (inRange) return DropTouches();
        // A pen that leaves while still down lost its lift report: the stroke ends where it was last seen.
        if (penId is not null && lastPen is { } last)
        {
            penId = null;
            if (penHeld) { penHeld = false; return []; }
            return [new PointerAction.ToolUp(last)];
        }
        return [];
    }

    public IReadOnlyList<PointerAction> Down(CanvasPointer pointer)
    {
        switch (pointer.Device)
        {
            case PointerDevice.Pen:
            {
                if (penId is not null) return [];
                // The pen wins: a finger stroke or gesture in progress is dropped (the palm usually touched first).
                var actions = DropTouches().ToList();
                penId = pointer.PointerId;
                lastPen = pointer;
                penNear = true;
                penDownTicks = clock.GetTimestamp();
                penMoved = penHeld = false;
                penPress = pointer;
                actions.Add(new PointerAction.ToolDown(pointer, pointer.Eraser));
                return actions;
            }
            case PointerDevice.Touch:
                return TouchDown(pointer);
            default:
                return [];
        }
    }

    public IReadOnlyList<PointerAction> Move(CanvasPointer pointer)
    {
        if (pointer.Device == PointerDevice.Pen)
        {
            if (penId != pointer.PointerId || penHeld) return [];
            if (!penMoved && penPress is { } press && pointer.Position.DistanceTo(press.Position) > options().TapSlopDip) penMoved = true;
            lastPen = pointer;
            return [new PointerAction.ToolMove(pointer)];
        }
        if (pointer.Device != PointerDevice.Touch || ignored.Contains(pointer.PointerId) || !touches.TryGetValue(pointer.PointerId, out var contact)) return [];
        if (pointer.ContactSize >= options().PalmContactDip) return Ignore(pointer.PointerId);
        var previous = contact.Last;
        contact.Last = pointer.Position;
        switch (state)
        {
            case TouchState.Drawing:
                return [new PointerAction.ToolMove(pointer)];
            case TouchState.Pending:
                if (pointer.Position.DistanceTo(contact.Down.Position) <= options().TapSlopDip) return [];
                if (grabbing)
                {
                    state = TouchState.Drawing;
                    return [new PointerAction.ToolDown(contact.Down, false), new PointerAction.ToolMove(pointer)];
                }
                state = TouchState.Panning;
                panSamples.Clear();
                Sample(contact.Down.Position, contact.DownTicks);
                Sample(pointer.Position, clock.GetTimestamp());
                return [new PointerAction.Pan(Minus(pointer.Position, contact.Down.Position))];
            case TouchState.Panning:
                Sample(pointer.Position, clock.GetTimestamp());
                return [new PointerAction.Pan(Minus(pointer.Position, previous))];
            case TouchState.Pinching:
                return Pinch();
            default:
                return [];
        }
    }

    public IReadOnlyList<PointerAction> Up(CanvasPointer pointer)
    {
        if (pointer.Device == PointerDevice.Pen)
        {
            if (penId != pointer.PointerId) return [];
            penId = null;
            penLeftTicks = clock.GetTimestamp();
            if (penHeld) { penHeld = false; return []; } // the menu took this press
            return [new PointerAction.ToolUp(pointer)];
        }
        if (pointer.Device != PointerDevice.Touch) return [];
        if (ignored.Remove(pointer.PointerId) || !touches.Remove(pointer.PointerId, out var contact))
        {
            if (touches.Count == 0 && state != TouchState.None) state = TouchState.None;
            return [];
        }
        IReadOnlyList<PointerAction> result = [];
        switch (state)
        {
            case TouchState.Drawing:
                result = [new PointerAction.ToolUp(pointer)];
                state = TouchState.Finished;
                break;
            case TouchState.Pending:
                long now = clock.GetTimestamp();
                if (clock.GetElapsedTime(contact.DownTicks, now) <= options().TapTime) result = [Tap(pointer, now)];
                state = TouchState.Finished;
                break;
            case TouchState.Panning:
                Sample(pointer.Position, clock.GetTimestamp());
                if (FlingVelocity() is { } velocity) result = [new PointerAction.Fling(velocity)];
                state = TouchState.Finished;
                break;
            case TouchState.Pinching:
                // The other finger stays down but does not start moving the page: lifting one finger of a pinch
                // would otherwise make the page jump to it.
                state = TouchState.Finished;
                break;
        }
        if (touches.Count == 0) state = TouchState.None;
        return result;
    }

    /// <summary>
    /// The view's timer after a finger went down: a single finger still within the tap slop for <see cref="PointerRouterOptions.HoldTime"/>
    /// is a long press, and the touch is then finished (lifting it is not a tap, moving it does not move the page).
    /// </summary>
    public IReadOnlyList<PointerAction> HoldElapsed(int pointerId)
    {
        if (penId == pointerId) return PenHoldElapsed();
        if (state != TouchState.Pending || touches.Count != 1 || !touches.TryGetValue(pointerId, out var contact)) return [];
        if (clock.GetElapsedTime(contact.DownTicks) < options().HoldTime - TimeSpan.FromMilliseconds(30)) return []; // a timer may fire a little early
        lastTap = null;
        var at = contact.Down with { Position = contact.Last };
        if (HoldSelects?.Invoke(at) == true)
        {
            state = TouchState.Drawing;
            return [new PointerAction.ToolDown(at with { ClickCount = 2 }, false)];
        }
        state = TouchState.Finished;
        return [new PointerAction.Hold(at)];
    }

    /// <summary>
    /// Whether a long press of this contact can still come: the pen that is down and not yet held, or the only finger
    /// before it moved the page. The view (re)starts its hold timer only for such a contact, so a palm or a second
    /// finger landing meanwhile does not take the timer of the press in progress.
    /// </summary>
    public bool WaitsForHold(int pointerId) =>
        penId == pointerId ? !penHeld : state == TouchState.Pending && touches.Count == 1 && touches.ContainsKey(pointerId);

    /// <summary>How far back the finger's last moves count for its speed when it lets go.</summary>
    private static readonly TimeSpan FlingWindow = TimeSpan.FromMilliseconds(100);

    private void Sample(Point2 at, long ticks)
    {
        panSamples.Enqueue((ticks, at));
        while (panSamples.Count > 2 && clock.GetElapsedTime(panSamples.Peek().Ticks, ticks) > FlingWindow) panSamples.Dequeue();
    }

    private Point2? FlingVelocity()
    {
        if (panSamples.Count < 2) return null;
        var (firstTicks, first) = panSamples.Peek();
        var (lastTicks, last) = panSamples.Last();
        panSamples.Clear();
        double seconds = clock.GetElapsedTime(firstTicks, lastTicks).TotalSeconds;
        if (seconds <= 0) return null;
        var velocity = new Point2((last.X - first.X) / seconds, (last.Y - first.Y) / seconds);
        return Math.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y) >= options().FlingMinDipPerSecond ? velocity : null;
    }

    /// <summary>A contact was lost (capture taken away, device removed). Its stroke is dropped.</summary>
    public IReadOnlyList<PointerAction> Cancel(int pointerId)
    {
        if (penId == pointerId)
        {
            penId = null;
            penLeftTicks = clock.GetTimestamp();
            return [new PointerAction.ToolCancel()];
        }
        ignored.Remove(pointerId);
        if (!touches.Remove(pointerId)) return [];
        bool drawing = state == TouchState.Drawing;
        state = touches.Count == 0 ? TouchState.None : TouchState.Finished;
        return drawing ? [new PointerAction.ToolCancel()] : [];
    }

    /// <summary>Forgets every contact (the window lost focus, the document closed). A stroke in progress is dropped.</summary>
    public IReadOnlyList<PointerAction> Reset()
    {
        bool drawing = IsDrawing;
        touches.Clear();
        ignored.Clear();
        state = TouchState.None;
        penId = null;
        return drawing ? [new PointerAction.ToolCancel()] : [];
    }

    private IReadOnlyList<PointerAction> TouchDown(CanvasPointer pointer)
    {
        var o = options();
        long now = clock.GetTimestamp();
        bool palm = PenNear || (penLeftTicks != long.MinValue && clock.GetElapsedTime(penLeftTicks, now) < o.PalmAfterPen) ||
                    pointer.ContactSize >= o.PalmContactDip;
        if (palm || state == TouchState.Finished || touches.Count >= 2)
        {
            ignored.Add(pointer.PointerId);
            return [];
        }
        touches[pointer.PointerId] = new(pointer, now);
        if (touches.Count == 1)
        {
            if (o.FingerDrawing)
            {
                state = TouchState.Drawing;
                return [new PointerAction.ToolDown(pointer, false)];
            }
            state = TouchState.Pending;
            grabbing = FingerGrabs?.Invoke(pointer) == true;
            return [];
        }
        // A second finger: a pinch. A finger stroke that had just begun is dropped.
        bool drawing = state == TouchState.Drawing;
        state = TouchState.Pinching;
        (pinchCenter, pinchDistance) = PinchShape();
        return drawing ? [new PointerAction.ToolCancel()] : [];
    }

    /// <summary>The pen's press: the stroke is dropped and the menu opens where the pen is (lifting it then does nothing).</summary>
    private IReadOnlyList<PointerAction> PenHoldElapsed()
    {
        if (penHeld || penMoved || penPress is not { } down || down.Eraser || lastPen is not { } last || PenHolds?.Invoke(down) != true) return [];
        if (clock.GetElapsedTime(penDownTicks) < options().HoldTime - TimeSpan.FromMilliseconds(30)) return [];
        penHeld = true;
        return [new PointerAction.ToolCancel(KeepCapture: true), new PointerAction.Hold(last)];
    }

    private IReadOnlyList<PointerAction> Pinch()
    {
        var (center, distance) = PinchShape();
        var actions = new List<PointerAction>(2);
        if (center != pinchCenter) actions.Add(new PointerAction.Pan(Minus(center, pinchCenter)));
        // Fingers that come very close make the ratio meaningless; keep the zoom until they part again.
        if (distance > 1 && pinchDistance > 1 && Math.Abs(distance - pinchDistance) > 1e-9)
            actions.Add(new PointerAction.Zoom(distance / pinchDistance, center));
        pinchCenter = center;
        if (distance > 1) pinchDistance = distance;
        return actions;
    }

    private (Point2 Center, double Distance) PinchShape()
    {
        var points = touches.Values.Select(c => c.Last).Take(2).ToArray();
        return (new((points[0].X + points[1].X) / 2, (points[0].Y + points[1].Y) / 2), points[0].DistanceTo(points[1]));
    }

    private PointerAction Tap(CanvasPointer pointer, long now)
    {
        var o = options();
        bool second = lastTap is { } last && clock.GetElapsedTime(last.Ticks, now) <= o.DoubleTapTime &&
                      last.At.DistanceTo(pointer.Position) <= o.DoubleTapSlopDip;
        lastTap = second ? null : (now, pointer.Position);
        return new PointerAction.Tap(pointer with { ClickCount = second ? 2 : 1 });
    }

    private IReadOnlyList<PointerAction> Ignore(int pointerId)
    {
        // A contact that grows into a palm is dropped with whatever it was doing.
        touches.Remove(pointerId);
        ignored.Add(pointerId);
        bool drawing = state == TouchState.Drawing;
        state = touches.Count == 0 ? TouchState.None : TouchState.Finished;
        return drawing ? [new PointerAction.ToolCancel()] : [];
    }

    private IReadOnlyList<PointerAction> DropTouches()
    {
        if (touches.Count == 0) return [];
        bool drawing = state == TouchState.Drawing;
        foreach (var id in touches.Keys) ignored.Add(id);
        touches.Clear();
        state = TouchState.Finished;
        return drawing ? [new PointerAction.ToolCancel()] : [];
    }

    private static Point2 Minus(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
}
