using PdfNotes.Core.Geometry;

namespace PdfNotes.Editor.Input;

/// <summary>Modifier keys held during an input event. Same bit values as the Windows modifiers, so a platform can cast.</summary>
[Flags]
public enum InputModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }

/// <summary>What produced a pointer event.</summary>
public enum PointerDevice { Mouse, Pen, Touch }

/// <summary>The button of a pointer event (a pen tip is <see cref="Left"/>, its barrel button <see cref="Right"/>).</summary>
public enum PointerButton { Left, Middle, Right }

/// <summary>
/// A pointer event on the document view, in viewport DIPs. <see cref="ClickCount"/> is 2 on a double-click press.
/// <see cref="Pressure"/> is 0–1 when the device reports it, null otherwise.
/// </summary>
public readonly record struct CanvasPointer(Point2 Position, InputModifiers Modifiers, int ClickCount = 1,
    PointerButton Button = PointerButton.Left, PointerDevice Device = PointerDevice.Mouse, double? Pressure = null);

/// <summary>Cursor shapes a tool asks for; the platform maps them to its own cursors.</summary>
public enum CursorShape { Arrow, Pen, Cross, IBeam, Hand, Move, SizeAll, Rotate, Resize, Lock }

/// <summary>
/// A cursor request. <see cref="Direction"/> is the screen direction of a <see cref="CursorShape.Resize"/> cursor (from the
/// frame center to the handle); <see cref="Scale"/> is the display scale a <see cref="CursorShape.Rotate"/> cursor is drawn for.
/// </summary>
public readonly record struct ToolCursor(CursorShape Shape, Point2 Direction = default, double Scale = 1)
{
    public static ToolCursor Arrow => new(CursorShape.Arrow);
    public static ToolCursor Pen => new(CursorShape.Pen);
    public static ToolCursor Cross => new(CursorShape.Cross);
    public static ToolCursor IBeam => new(CursorShape.IBeam);
    public static ToolCursor Hand => new(CursorShape.Hand);
    public static ToolCursor Move => new(CursorShape.Move);
    public static ToolCursor SizeAll => new(CursorShape.SizeAll);
    public static ToolCursor Rotate(double scale) => new(CursorShape.Rotate, Scale: scale);
    public static ToolCursor Resize(Point2 direction) => new(CursorShape.Resize, direction);

    /// <summary>Over a locked annotation (spec 5.2, D14g): it cannot be moved, deleted or erased.</summary>
    public static ToolCursor Lock(double scale) => new(CursorShape.Lock, Scale: scale);
}
