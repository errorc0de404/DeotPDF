using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;

namespace PdfNotes.Editor.Tools;

/// <summary>
/// The one highlighter (spec 9.1–9.2, D14g): free highlight strokes, glyph highlights and glyph underlines/strikeouts.
/// The press decides once, by where it starts: passing over text later does not change it.
/// <list type="bullet">
/// <item>Underline/strikeout (<paramref name="markup"/> on): always <paramref name="text"/> in markup mode; away from text it makes nothing.</item>
/// <item>Highlight with <paramref name="snap"/> on: a press on PDF glyphs or text box characters is <paramref name="text"/>; anything else, or Alt held, a free stroke.</item>
/// <item>Highlight with snapping off: always a free stroke.</item>
/// </list>
/// A click (no drag) on an existing highlighter result — free highlight stroke, glyph highlight or markup — hands it to
/// <paramref name="select"/> instead of drawing a dot (spec 5.4).
/// </summary>
public sealed class HighlighterTool(ICanvasView view, Func<DocumentSession?> session, StrokeTool stroke, TextHighlightTool text,
    Func<bool> snap, Func<bool> markup, Action<Guid> select) : ICanvasTool, IHoverTool
{
    private ICanvasTool? active;
    private bool overText;
    private Guid? pressed;
    private Point2 start;
    private double reachDip;

    public StrokeTool Stroke => stroke;
    public ToolCursor? Cursor => active is not null ? active.Cursor : overText ? ToolCursor.IBeam : markup() ? ToolCursor.Arrow : stroke.Cursor;
    public bool IsActive => active?.IsActive == true;

    private bool OnText(CanvasPointer pointer) => markup() || snap() && !pointer.Modifiers.HasFlag(InputModifiers.Alt) && text.StartsOnText(pointer);

    public bool PointerDown(CanvasPointer pointer)
    {
        pressed = null;
        if (markup() || OnText(pointer))
        {
            active = text;
        }
        else
        {
            active = stroke;
            pressed = ExistingAt(pointer.Position);
            start = pointer.Position; reachDip = 0;
        }
        if (active.PointerDown(pointer)) return true;
        active = null;
        return false;
    }

    public void PointerMove(CanvasPointer pointer)
    {
        if (active == stroke) reachDip = Math.Max(reachDip, pointer.Position.DistanceTo(start));
        active?.PointerMove(pointer);
    }

    public void PointerUp(CanvasPointer pointer)
    {
        if (active == stroke && pressed is { } existing && Math.Max(reachDip, pointer.Position.DistanceTo(start)) < TextHighlightTool.ClickDip)
        {
            stroke.Cancel(); // a click on an existing result edits it instead of leaving a dot
            active = null; pressed = null;
            select(existing);
            return;
        }
        active?.PointerUp(pointer);
        active = null; pressed = null;
    }

    public void Cancel()
    {
        active?.Cancel();
        active = null; pressed = null;
    }

    /// <summary>Over text the highlighter will mark the cursor is an I-beam (Alt with snapping shows the pen: a free stroke).</summary>
    public void PointerHover(Point2? position, InputModifiers modifiers)
    {
        overText = position is { } p && (markup() || snap() && !modifiers.HasFlag(InputModifiers.Alt)) && text.StartsOnText(new(p, modifiers));
        if (markup()) text.PointerHover(position, modifiers);
    }

    /// <summary>The topmost unlocked highlighter result under a viewport point (free highlight stroke, glyph highlight or markup).</summary>
    private Guid? ExistingAt(Point2 viewportPoint)
    {
        if (session() is not { } s || view.HitTestPage(viewportPoint) is not { } at) return null;
        return s.Current.Pages[at.PageIndex].Annotations.LastOrDefault(a => !a.Locked && a is FreeHighlightStroke or PdfTextHighlight or PdfTextMarkup &&
            AnnotationGeometry.Hit(a, at.PagePoint, 0))?.Id;
    }
}
