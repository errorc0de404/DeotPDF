using Avalonia;
using Avalonia.Input;
using PdfNotes.Core.Geometry;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Tools;

namespace PdfNotes.Desktop.Canvas;

/// <summary>
/// Pointer input of the canvas (roadmap P4 A3), the WPF <c>DocumentView</c>'s rules: the left button drives the tool, the
/// middle button is the quick eraser while held (spec 7.5), another button cancels a stroke, Space + drag pans. Pen and
/// touch go through the P3 <see cref="PointerRouter"/> (spec 7.6) exactly as in the WPF app.
/// </summary>
public sealed partial class DocumentCanvas
{
    private IPointer? captured;           // the mouse while the tool or a pan holds it
    private bool toolCaptured, panning, spaceHeld;
    private Point panStart;
    private Point2 panStartOffset;
    private ICanvasTool? middleTool;
    private ICanvasTool? touchTool;        // a pen or finger operation (the pen eraser while it erases)
    private IPointer? touchPointer;
    private PointerRouter? router;

    /// <summary>The middle mouse button (spec 7.5): Reserved = the tool owns it; Tool = the temporary tool while held.</summary>
    public Func<(bool Reserved, ICanvasTool? Tool)>? MiddleButton { get; set; }

    /// <summary>The eraser of a pen's eraser end or barrel button (spec 7.6).</summary>
    public Func<ICanvasTool?>? PenEraser { get; set; }

    /// <summary>Settings of the pen and touch decisions (finger drawing).</summary>
    public Func<PointerRouterOptions>? TouchOptions { get; set; }

    /// <summary>Clicks and place of the last left press (a double click on a text box edits it).</summary>
    public int LastClickCount { get; private set; }
    public Point2 LastPress { get; private set; }

    /// <summary>The device of the last press: the touch keyboard comes up only after a finger tap.</summary>
    public PointerDevice LastPressDevice { get; private set; } = PointerDevice.Mouse;

    public PointerRouter Router => router ??= new(() => TouchOptions?.Invoke() ?? new PointerRouterOptions());

    /// <summary>The tool whose operation is in progress (a temporary one first).</summary>
    public ICanvasTool? ActiveTool => middleTool ?? touchTool ?? tool;

    /// <summary>Raised when a press starts an operation of a tool; panning does not.</summary>
    public event EventHandler? ToolPressed;

    /// <summary>Raised when a middle-button or pen-eraser operation ends: the original tool is back.</summary>
    public event EventHandler? TemporaryToolEnded;

    private static InputModifiers Modifiers(KeyModifiers k) =>
        (k.HasFlag(KeyModifiers.Alt) ? InputModifiers.Alt : 0) | (k.HasFlag(KeyModifiers.Control) ? InputModifiers.Control : 0) |
        (k.HasFlag(KeyModifiers.Shift) ? InputModifiers.Shift : 0) | (k.HasFlag(KeyModifiers.Meta) ? InputModifiers.Windows : 0);

    private CanvasPointer Pointer(PointerEventArgs e, PointerButton button = PointerButton.Left, int clicks = 1)
    {
        var point = e.GetCurrentPoint(this);
        var device = e.Pointer.Type switch { PointerType.Pen => PointerDevice.Pen, PointerType.Touch => PointerDevice.Touch, _ => PointerDevice.Mouse };
        var p = point.Properties;
        double? pressure = device == PointerDevice.Pen ? Math.Clamp(p.Pressure, 0, 1) : null;
        double contact = device == PointerDevice.Touch ? Math.Max(p.ContactRect.Width, p.ContactRect.Height) : 0;
        return new(new(point.Position.X, point.Position.Y), Modifiers(e.KeyModifiers), clicks, button, device, pressure,
            (int)e.Pointer.Id, device == PointerDevice.Pen && (p.IsEraser || p.IsBarrelButtonPressed), contact);
    }

    /// <summary>Space held outside text input: the next left drag pans the view (spec 4.4).</summary>
    public void SetTemporaryHand(bool held)
    {
        spaceHeld = held;
        UpdateCursor();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (e.Pointer.Type != PointerType.Mouse)
        {
            Run(Router.Down(Pointer(e)), e.Pointer);
            e.Handled = true;
            return;
        }
        LastPressDevice = PointerDevice.Mouse;
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (middleTool is not null)
        {
            // Another button while the middle button erases cancels the rub (spec 7.5).
            if (kind != PointerUpdateKind.MiddleButtonPressed) EndMiddle(commit: null);
            e.Handled = true;
            return;
        }
        if (kind == PointerUpdateKind.MiddleButtonPressed && MiddleButton?.Invoke() is { Reserved: true } middle)
        {
            if (!IsToolActive && !panning && middle.Tool is { } temporary && temporary.PointerDown(Pointer(e, PointerButton.Middle)))
            {
                if (temporary.IsActive) { middleTool = temporary; Capture(e.Pointer); (tool as IHoverTool)?.PointerHover(null, InputModifiers.None); }
                else temporary.Cancel();
                UpdateCursor();
                ToolPressed?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
            return;
        }
        if (kind != PointerUpdateKind.LeftButtonPressed)
        {
            if (IsToolActive) CancelTool(); // another button during a stroke never commits it (spec 5.6)
            return;
        }
        var at = e.GetPosition(this);
        LastClickCount = e.ClickCount;
        LastPress = new(at.X, at.Y);
        if (spaceHeld && !IsToolActive)
        {
            panning = true;
            panStart = at;
            panStartOffset = offset;
            Capture(e.Pointer);
            e.Handled = true;
        }
        else if (!spaceHeld && tool is not null && (!IsToolActive || tool is IMultiClickTool && !toolCaptured) &&
                 tool.PointerDown(Pointer(e, clicks: e.ClickCount)))
        {
            toolCaptured = tool.IsActive;
            if (toolCaptured) Capture(e.Pointer);
            UpdateCursor();
            e.Handled = true;
            ToolPressed?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Type != PointerType.Mouse)
        {
            var pointer = Pointer(e);
            // A pen moving without contact is near the screen: touches are the palm meanwhile.
            if (e.Pointer.Type == PointerType.Pen && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !Router.IsDrawing)
                Run(Router.PenInRange(true), e.Pointer);
            else Run(Router.Move(pointer), e.Pointer);
            return;
        }
        var at = e.GetPosition(this);
        if (panning) SetOffset(new(panStartOffset.X - (at.X - panStart.X), panStartOffset.Y - (at.Y - panStart.Y)));
        else if (middleTool is { } temporary) temporary.PointerMove(Pointer(e, PointerButton.Middle));
        else if (toolCaptured && tool is { IsActive: true }) tool.PointerMove(Pointer(e));
        else
        {
            (tool as IHoverTool)?.PointerHover(spaceHeld ? null : new Point2(at.X, at.Y), Modifiers(e.KeyModifiers));
            UpdateCursor();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Type != PointerType.Mouse)
        {
            Run(Router.Up(Pointer(e)), e.Pointer);
            e.Handled = true;
            return;
        }
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind == PointerUpdateKind.MiddleButtonReleased && middleTool is not null)
        {
            EndMiddle(commit: Pointer(e, PointerButton.Middle));
            e.Handled = true;
            return;
        }
        if (kind != PointerUpdateKind.LeftButtonReleased) return;
        if (panning)
        {
            panning = false;
            Release();
            e.Handled = true;
        }
        else if (toolCaptured && tool is { IsActive: true })
        {
            toolCaptured = false;
            tool.PointerUp(Pointer(e));
            Release();
            var at = e.GetPosition(this);
            (tool as IHoverTool)?.PointerHover(new Point2(at.X, at.Y), Modifiers(e.KeyModifiers));
            UpdateCursor();
            e.Handled = true;
        }
        else toolCaptured = false;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!ReferenceEquals(e.Pointer, captured) && !ReferenceEquals(e.Pointer, touchPointer)) return;
        if (ReferenceEquals(e.Pointer, touchPointer))
        {
            touchPointer = null;
            if (touchTool is not null) Run(Router.Cancel((int)e.Pointer.Id), null);
            return;
        }
        captured = null;
        // Losing the mouse mid-stroke (another window, Alt+Tab) cancels the unfinished operation (spec 4.3).
        EndMiddle(commit: null);
        panning = false;
        bool pressed = toolCaptured;
        toolCaptured = false;
        if (pressed && tool?.IsActive == true) tool.Cancel();
        UpdateCursor();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (e.Pointer.Type == PointerType.Pen) Run(Router.PenInRange(false), e.Pointer);
        else if (!IsToolActive) (tool as IHoverTool)?.PointerHover(null, InputModifiers.None);
    }

    private void Capture(IPointer pointer)
    {
        captured = pointer;
        pointer.Capture(this);
    }

    private void Release()
    {
        var pointer = captured;
        captured = null;
        pointer?.Capture(null);
    }

    private void EndMiddle(CanvasPointer? commit)
    {
        if (middleTool is not { } temporary) return;
        middleTool = null;
        if (commit is { } at && temporary.IsActive) temporary.PointerUp(at);
        else if (temporary.IsActive) temporary.Cancel();
        (temporary as IHoverTool)?.PointerHover(null, InputModifiers.None);
        if (!toolCaptured) Release();
        UpdateCursor();
        TemporaryToolEnded?.Invoke(this, EventArgs.Empty);
    }

    // ----- pen and touch: the router's actions -----

    private void Run(IReadOnlyList<PointerAction> actions, IPointer? pointer)
    {
        foreach (var action in actions)
            switch (action)
            {
                case PointerAction.ToolDown down:
                    PenOrFingerDown(down.Pointer, down.Erase, pointer);
                    break;
                case PointerAction.ToolMove move:
                    if (touchTool is { IsActive: true } moving) moving.PointerMove(move.Pointer);
                    break;
                case PointerAction.ToolUp up:
                    if (touchTool is { IsActive: true } lifting) lifting.PointerUp(up.Pointer);
                    EndTouchTool();
                    break;
                case PointerAction.ToolCancel:
                    if (touchTool is { IsActive: true } cancelled) cancelled.Cancel();
                    EndTouchTool();
                    break;
                case PointerAction.Pan pan:
                    SetOffset(new(offset.X - pan.Delta.X, offset.Y - pan.Delta.Y));
                    break;
                case PointerAction.Zoom z:
                    ZoomBy(z.Factor, z.Center);
                    break;
                case PointerAction.Tap tap:
                    FingerTap(tap.Pointer);
                    break;
            }
    }

    private void PenOrFingerDown(CanvasPointer pointer, bool erase, IPointer? device)
    {
        if (middleTool is not null || toolCaptured || panning) return; // the mouse is busy
        LastPressDevice = pointer.Device;
        var target = erase ? PenEraser?.Invoke() : tool;
        if (target is null || target.IsActive && !ReferenceEquals(target, tool)) return;
        if (erase && tool?.IsActive == true) tool.Cancel();
        if (!target.PointerDown(pointer)) return;
        ToolPressed?.Invoke(this, EventArgs.Empty);
        if (!target.IsActive) return;
        touchTool = target;
        touchPointer = device;
        device?.Capture(this);
    }

    private void EndTouchTool()
    {
        bool eraser = touchTool is not null && !ReferenceEquals(touchTool, tool);
        touchTool = null;
        var device = touchPointer;
        touchPointer = null;
        device?.Capture(null);
        if (eraser) TemporaryToolEnded?.Invoke(this, EventArgs.Empty);
        UpdateCursor();
    }

    /// <summary>For tests: a pen event (0 down, 1 move, 2 up) through the router and the canvas actions.</summary>
    public void ProbePen(CanvasPointer pointer, int phase) => Run(phase switch { 0 => Router.Down(pointer), 1 => Router.Move(pointer), _ => Router.Up(pointer) }, null);

    private void FingerTap(CanvasPointer pointer)
    {
        if (IsToolActive) return;
        LastPressDevice = PointerDevice.Touch;
        if (!TouchRules.TapReachesTool(tool) || tool is null || !tool.PointerDown(pointer)) return;
        ToolPressed?.Invoke(this, EventArgs.Empty);
        if (tool.IsActive) tool.PointerUp(pointer);
        UpdateCursor();
    }

    private void CancelTouch()
    {
        foreach (var _ in Router.Reset()) { }
        if (touchTool is { IsActive: true } active) active.Cancel();
        touchTool = null;
        var device = touchPointer;
        touchPointer = null;
        device?.Capture(null);
    }

    // ----- cursor -----

    private void UpdateCursor()
    {
        StandardCursorType type;
        if (spaceHeld || panning) type = StandardCursorType.Hand;
        else type = ActiveTool?.Cursor is { } c ? CursorFor(c) : StandardCursorType.Arrow;
        Cursor = new Cursor(type);
    }

    /// <summary>The editor's cursor request as an Avalonia standard cursor (custom rotate/lock cursors come with A5).</summary>
    public static StandardCursorType CursorFor(ToolCursor cursor) => cursor.Shape switch
    {
        CursorShape.Pen or CursorShape.Cross => StandardCursorType.Cross,
        CursorShape.IBeam => StandardCursorType.Ibeam,
        CursorShape.Hand => StandardCursorType.Hand,
        CursorShape.Move or CursorShape.SizeAll => StandardCursorType.SizeAll,
        CursorShape.Rotate => StandardCursorType.Hand,
        CursorShape.Lock => StandardCursorType.No,
        CursorShape.Resize => ResizeCursor(cursor.Direction),
        _ => StandardCursorType.Arrow
    };

    private static StandardCursorType ResizeCursor(Point2 d)
    {
        double angle = Math.Atan2(d.Y, d.X) * 180 / Math.PI;
        angle = (angle + 180) % 180; // a resize cursor points both ways
        return angle switch
        {
            < 22.5 or >= 157.5 => StandardCursorType.SizeWestEast,
            < 67.5 => StandardCursorType.BottomRightCorner,
            < 112.5 => StandardCursorType.SizeNorthSouth,
            _ => StandardCursorType.BottomLeftCorner
        };
    }
}
