using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;
using PdfNotes.Core.Marks;

namespace PdfNotes.Editor.Tools;

/// <summary>
/// Pointer handling of the active canvas tool (spec 4.3: start → preview → commit or cancel). The document view
/// routes left-button input here unless the temporary hand tool is active, captures the pointer while
/// <see cref="IsActive"/>, and calls <see cref="Cancel"/> on Esc, right button or lost capture.
/// </summary>
public interface ICanvasTool
{
    ToolCursor? Cursor { get; }

    /// <summary>True while an operation (for example a stroke) is in progress.</summary>
    bool IsActive { get; }

    /// <summary>Returns true when the tool started an operation and wants the pointer captured.</summary>
    bool PointerDown(CanvasPointer pointer);

    void PointerMove(CanvasPointer pointer);

    /// <summary>Completes the operation (one Undo unit).</summary>
    void PointerUp(CanvasPointer pointer);

    /// <summary>Discards the operation without changing the document.</summary>
    void Cancel();
}

/// <summary>A tool that shows something under the pointer while no operation is in progress (the eraser circle).</summary>
public interface IHoverTool
{
    /// <summary>The pointer moved to <paramref name="position"/> (viewport DIPs), or left the view when null.</summary>
    void PointerHover(Point2? position, InputModifiers modifiers);
}

/// <summary>A press that keeps the tool active between clicks (the free polygon): the view routes later presses to it.</summary>
public interface IMultiClickTool
{
    /// <summary>Completes the operation in progress (Enter).</summary>
    void Finish();
}

/// <summary>
/// What the tools need from the document view: page hit tests and transforms, scrolling, and the preview layer. The
/// platform view implements it; drawing stays on the platform side (tools describe previews as <see cref="CanvasMark"/>s).
/// </summary>
public interface ICanvasView
{
    PageAnchor? HitTestPage(Point2 viewportPoint);
    ViewTransform PageTransform(int index);
    double DpiScale { get; }
    Point2 Offset { get; }
    void SetHorizontalOffset(double value);
    void SetVerticalOffset(double value);

    /// <summary>Shows <paramref name="preview"/> on page <paramref name="pageIndex"/> (-1 or null: none). <paramref name="mark"/>: drawn above the page marks.</summary>
    void SetPreview(int pageIndex, ICanvasPreview? preview, bool mark = false);
    void RefreshPreview();
    void RefreshOverlays();
    void RefreshOverlay(int pageIndex);
}

/// <summary>A tool's live preview on one page, as marks the platform paints in order.</summary>
public interface ICanvasPreview
{
    IEnumerable<CanvasMark> Marks(int pageIndex, Page page, ViewTransform transform);
}
