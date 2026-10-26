using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;

namespace PdfNotes.Editor.Tools;

/// <summary>Where a press lands relative to the current selection (used by the lasso tool).</summary>
public enum SelectionHit { None, Handle, Annotation, Frame }

/// <summary>
/// Page-scoped selection. Preview copies are committed as one command on release. Like a PDF reader (spec 5.3), a drag
/// that starts on PDF text (I-beam) selects glyphs instead of panning; <see cref="SelectedText"/> is what Ctrl+C copies.
/// Annotations bound to glyphs (PDF text highlights) can be selected, styled, locked and deleted but never moved,
/// scaled or rotated; a selection containing one shows its frame without handles.
/// </summary>
public sealed class SelectTool(ICanvasView view, Func<DocumentSession?> session, Func<int, IReadOnlyList<TextGlyph>>? glyphs = null) : ICanvasTool, IHoverTool, ICanvasPreview
{
    public const double PickToleranceDip = 4;
    private readonly HashSet<Guid> ids = [];
    private Annotation[] originals = [], preview = [];
    private Point2 start;
    private Point2 panOffset;
    private Affine frame;
    private Rect2 bounds;
    private int drag; // 0 idle, 1 move, 2 pan, 3 rotate, 4..7 corners, 8.. line point handles (ends first, then controls), TextDrag
    private const int TextDrag = 1000, RectDrag = 1001;
    private int rectPage = -1;
    private Point2 rectStart, rectEnd; // canonical points of rectPage
    private int textPage = -1, textAnchor, textFocus;
    private bool overText;
    private ToolCursor? hoverCursor, dragCursor;
    public const double HandleRadiusDip = 7;

    /// <summary>Presses this close outside the selection frame (screen DIPs) still move the selection (spec 5.3, D11).</summary>
    public const double FrameMarginDip = 6;

    /// <summary>Rotation snaps to the nearest right angle within this many degrees unless Shift or Alt is held (spec 5.3).</summary>
    public const double RightAngleSnapDegrees = 3;

    public ToolCursor? Cursor => drag switch
    {
        2 => ToolCursor.Hand,
        TextDrag => ToolCursor.IBeam,
        RectDrag => ToolCursor.Cross,
        0 => hoverCursor ?? (overText ? ToolCursor.IBeam : null),
        _ => dragCursor
    };
    public bool IsActive => drag != 0;
    public Guid? PageId { get; private set; }
    public Guid? SelectedId => ids.Count == 1 ? ids.First() : null;
    public IReadOnlyCollection<Guid> SelectedIds => ids;
    public Annotation[] SelectedAnnotations => session()?.Current.AllAnnotations.Where(a => ids.Contains(a.Id) && !a.Locked).ToArray() ?? [];
    public StrokeAnnotation? Selected => SelectedAnnotations is [StrokeAnnotation s] ? s : null;
    public IReadOnlyList<Annotation> DisplayAnnotations => preview.Length > 0 ? preview : SelectedAnnotations;

    /// <summary>
    /// Dragging the selection onto another page is allowed now (spec 5.3, D14g: the setting is on and the view scrolls
    /// continuously). Null: never.
    /// </summary>
    public Func<bool>? AllowCrossPage { get; set; }

    /// <summary>While a move drags the selection over another page: that page and the annotations as they would land there.</summary>
    public (int PageIndex, Annotation[] Annotations)? CrossPage { get; private set; }
    public event EventHandler? SelectionChanged;

    /// <summary>A press of this tool ended; the argument is true when it changed the document (move, resize, rotate, line edit).</summary>
    public event EventHandler<bool>? Released;

    /// <summary>True when every selected annotation may be moved, scaled and rotated (no glyph highlight).</summary>
    public bool CanTransform => SelectedAnnotations.All(AnnotationGeometry.CanTransform);

    // ----- PDF text selection (spec 5.3, 17) -----

    public int TextSelectionPage => HasTextSelection ? textPage : -1;
    public bool HasTextSelection => textPage >= 0 && textAnchor != textFocus;
    public (int Start, int End) TextSelectionRange => (Math.Min(textAnchor, textFocus), Math.Max(textAnchor, textFocus));
    public string SelectedText => HasTextSelection && glyphs is not null ? TextSelection.TextOf(glyphs(textPage), TextSelectionRange.Start, TextSelectionRange.End) : "";

    /// <summary>Line quads of the selected glyphs on <paramref name="pageIndex"/> (empty elsewhere).</summary>
    public IReadOnlyList<Quad> TextSelectionQuads(int pageIndex) =>
        HasTextSelection && pageIndex == textPage && glyphs is not null ? TextSelection.LineQuads(glyphs(textPage), TextSelectionRange.Start, TextSelectionRange.End) : [];

    /// <summary>Selects the glyphs between two carets of a page (also used by the validation harness).</summary>
    public void SelectText(int pageIndex, int anchor, int focus)
    {
        SetSelection([]);
        textPage = pageIndex; textAnchor = anchor; textFocus = focus;
        view.RefreshOverlays();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearTextSelection()
    {
        if (textPage < 0) return;
        textPage = -1;
        view.RefreshOverlays();
    }

    /// <summary>Caret under a viewport point when PDF text is within <paramref name="toleranceDip"/>, with its page.</summary>
    public (int Page, int Caret)? TextCaretAt(Point2 viewportPoint, double toleranceDip = PickToleranceDip)
    {
        if (glyphs is null || view.HitTestPage(viewportPoint) is not { } anchor) return null;
        double tolerance = view.PageTransform(anchor.PageIndex).DipLengthToPoints(toleranceDip);
        return TextSelection.CaretAt(glyphs(anchor.PageIndex), anchor.PagePoint, tolerance) is { } caret ? (anchor.PageIndex, caret) : null;
    }

    public void PointerHover(Point2? position, InputModifiers modifiers)
    {
        hoverCursor = position is { } at ? CursorAt(at) ?? (OverLocked(at) ? ToolCursor.Lock(view.DpiScale) : null) : null;
        overText = hoverCursor is null && position is { } p && Pick(p) is null && TextCaretAt(p) is not null;
    }

    /// <summary>
    /// The cursor over the current selection: rotation or direction-aware resize on its handles, a cross on line
    /// points, the move cursor where a press moves it; null elsewhere.
    /// </summary>
    public ToolCursor? CursorAt(Point2 viewportPoint)
    {
        if (ids.Count == 0 || PageId is not { } pageId || session() is not { } s || s.Current.IndexOfPage(pageId) is not (>= 0 and var index)) return null;
        if (LineHandleAt(viewportPoint) >= 0) return ToolCursor.Cross;
        var transform = view.PageTransform(index);
        int handle = HandleAt(viewportPoint, transform);
        if (handle >= 0) return handle == 4 ? ToolCursor.Rotate(transform.DpiScale) : ResizeCursor(handle, transform);
        return HitSelection(viewportPoint) is SelectionHit.Annotation or SelectionHit.Frame && CanTransform ? ToolCursor.Move : null;
    }

    private int HandleAt(Point2 viewportPoint, ViewTransform transform)
    {
        var handles = HandlePoints(transform);
        for (int i = 0; i < handles.Length; i++)
            if (handles[i].DistanceTo(new(viewportPoint.X, viewportPoint.Y)) <= HandleRadiusDip) return i;
        return -1;
    }

    /// <summary>Resize cursor of corner (or text width) handle <paramref name="handle"/>: along the line from the frame center to it on screen.</summary>
    private ToolCursor ResizeCursor(int handle, ViewTransform transform)
    {
        if (SelectionFrame() is not { } f) return ToolCursor.SizeAll;
        var m = f.Frame.Then(transform.PageToDip);
        var center = m.Transform(new(f.Bounds.X + f.Bounds.Width / 2, f.Bounds.Y + f.Bounds.Height / 2));
        var at = HandlePoints(transform)[handle];
        return ToolCursor.Resize(new(at.X - center.X, at.Y - center.Y));
    }

    /// <summary>
    /// Rotation by <paramref name="delta"/> radians of a frame turned by <paramref name="original"/>: Shift keeps 15 degree
    /// steps of the final angle; otherwise a final angle within <see cref="RightAngleSnapDegrees"/> of a multiple of 90
    /// degrees becomes that multiple, unless Alt is held (spec 5.3). Returns the rotation to apply.
    /// </summary>
    public static double SnapRotation(double original, double delta, bool shift, bool alt)
    {
        double final = original + delta;
        if (shift) return Math.Round(final / (Math.PI / 12)) * Math.PI / 12 - original;
        if (alt) return delta;
        double right = Math.Round(final / (Math.PI / 2)) * Math.PI / 2;
        return Math.Abs(final - right) <= RightAngleSnapDegrees * Math.PI / 180 + 1e-9 ? right - original : delta;
    }

    public void Select(Guid? id) => SetSelection(id is { } value ? [value] : []);
    public void SetSelection(IEnumerable<Guid> values)
    {
        var current = session()?.Current;
        var locations = values.Distinct().Select(id => current?.FindAnnotation(id)).Where(l => l is { Annotation.Locked: false }).ToArray();
        PageId = locations.FirstOrDefault()?.Page.Id;
        ids.Clear();
        foreach (var l in locations.Where(l => l!.Page.Id == PageId)) ids.Add(l!.Annotation.Id);
        textPage = -1; // annotation and text selections are exclusive
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    public void Revalidate()
    {
        var valid = SelectedAnnotations.Select(a => a.Id).ToHashSet();
        if (!ids.SetEquals(valid)) SetSelection(valid);
    }
    /// <summary>Notes are hidden (spec 24.10): no annotation can be picked; PDF text selection still works.</summary>
    public bool IgnoreAnnotations { get; set; }

    /// <summary>True when the topmost annotation under the point is locked (spec 5.2: the lock cursor shows it).</summary>
    public bool OverLocked(Point2 viewportPoint) =>
        Pick(viewportPoint, includeLocked: true) is { } id && session()?.Current.FindAnnotation(id)?.Annotation.Locked == true;

    public Guid? Pick(Point2 viewportPoint, bool includeLocked = false)
    {
        if (IgnoreAnnotations || session() is not { } s || view.HitTestPage(viewportPoint) is not { } anchor) return null;
        var page = s.Current.Pages[anchor.PageIndex];
        double tolerance = view.PageTransform(anchor.PageIndex).DipLengthToPoints(PickToleranceDip);
        for (int i = page.Annotations.Count - 1; i >= 0; i--)
        {
            var a = page.Annotations[i];
            if ((includeLocked || !a.Locked) && AnnotationGeometry.Hit(a, anchor.PagePoint, tolerance, PdfNotes.Text.TextLayout.Bounds)) return a.Id;
        }
        return null;
    }
    public (Rect2 Bounds, Affine Frame)? SelectionFrame()
    {
        var items = DisplayAnnotations;
        if (items.Count == 0) return null;
        if (items.Count == 1) return (AnnotationGeometry.LocalBounds(items[0], PdfNotes.Text.TextLayout.Bounds), items[0].Transform);
        // Keep the group box in the first selected annotation's frame so the box and handles turn with the group.
        var groupFrame = Affine.Rotation(Math.Atan2(items[0].Transform.M12, items[0].Transform.M11));
        var inverse = groupFrame.Invert();
        var corners = items.SelectMany(a => AnnotationGeometry.Corners(AnnotationGeometry.LocalBounds(a, PdfNotes.Text.TextLayout.Bounds), a.Transform.Then(inverse))).ToArray();
        var groupBounds = new Rect2(corners.Min(p => p.X), corners.Min(p => p.Y),
            corners.Max(p => p.X) - corners.Min(p => p.X), corners.Max(p => p.Y) - corners.Min(p => p.Y));
        return (groupBounds, groupFrame);
    }
    /// <summary>Viewport DIP handles of a single selected line: Start, End, then its control points. Empty otherwise.</summary>
    public Point2[] LineHandlePoints(ViewTransform transform)
    {
        if (DisplayAnnotations is not [LineAnnotation line]) return [];
        var m = line.Transform.Then(transform.PageToDip);
        return [m.Transform(line.Start), m.Transform(line.End), .. line.Controls.Select(m.Transform)];
    }
    /// <summary>Index of the line handle under <paramref name="viewportPoint"/>, or -1. Control points win over ends when they overlap.</summary>
    public int LineHandleAt(Point2 viewportPoint)
    {
        if (PageId is not { } pageId || session() is not { } s || s.Current.IndexOfPage(pageId) is not (>= 0 and var index)) return -1;
        var handles = LineHandlePoints(view.PageTransform(index));
        for (int i = handles.Length - 1; i >= 0; i--)
            if (handles[i].DistanceTo(new(viewportPoint.X, viewportPoint.Y)) <= HandleRadiusDip) return i;
        return -1;
    }
    /// <summary>
    /// Moves one point of a line to a canonical page point. Shift on an end of a straight line keeps 15° steps
    /// relative to the other end (on screen the page turns by multiples of 90°, so the steps stay screen-aligned).
    /// </summary>
    public static LineAnnotation MoveLinePoint(LineAnnotation line, int handle, Point2 pagePoint, bool snap)
    {
        if (line.Locked) throw new AnnotationLockedException();
        var polygon = LineGeometry.ControlPolygon(line);
        int index = handle switch { 0 => 0, 1 => polygon.Length - 1, _ => handle - 1 };
        if (index < 0 || index >= polygon.Length) throw new ArgumentOutOfRangeException(nameof(handle));
        if (snap && line.LineKind == LineKind.Straight)
            pagePoint = SnapAngle(line.Transform.Transform(polygon[polygon.Length - 1 - index]), pagePoint);
        polygon[index] = line.Transform.Invert().Transform(pagePoint);
        return LineGeometry.WithPolygon(line, polygon);
    }
    public static Point2 SnapAngle(Point2 anchor, Point2 p)
    {
        double dx = p.X - anchor.X, dy = p.Y - anchor.Y, length = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 12)) * Math.PI / 12;
        return new(anchor.X + Math.Cos(angle) * length, anchor.Y + Math.Sin(angle) * length);
    }
    public Point2[] HandlePoints(ViewTransform transform)
    {
        // Glyph-bound marks never move; sticky notes only move (no size or rotate handles, spec 24.9).
        if (DisplayAnnotations is [LineAnnotation] || !DisplayAnnotations.All(AnnotationGeometry.CanTransform) || DisplayAnnotations.Any(AnnotationGeometry.MoveOnly)) return [];
        if (SelectionFrame() is not { } f) return [];
        var b = f.Bounds;
        var m = f.Frame.Then(transform.PageToDip);
        var corners = AnnotationGeometry.Corners(b, m);
        var top = m.Transform(new(b.X + b.Width / 2, b.Y));
        var center = m.Transform(new(b.X + b.Width / 2, b.Y + b.Height / 2));
        var direction = m.TransformVector(new(0, -1));
        double length = Math.Max(direction.DistanceTo(default), 1e-6);
        if (DisplayAnnotations is [TextBoxAnnotation])
        {
            corners[0] = m.Transform(new(b.X, b.Y + b.Height / 2)); corners[1] = m.Transform(new(b.X + b.Width, b.Y + b.Height / 2));
            corners[2] = new(double.MaxValue, double.MaxValue); corners[3] = new(double.MaxValue, double.MaxValue);
        }
        return [.. corners, new(top.X + direction.X / length * 24, top.Y + direction.Y / length * 24)];
    }
    /// <summary>What of the current selection is under <paramref name="viewportPoint"/>: a handle, a selected annotation or the inside of its frame.</summary>
    public SelectionHit HitSelection(Point2 viewportPoint)
    {
        if (ids.Count == 0 || PageId is not { } pageId || session() is not { } s || s.Current.IndexOfPage(pageId) is not (>= 0 and var index)) return SelectionHit.None;
        var p = new Point2(viewportPoint.X, viewportPoint.Y);
        var transform = view.PageTransform(index);
        if (LineHandleAt(viewportPoint) >= 0 || HandlePoints(transform).Any(h => h.DistanceTo(p) <= HandleRadiusDip)) return SelectionHit.Handle;
        if (Pick(viewportPoint) is { } id && ids.Contains(id)) return SelectionHit.Annotation;
        if (DisplayAnnotations is [LineAnnotation] || SelectionFrame() is not { } f) return SelectionHit.None;
        // The visible frame stays thin; its inside and a few screen pixels around it move the selection.
        var local = f.Frame.Invert().Transform(transform.DipToPage.Transform(p));
        double margin = transform.DipLengthToPoints(FrameMarginDip);
        return local.X >= f.Bounds.X - margin && local.X <= f.Bounds.X + f.Bounds.Width + margin &&
               local.Y >= f.Bounds.Y - margin && local.Y <= f.Bounds.Y + f.Bounds.Height + margin
            ? SelectionHit.Frame : SelectionHit.None;
    }

    /// <summary>Starts dragging the whole selection from <paramref name="viewportPoint"/> (inside its frame).</summary>
    public bool BeginMove(Point2 viewportPoint)
    {
        if (SelectedAnnotations.Length == 0 || !CanTransform) return false;
        start = viewportPoint;
        Begin(1);
        return true;
    }

    /// <summary>Starts a handle drag (resize or rotate) when <paramref name="viewportPoint"/> is on a selection handle.</summary>
    private bool BeginHandle(Point2 viewportPoint)
    {
        if (LineHandleAt(viewportPoint) is >= 0 and var lineHandle) { Begin(8 + lineHandle); dragCursor = ToolCursor.Cross; return true; }
        if (PageId is not { } pageId || session()?.Current.IndexOfPage(pageId) is not (>= 0 and var index)) return false;
        var transform = view.PageTransform(index);
        int handle = HandleAt(viewportPoint, transform);
        if (handle < 0) return false;
        var cursor = handle == 4 ? ToolCursor.Rotate(transform.DpiScale) : ResizeCursor(handle, transform);
        Begin(handle == 4 ? 3 : handle + 4);
        dragCursor = cursor;
        return true;
    }

    public bool PointerDown(CanvasPointer pointer)
    {
        if (session() is not { } s) return false;
        start = pointer.Position;
        if (BeginHandle(start)) return true;
        var picked = Pick(start);
        // Inside the frame or just around its border the selection moves, before PDF text selection or panning (spec 5.3).
        if (picked is null && !pointer.Modifiers.HasFlag(InputModifiers.Shift) && HitSelection(start) == SelectionHit.Frame && BeginMove(start)) return true;
        // Shift + a drag from where no annotation is: rectangle selection, added to the page's selection (spec 5.3, D14g).
        if (picked is null && pointer.Modifiers.HasFlag(InputModifiers.Shift) && !IgnoreAnnotations && view.HitTestPage(start) is { } corner)
        {
            textPage = -1;
            rectPage = corner.PageIndex; rectStart = rectEnd = corner.PagePoint; drag = RectDrag;
            view.SetPreview(rectPage, this);
            return true;
        }
        if (picked is { } id)
        {
            var page = s.Current.FindAnnotation(id)!.Page.Id;
            if (pointer.Modifiers.HasFlag(InputModifiers.Shift))
            {
                HashSet<Guid> next = PageId == page ? ids.ToHashSet() : [];
                if (!next.Add(id)) next.Remove(id);
                SetSelection(next);
                if (!ids.Contains(id)) return false;
            }
            else if (!ids.Contains(id)) Select(id);
            if (!CanTransform) return false; // selected, but glyph highlights stay where their text is
            Begin(1);
        }
        else if (TextCaretAt(start) is { } text)
        {
            Select(null);
            textPage = text.Page; textAnchor = textFocus = text.Caret; drag = TextDrag;
        }
        else { Select(null); drag = 2; panOffset = view.Offset; }
        return true;
    }
    private void Begin(int mode)
    {
        originals = SelectedAnnotations; preview = [];
        (bounds, frame) = SelectionFrame()!.Value; drag = mode;
        dragCursor = mode == 1 ? ToolCursor.Move : null;
    }
    public void PointerMove(CanvasPointer pointer)
    {
        if (drag == 0) return;
        if (drag == TextDrag)
        {
            var at = view.PageTransform(textPage).DipToPage.Transform(new(pointer.Position.X, pointer.Position.Y));
            if (glyphs is not null && TextSelection.CaretAt(glyphs(textPage), at) is { } caret && caret != textFocus) { textFocus = caret; view.RefreshOverlays(); }
            return;
        }
        if (drag == RectDrag)
        {
            rectEnd = view.PageTransform(rectPage).DipToPage.Transform(new(pointer.Position.X, pointer.Position.Y));
            view.RefreshPreview();
            return;
        }
        if (drag == 2)
        {
            view.SetHorizontalOffset(panOffset.X - (pointer.Position.X - start.X));
            view.SetVerticalOffset(panOffset.Y - (pointer.Position.Y - start.Y)); return;
        }
        int index = session()!.Current.IndexOfPage(PageId!.Value);
        var toPage = view.PageTransform(index).DipToPage;
        var a = toPage.Transform(new(start.X, start.Y));
        var b = toPage.Transform(new(pointer.Position.X, pointer.Position.Y));
        if (new Point2(start.X, start.Y).DistanceTo(new(pointer.Position.X, pointer.Position.Y)) < 2) { preview = []; CrossPage = null; view.RefreshOverlays(); return; }
        if (drag >= 8 && originals is [LineAnnotation line])
        {
            var grabbed = line.Transform.Transform(LineGeometry.ControlPolygon(line)[drag == 8 ? 0 : drag == 9 ? line.Controls.Length + 1 : drag - 9]);
            preview = [MoveLinePoint(line, drag - 8, new(grabbed.X + b.X - a.X, grabbed.Y + b.Y - a.Y), pointer.Modifiers.HasFlag(InputModifiers.Shift))];
        }
        else if (drag == 1)
        {
            CrossPage = AllowCrossPage?.Invoke() == true && view.HitTestPage(pointer.Position) is { } over && over.PageIndex != index &&
                MovedToPage(index, over.PageIndex, pointer.Position) is { } landed ? (over.PageIndex, landed) : null;
            preview = CrossPage is null ? originals.Select(x => AnnotationGeometry.Similarity(x, default, move: new(b.X - a.X, b.Y - a.Y))).ToArray() : [];
        }
        else if (drag == 3)
        {
            var center = frame.Transform(new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
            double angle = Math.Atan2(b.Y - center.Y, b.X - center.X) - Math.Atan2(a.Y - center.Y, a.X - center.X);
            // The preview and the committed rotation use the same snapped angle, so releasing never jumps.
            angle = SnapRotation(Math.Atan2(frame.M12, frame.M11), angle, pointer.Modifiers.HasFlag(InputModifiers.Shift), pointer.Modifiers.HasFlag(InputModifiers.Alt));
            preview = originals.Select(x => AnnotationGeometry.Similarity(x, center, angle: angle)).ToArray();
        }
        else
        {
            if (originals is [TextBoxAnnotation text])
            {
                var localPoint = text.Transform.Invert().Transform(b);
                bool left = drag == 4;
                double width = Math.Clamp(left ? text.WidthPt - localPoint.X : localPoint.X, 16, 100000);
                preview = [text with { WidthPt = width, Transform = left ? Affine.Translation(text.WidthPt - width, 0).Then(text.Transform) : text.Transform }];
                view.RefreshOverlays(); return;
            }
            var corners = AnnotationGeometry.Corners(bounds, Affine.Identity);
            var anchor = corners[(drag - 4 + 2) % 4];
            var local = frame.Invert().Transform(b);
            var startLocal = frame.Invert().Transform(a);
            double sx = Math.Clamp((local.X - anchor.X) / NonZero(startLocal.X - anchor.X), .01, 100);
            double sy = Math.Clamp((local.Y - anchor.Y) / NonZero(startLocal.Y - anchor.Y), .01, 100);
            if (originals is [ImageAnnotation image] && pointer.Modifiers.HasFlag(InputModifiers.Shift)) preview = [AnnotationGeometry.ResizeImage(image, anchor, sx, sy)];
            // A single shape stretches freely; Shift keeps its proportions (D11, like presentation apps).
            else if (originals is [ShapeAnnotation shape] && !pointer.Modifiers.HasFlag(InputModifiers.Shift)) preview = [AnnotationGeometry.ResizeShape(shape, anchor, sx, sy)];
            else
            {
                var initial = new Point2(startLocal.X - anchor.X, startLocal.Y - anchor.Y);
                double scale = Math.Clamp(((local.X - anchor.X) * initial.X + (local.Y - anchor.Y) * initial.Y) /
                    Math.Max(initial.X * initial.X + initial.Y * initial.Y, 1e-6), .01, 100);
                preview = originals.Select(x => AnnotationGeometry.Similarity(x, frame.Transform(anchor), scale)).ToArray();
            }
        }
        if (preview.Any(x => ModelValidator.ValidateAnnotation(x, session()!.Current.Resources.ContainsKey) is not null)) preview = [];
        view.RefreshOverlays();
    }
    private static double NonZero(double v) => Math.Abs(v) < 1e-6 ? 1e-6 : v;

    /// <summary>
    /// The selection moved by the drag from page <paramref name="from"/> onto page <paramref name="to"/>: where it shows on
    /// screen stays, in the other page's coordinates (its rotation included). Null when the two pages are not shown at one
    /// scale or a moved annotation would not be valid.
    /// </summary>
    private Annotation[]? MovedToPage(int from, int to, Point2 pointer)
    {
        var change = view.PageTransform(from).PageToDip.Then(Affine.Translation(pointer.X - start.X, pointer.Y - start.Y)).Then(view.PageTransform(to).DipToPage);
        double scale = Math.Sqrt(Math.Abs(change.M11 * change.M22 - change.M12 * change.M21));
        if (Math.Abs(scale - 1) > 1e-6) return null;
        var moved = originals.Select(x => AnnotationClipboard.Moved(x, change)).ToArray();
        return moved.Any(x => ModelValidator.ValidateAnnotation(x, session()!.Current.Resources.ContainsKey) is not null) ? null : moved;
    }
    public void PointerUp(CanvasPointer pointer)
    {
        if (drag == TextDrag)
        {
            PointerMove(pointer);
            drag = 0;
            if (!HasTextSelection) textPage = -1;
            view.RefreshOverlays();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Released?.Invoke(this, false);
            return;
        }
        if (drag == RectDrag)
        {
            PointerMove(pointer);
            drag = 0;
            view.SetPreview(-1, null);
            int page = rectPage; rectPage = -1;
            if (start.DistanceTo(pointer.Position) >= Lasso.MinDragDip && session() is { } current && page < current.Current.Pages.Count)
            {
                var model = current.Current;
                Point2[] path = [rectStart, new(rectEnd.X, rectStart.Y), rectEnd, new(rectStart.X, rectEnd.Y)];
                var hit = Lasso.Select(model.Pages[page], path, PdfNotes.Text.TextLayout.Bounds);
                if (hit.Count > 0) SetSelection(PageId == model.Pages[page].Id ? ids.Concat(hit).ToList() : hit);
            }
            Released?.Invoke(this, false);
            return;
        }
        PointerMove(pointer);
        var changes = preview; int mode = drag;
        var across = CrossPage;
        drag = 0; preview = []; originals = []; CrossPage = null;
        if (across is { } c && session() is { } s && PageId is { } fromPage)
        {
            // Dropped on another page (spec 5.3, D14g): the annotations move there and stay selected.
            var toPage = s.Current.Pages[c.PageIndex].Id;
            s.Execute(DocumentEdits.MoveToPage(s.Current, fromPage, toPage, c.Annotations));
            SetSelection(c.Annotations.Select(x => x.Id));
            view.RefreshOverlays();
            Released?.Invoke(this, true);
            return;
        }
        bool changed = changes.Length > 0 && session() is not null;
        if (changed) session()!.Execute(DocumentEdits.UpdateAnnotations(session()!.Current, changes, mode == 1 ? "undo.move" : mode == 3 ? "undo.rotate" : mode >= 8 ? "undo.editLine" : "undo.resize"));
        view.RefreshOverlays();
        Released?.Invoke(this, changed);
    }
    public void Cancel()
    {
        if (drag == TextDrag) textPage = -1;
        if (drag == RectDrag) { rectPage = -1; view.SetPreview(-1, null); }
        drag = 0; preview = []; originals = []; CrossPage = null; view.RefreshOverlays();
    }
    /// <summary>The rectangle of a Shift-drag selection in progress.</summary>
    IEnumerable<CanvasMark> ICanvasPreview.Marks(int pageIndex, Page page, ViewTransform transform)
    {
        if (drag != RectDrag || pageIndex != rectPage) yield break;
        yield return new LassoMark([rectStart, new(rectEnd.X, rectStart.Y), rectEnd, new(rectStart.X, rectEnd.Y)]);
    }

    public void Nudge(int x, int y, bool large)
    {
        if (PageId is not { } pageId || session() is not { } s || !CanTransform) return;
        var delta = view.PageTransform(s.Current.IndexOfPage(pageId)).DipToPage.TransformVector(new(x * (large ? 10 : 1) / view.DpiScale, y * (large ? 10 : 1) / view.DpiScale));
        s.Execute(DocumentEdits.UpdateAnnotations(s.Current, SelectedAnnotations.Select(a => AnnotationGeometry.Similarity(a, default, move: delta)), "undo.move"));
    }
}
