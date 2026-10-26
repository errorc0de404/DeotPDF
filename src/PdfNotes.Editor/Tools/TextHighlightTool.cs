using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;
using Page = PdfNotes.Core.Models.Page;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;

namespace PdfNotes.Editor.Tools;

/// <summary>
/// The highlighter on text (spec 9.2, 9.4–9.6; part of <see cref="HighlighterTool"/> since D14g). A drag that starts on
/// PDF text selects glyphs in reading order (one page) and adds a <see cref="PdfTextHighlight"/> (in markup mode a
/// <see cref="PdfTextMarkup"/>) with one quad per line. A drag that starts on an unlocked text box selects its characters
/// and adds a highlight range to the box, or in markup mode turns the box's underline/strikethrough formatting on for the
/// range (off when the whole range has it already). A press away from extractable text makes nothing and shows no notice
/// (no OCR). One drag is one Undo unit; Esc cancels. A click (no drag) on an existing highlight hands it to
/// <paramref name="select"/> for editing, and a double-click on a word marks that word at once.
/// </summary>
/// <param name="markup">Underline/strikeout mode: the kind and its style, or null for highlights.</param>
public sealed class TextHighlightTool(ICanvasView view, Func<DocumentSession?> session, Func<int, IReadOnlyList<TextGlyph>> glyphs,
    Func<TextHighlightStyle> style, Action<Guid> select, Func<(TextMarkupKind Kind, StrokeStyle Style)?>? markup = null) : ICanvasTool, IHoverTool, ICanvasPreview
{
    private (TextMarkupKind Kind, StrokeStyle Style)? mark;

    private string Label => mark is { Kind: var k } ? (k == TextMarkupKind.Underline ? "undo.underline" : "undo.strikeOut") : "undo.textHighlight";

    /// <summary>A press that moves less than this on screen is a click.</summary>
    public const double ClickDip = 4;

    public const double StartToleranceDip = 6;

    private int pageIndex = -1;
    private Guid pageId;
    private int anchor, focus;
    private TextBoxAnnotation? box;
    private TextHighlightStyle current = TextHighlightStyle.Default;
    private bool overText;
    private Point2 start;
    private double reachDip;
    private Guid? pressed; // existing highlight (or highlighted text box) under the press

    public ToolCursor? Cursor => overText || IsActive ? ToolCursor.IBeam : ToolCursor.Arrow;
    public bool IsActive => pageIndex >= 0;

    /// <summary>True when a press at <paramref name="pointer"/> starts on PDF glyphs or an unlocked text box (start tolerance 6 DIP).</summary>
    public bool StartsOnText(CanvasPointer pointer)
    {
        if (session() is not { } s || view.HitTestPage(pointer.Position) is not { } at) return false;
        var page = s.Current.Pages[at.PageIndex];
        if (BoxAt(page, at.PagePoint) is { } b) return BoxCaret(b, at.PagePoint) is not null;
        return TextSelection.CaretAt(glyphs(at.PageIndex), at.PagePoint, view.PageTransform(at.PageIndex).DipLengthToPoints(StartToleranceDip)) is not null;
    }

    public bool PointerDown(CanvasPointer pointer)
    {
        if (session() is not { } s || view.HitTestPage(pointer.Position) is not { } at) return false;
        var page = s.Current.Pages[at.PageIndex];
        double tolerance = view.PageTransform(at.PageIndex).DipLengthToPoints(StartToleranceDip);
        current = style() with { Style = style().Style.ClampFor(AnnotationKind.PdfTextHighlight) };
        mark = markup?.Invoke() is { } m ? (m.Kind, m.Style.ClampFor(AnnotationKind.TextMarkup)) : null;
        var boxTarget = BoxAt(page, at.PagePoint);
        // Only the second press marks the word: further presses of the same click run (ClickCount 3, 4, …) used to
        // stack copies of the word highlight until the text vanished under it (D09 leftover, "98%").
        if (pointer.ClickCount >= 3) return false;
        if (pointer.ClickCount == 2) return HighlightWord(page, at.PageIndex, at.PagePoint, boxTarget, tolerance);
        pressed = page.Annotations.LastOrDefault(a => a is PdfTextHighlight { Locked: false } or PdfTextMarkup { Locked: false } && AnnotationGeometry.Hit(a, at.PagePoint, 0))?.Id;
        if (boxTarget is { } target)
        {
            if (BoxCaret(target, at.PagePoint) is not { } c) return false;
            box = target; anchor = focus = c;
            pressed ??= BoxCharacter(target, at.PagePoint) is { } ch && target.Highlights.Any(h => ch.Offset >= h.Start && ch.Offset < h.Start + h.Length) ? target.Id : null;
        }
        else
        {
            var text = glyphs(at.PageIndex);
            if (TextSelection.CaretAt(text, at.PagePoint, tolerance) is not { } c)
            {
                if (pressed is { } id) { select(id); pressed = null; return false; } // a manual-height band reaching past its glyphs
                return false;
            }
            box = null; anchor = focus = c;
        }
        start = pointer.Position; reachDip = 0;
        pageIndex = at.PageIndex; pageId = page.Id;
        view.SetPreview(pageIndex, this);
        return true;
    }

    public void PointerMove(CanvasPointer pointer)
    {
        if (!IsActive) return;
        reachDip = Math.Max(reachDip, pointer.Position.DistanceTo(start));
        var p = view.PageTransform(pageIndex).DipToPage.Transform(new(pointer.Position.X, pointer.Position.Y));
        int? caret = box is { } b ? BoxCaret(b, p) : TextSelection.CaretAt(glyphs(pageIndex), p);
        if (caret is { } c && c != focus) { focus = c; view.RefreshPreview(); }
    }

    public void PointerUp(CanvasPointer pointer)
    {
        if (!IsActive) return;
        PointerMove(pointer);
        int a = Math.Min(anchor, focus), z = Math.Max(anchor, focus);
        var target = box; Guid page = pageId; int index = pageIndex;
        var clicked = reachDip < ClickDip ? pressed : null;
        bool click = reachDip < ClickDip;
        Reset();
        if (clicked is { } existing) { select(existing); return; } // edit what is already highlighted
        if (click) return; // a click on text makes nothing (spec 9.2)
        if (z <= a || session() is not { } s || s.Current.IndexOfPage(page) < 0) return;
        if (target is not null)
        {
            if (s.Current.FindAnnotation(target.Id)?.Annotation is not TextBoxAnnotation { Locked: false } live) return;
            var after = MarkBox(live, a, z - a);
            if (!ReferenceEquals(after, live)) s.Execute(DocumentEdits.UpdateAnnotations(s.Current, [after], Label));
            return;
        }
        if (Create(index, a, z) is { } highlight) s.Execute(DocumentEdits.AddAnnotation(s.Current, page, highlight, label: Label));
    }

    public void Cancel() => Reset();

    /// <summary>
    /// The box with [start, start+length) marked: a highlight range, or in markup mode the underline/strikethrough
    /// formatting turned on (off when every character of the range has it already). The same box when nothing changes.
    /// </summary>
    private TextBoxAnnotation MarkBox(TextBoxAnnotation box, int start, int length)
    {
        if (mark is not { Kind: var kind })
            return TextHighlights.Add(box, start, length, new TextHighlightRange(0, 0, current.Mode, current.ManualHeight, current.Style.Color, current.Style.Opacity));
        var (a, z) = TextEditing.Range(box.Text, start, length);
        if (z <= a) return box;
        bool Has(TextRun r) => kind == TextMarkupKind.Underline ? r.Underline : r.Strikethrough;
        bool all = box.Runs.Where(r => r.Start < z && r.Start + r.Length > a).All(Has);
        var after = TextEditing.Format(box, a, z - a, r => kind == TextMarkupKind.Underline ? r with { Underline = !all } : r with { Strikethrough = !all });
        return after.Runs.SequenceEqual(box.Runs) ? box : after;
    }

    public void PointerHover(Point2? position, InputModifiers modifiers)
    {
        overText = false;
        if (position is not { } p || session() is not { } s || view.HitTestPage(p) is not { } at) return;
        double tolerance = view.PageTransform(at.PageIndex).DipLengthToPoints(StartToleranceDip);
        overText = BoxAt(s.Current.Pages[at.PageIndex], at.PagePoint) is not null || TextSelection.CaretAt(glyphs(at.PageIndex), at.PagePoint, tolerance) is not null;
    }

    private void Reset()
    {
        pageIndex = -1; box = null; pressed = null;
        view.SetPreview(-1, null);
    }

    private Annotation? Create(int index, int a, int z)
    {
        var text = glyphs(index);
        var quads = TextSelection.LineQuads(text, a, z);
        if (quads.IsEmpty) return null;
        string hint = TextSelection.TextOf(text, a, z);
        if (hint.Length > ModelLimits.MaxHintLength) hint = hint[..ModelLimits.MaxHintLength];
        if (mark is { } m) return new PdfTextMarkup(Guid.NewGuid(), false, quads, hint, m.Kind, m.Style.WidthPt, m.Style.Color, m.Style.Opacity);
        return new PdfTextHighlight(Guid.NewGuid(), false, quads, hint, current.Mode, current.ManualHeight, current.Style.Color, current.Style.Opacity);
    }

    /// <summary>Double-click: highlights the word under the point at once (PDF glyphs or text box characters). One Undo unit.</summary>
    private bool HighlightWord(Page page, int index, Point2 p, TextBoxAnnotation? target, double tolerance)
    {
        if (session() is not { } s) return false;
        if (target is not null)
        {
            var characters = TextLayout.Measure(target).Characters;
            if (BoxCharacter(target, p) is not { } hit) return false;
            int at = characters.IndexOf(hit);
            if (TextSelection.Word(characters.Length, at, i => characters[i].Text,
                    (a, b) => characters[a].Line == characters[b].Line && characters[a].Offset + characters[a].Text.Length == characters[b].Offset) is not { } w) return false;
            int a0 = characters[w.Start].Offset, z0 = characters[w.End - 1].Offset + characters[w.End - 1].Text.Length;
            var after = MarkBox(target, a0, z0 - a0);
            if (!after.Highlights.SequenceEqual(target.Highlights) || !after.Runs.SequenceEqual(target.Runs))
                s.Execute(DocumentEdits.UpdateAnnotations(s.Current, [after], Label));
            return false;
        }
        if (TextSelection.WordAt(glyphs(index), p, tolerance) is not { } word) return false;
        // A word already marked the same way is not covered a second time.
        var created = Create(index, word.Start, word.End);
        bool repeated = created switch
        {
            PdfTextHighlight highlight => page.Annotations.OfType<PdfTextHighlight>().Any(h => h.Quads.SequenceEqual(highlight.Quads) &&
                h.HeightMode == highlight.HeightMode && h.ManualHeightPt == highlight.ManualHeightPt && h.Color == highlight.Color && h.Opacity == highlight.Opacity),
            PdfTextMarkup markup => page.Annotations.OfType<PdfTextMarkup>().Any(h => h.Quads.SequenceEqual(markup.Quads) && h.MarkupKind == markup.MarkupKind &&
                h.WidthPt == markup.WidthPt && h.Color == markup.Color && h.Opacity == markup.Opacity),
            _ => true
        };
        if (!repeated) s.Execute(DocumentEdits.AddAnnotation(s.Current, page.Id, created!, label: Label));
        return false;
    }

    /// <summary>The laid-out character of a box nearest to a page point (same line first).</summary>
    private static TextLayout.Character? BoxCharacter(TextBoxAnnotation box, Point2 pagePoint)
    {
        var characters = TextLayout.Measure(box).Characters;
        if (characters.IsEmpty) return null;
        var local = box.Transform.Invert().Transform(pagePoint);
        return characters.MinBy(c => Distance(c.Bounds, local));
    }

    /// <summary>Topmost unlocked text box whose laid-out rectangle contains the page point.</summary>
    private static TextBoxAnnotation? BoxAt(Page page, Point2 p) =>
        page.Annotations.OfType<TextBoxAnnotation>().LastOrDefault(t => !t.Locked && !t.IsBlank && AnnotationGeometry.Hit(t, p, 0, TextLayout.Bounds));

    /// <summary>Caret (UTF-16 offset) in the box nearest to a page point: before the closest character, or after it past its middle.</summary>
    private static int? BoxCaret(TextBoxAnnotation box, Point2 pagePoint)
    {
        var characters = TextLayout.Measure(box).Characters;
        if (characters.IsEmpty) return null;
        var local = box.Transform.Invert().Transform(pagePoint);
        var best = characters.MinBy(c => Distance(c.Bounds, local))!;
        bool after = local.X > best.Bounds.X + best.Bounds.Width / 2;
        return after ? best.Offset + best.Text.Length : best.Offset;
    }

    private static double Distance(Rect2 r, Point2 p)
    {
        double dx = Math.Max(0, Math.Max(r.X - p.X, p.X - (r.X + r.Width)));
        double dy = Math.Max(0, Math.Max(r.Y - p.Y, p.Y - (r.Y + r.Height)));
        return dy * 1000 + dx; // the line first, then the position on it
    }

    IEnumerable<CanvasMark> ICanvasPreview.Marks(int index, Page page, ViewTransform transform)
    {
        if (index != pageIndex) yield break;
        int a = Math.Min(anchor, focus), z = Math.Max(anchor, focus);
        if (z <= a) yield break;
        if (box is { } b)
        {
            // Markup mode previews the range as a faint band of the markup color (the formatting comes on release).
            var range = mark is { Style: var ms } ? new TextHighlightRange(0, 0, HighlightHeightMode.Auto, null, ms.Color, .25)
                : new TextHighlightRange(0, 0, current.Mode, current.ManualHeight, current.Style.Color, current.Style.Opacity);
            var preview = TextHighlights.Add(b with { Highlights = [] }, a, z - a, range);
            yield return new TextBoxHighlightMark(b, preview);
            yield break;
        }
        if (Create(index, a, z) is { } created && created is PdfTextHighlight or PdfTextMarkup) yield return new AnnotationMark(created);
    }
}
