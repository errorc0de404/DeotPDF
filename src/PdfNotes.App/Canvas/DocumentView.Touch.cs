using System.Windows;
using System.Windows.Input;
using PdfNotes.Core.Geometry;

namespace PdfNotes.App.Canvas;

/// <summary>
/// Pen and touch (spec 7.6, roadmap P3). WPF stylus events (pen and finger alike) become <see cref="CanvasPointer"/>s
/// for the editor's <see cref="PointerRouter"/>, which decides; this file only carries out its actions. The mouse events
/// WPF promotes from them are ignored (see <see cref="FromStylus"/>), so a pen or finger never acts twice.
/// </summary>
public sealed partial class DocumentView
{
    private PointerRouter? router;
    private ICanvasTool? touchTool; // the tool a pen or finger is using (the pen eraser while it erases)
    private StylusDevice? touchCapture;

    /// <summary>Settings of the router (finger drawing); read at every contact.</summary>
    public Func<PointerRouterOptions>? TouchOptions { get; set; }

    /// <summary>The eraser a pen's eraser end or barrel button uses (the last eraser of the toolbar).</summary>
    public Func<ICanvasTool?>? PenEraser { get; set; }

    /// <summary>The device of the last press on the canvas: the touch keyboard comes up only after a finger tap (spec 7.6).</summary>
    public PointerDevice LastPressDevice { get; private set; } = PointerDevice.Mouse;

    /// <summary>For tests and Probes: the decisions behind the stylus events.</summary>
    public PointerRouter Router => router ??= new(() => TouchOptions?.Invoke() ?? new PointerRouterOptions());

    /// <summary>True for a mouse event WPF made from pen or touch input; the stylus handlers below already acted on it.</summary>
    private static bool FromStylus(MouseEventArgs e) => e.StylusDevice is not null;

    private static void PrepareStylus(UIElement element)
    {
        // The canvas draws on a press: no press-and-hold right click, flicks or tap ripples.
        Stylus.SetIsPressAndHoldEnabled(element, false);
        Stylus.SetIsFlicksEnabled(element, false);
        Stylus.SetIsTapFeedbackEnabled(element, false);
        Stylus.SetIsTouchFeedbackEnabled(element, false);
    }

    protected override void OnStylusInRange(StylusEventArgs e)
    {
        base.OnStylusInRange(e);
        if (IsPen(e.StylusDevice)) Run(Router.PenInRange(true), e.StylusDevice);
    }

    protected override void OnStylusOutOfRange(StylusEventArgs e)
    {
        base.OnStylusOutOfRange(e);
        if (IsPen(e.StylusDevice)) Run(Router.PenInRange(false), e.StylusDevice);
    }

    protected override void OnStylusDown(StylusDownEventArgs e)
    {
        base.OnStylusDown(e);
        Focus();
        var points = e.GetStylusPoints(this);
        var pointer = Pointer(e.StylusDevice, e.GetPosition(this), points, points.Count - 1);
        Run(Router.Down(pointer), e.StylusDevice);
        e.Handled = true;
    }

    protected override void OnStylusMove(StylusEventArgs e)
    {
        base.OnStylusMove(e);
        // Every sample of the report counts: a fast pen stroke keeps its shape and its pressure changes.
        var points = e.GetStylusPoints(this);
        if (points.Count == 0) Run(Router.Move(Pointer(e.StylusDevice, e.GetPosition(this), points, -1)), e.StylusDevice);
        for (int i = 0; i < points.Count; i++)
            Run(Router.Move(Pointer(e.StylusDevice, new(points[i].X, points[i].Y), points, i)), e.StylusDevice);
        e.Handled = true;
    }

    protected override void OnStylusUp(StylusEventArgs e)
    {
        base.OnStylusUp(e);
        var points = e.GetStylusPoints(this);
        Run(Router.Up(Pointer(e.StylusDevice, e.GetPosition(this), points, points.Count - 1)), e.StylusDevice);
        e.Handled = true;
    }

    protected override void OnLostStylusCapture(StylusEventArgs e)
    {
        base.OnLostStylusCapture(e);
        if (ReferenceEquals(touchCapture, e.StylusDevice)) touchCapture = null;
        // Losing a contact mid-stroke (another window, Alt+Tab) drops the stroke, as with the mouse (spec 4.3).
        if (touchTool is not null) Run(Router.Cancel(e.StylusDevice.Id), e.StylusDevice);
    }

    private static bool IsPen(StylusDevice device) => device.TabletDevice?.Type == TabletDeviceType.Stylus;

    private static CanvasPointer Pointer(StylusDevice device, Point at, StylusPointCollection points, int index)
    {
        bool pen = IsPen(device);
        double? pressure = null;
        double contact = 0;
        if (index >= 0 && index < points.Count)
        {
            var p = points[index];
            if (pen && points.Description.HasProperty(StylusPointProperties.NormalPressure)) pressure = p.PressureFactor;
            if (!pen) contact = ContactSize(points.Description, p);
        }
        bool barrel = device.StylusButtons.Any(b => b.Guid == StylusPointProperties.BarrelButton.Id && b.StylusButtonState == StylusButtonState.Down);
        return new(new(at.X, at.Y), Modifiers, 1, PointerButton.Left, pen ? PointerDevice.Pen : PointerDevice.Touch, pressure,
            device.Id, pen && (device.Inverted || barrel), contact);
    }

    /// <summary>The longer side of a finger's contact in DIPs, when the digitizer reports it (palm rejection).</summary>
    private static double ContactSize(StylusPointDescription description, StylusPoint point)
    {
        double size = 0;
        foreach (var property in new[] { StylusPointProperties.Width, StylusPointProperties.Height })
        {
            if (!description.HasProperty(property)) continue;
            var info = description.GetPropertyInfo(property);
            if (info.Resolution <= 0) continue;
            double value = point.GetPropertyValue(property) / info.Resolution;
            double inches = info.Unit switch
            {
                StylusPointPropertyUnit.Inches => value,
                StylusPointPropertyUnit.Centimeters => value / 2.54,
                _ => double.NaN
            };
            if (double.IsFinite(inches)) size = Math.Max(size, inches * 96);
        }
        return size;
    }

    private void Run(IReadOnlyList<PointerAction> actions, StylusDevice? device)
    {
        foreach (var action in actions)
            switch (action)
            {
                case PointerAction.ToolDown down:
                    PenOrFingerDown(down.Pointer, down.Erase, device);
                    break;
                case PointerAction.ToolMove move:
                    if (touchTool is { IsActive: true } moving) moving.PointerMove(move.Pointer);
                    break;
                case PointerAction.ToolUp up:
                    if (touchTool is { IsActive: true } lifting) lifting.PointerUp(up.Pointer);
                    EndTouchTool(device);
                    break;
                case PointerAction.ToolCancel:
                    if (touchTool is { IsActive: true } cancelled) cancelled.Cancel();
                    EndTouchTool(device);
                    break;
                case PointerAction.Pan pan:
                    SetOffset(new(offset.X - pan.Delta.X, offset.Y - pan.Delta.Y));
                    break;
                case PointerAction.Zoom z:
                    ZoomBy(z.Factor, new Point(z.Center.X, z.Center.Y));
                    break;
                case PointerAction.Tap tap:
                    FingerTap(tap.Pointer);
                    break;
            }
    }

    private void PenOrFingerDown(CanvasPointer pointer, bool erase, StylusDevice? device)
    {
        if (middleTool is not null || toolCaptured || panning) return; // the mouse is busy
        LastPressDevice = pointer.Device;
        var target = erase ? PenEraser?.Invoke() : tool;
        if (target is null || target.IsActive && !ReferenceEquals(target, tool)) return;
        if (erase && tool?.IsActive == true) tool.Cancel(); // a free polygon between clicks
        if (!target.PointerDown(pointer)) return;
        ToolPressed?.Invoke(this, EventArgs.Empty);
        if (!target.IsActive) return; // a click that finished at once (a new text box)
        touchTool = target;
        if (device?.Capture(this) == true) touchCapture = device;
        (tool as IHoverTool)?.PointerHover(null, Modifiers);
    }

    private void EndTouchTool(StylusDevice? device)
    {
        bool eraser = touchTool is not null && !ReferenceEquals(touchTool, tool);
        touchTool = null;
        if (touchCapture is not null && ReferenceEquals(touchCapture, device))
        {
            touchCapture = null;
            device.Capture(null);
        }
        if (eraser) MiddleButtonEnded?.Invoke(this, EventArgs.Empty); // the toolbar shows the drawing tool again
        UpdateCursor();
    }

    /// <summary>A finger tap is a click for the selection, text and note tools (spec 7.6), and follows a link like a click.</summary>
    private void FingerTap(CanvasPointer pointer)
    {
        if (IsToolActive) return;
        LastPressDevice = PointerDevice.Touch;
        var at = new Point(pointer.Position.X, pointer.Position.Y);
        if (LinkTip?.Invoke(at) is not null && LinkFollowsClick?.Invoke() == true)
        {
            Dispatcher.BeginInvoke(() => LinkClicked?.Invoke(this, at));
            return;
        }
        if (!TouchRules.TapReachesTool(tool) || tool is null || !tool.PointerDown(pointer)) return;
        ToolPressed?.Invoke(this, EventArgs.Empty);
        if (tool.IsActive) tool.PointerUp(pointer);
        UpdateCursor();
    }

    /// <summary>
    /// For Probes: a pen event (0 down, 1 move, 2 up) through the router and the view's actions, as if WPF had reported
    /// it. Injected pen input cannot carry the eraser end or barrel button through WPF's default stylus stack.
    /// </summary>
    public void ProbePen(CanvasPointer pointer, int phase) => Run(phase switch
    {
        0 => Router.Down(pointer),
        1 => Router.Move(pointer),
        _ => Router.Up(pointer)
    }, null);

    /// <summary>Drops a pen or finger operation (Esc, tool switch, document change).</summary>
    private void CancelTouch()
    {
        foreach (var _ in Router.Reset()) { }
        if (touchTool is { IsActive: true } active) active.Cancel();
        touchTool = null;
        if (touchCapture is { } device)
        {
            touchCapture = null;
            device.Capture(null);
        }
    }
}
