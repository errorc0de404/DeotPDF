using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using Page = PdfNotes.Core.Models.Page;
using PdfNotes.UI.Canvas;
using PdfNotes.UI.Themes;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Session;
using PdfNotes.Editor.Tools;
using PdfNotes.Render.Skia;
using PdfNotes.Text;
using PdfNotes.ViewModels;
using SkiaSharp;

namespace PdfNotes.UI.Views;

/// <summary>
/// The canvas tools (roadmap P4 A3): the Editor tools wired to the canvas, with
/// the tool state in the view model (<see cref="ToolBarViewModel"/>, <see cref="ToolSwitch"/>): the canvas tool follows
/// <see cref="ToolBarViewModel.Current"/>, and the 필기 tab's buttons are bound to it (R4d). Here also the shape menu.
/// </summary>
public sealed partial class MainView
{
    private ToolSet toolSet = null!;
    private SelectTool selectTool => toolSet.Select;

    private ToolStylesViewModel styles => viewModel.ToolStyles;
    private ToolSwitch tools => viewModel.Tools;

    public ToolKind CurrentTool => tools.Current;
    public SelectTool Selection => selectTool;

    /// <summary>Switches the canvas tool (the tool row, shortcuts and tests use this one path; spec 5.6).</summary>
    public void SetTool(ToolKind kind) => viewModel.ToolBar.SetTool(kind);

    private void InitializeTools()
    {
        toolSet = new ToolSet(Canvas, () => Document?.Session, tools,
            new(viewModel.PageGlyphs, () => settings.PageDrag && Canvas.Mode == ViewMode.Continuous,
                () => styles.Pen, () => settings.PenPressure, () => styles.Highlighter, () => settings.HighlighterSnap, () => styles.TextHighlightStyle,
                () => (styles.MarkKind, styles.MarkupStyle), () => styles.Line, () => styles.LineOptions,
                () => settings.ShapeKind, () => styles.Shape, () => settings.StrokeEraserPt, () => settings.AreaEraserPt,
                () => viewModel.AiNotes.IsHidden, () => settings.ShapeRecognition),
            new(BeginNewTextAtClick, SelectTextAt, CreateNote, SelectNoteAt),
            SetTool, ReturnAfterAutoSelect, action => Dispatcher.UIThread.Post(action), changed => TextBoxReleased(null, changed), time);
        styles.Attach(selectTool);
        viewModel.Selection.Attach(selectTool, CheckImage);
        selectTool.SelectionChanged += (_, _) => Canvas.RefreshOverlays();
        Canvas.MiddleButton = () => tools.Current is ToolKind.Pen or ToolKind.FreeHighlight or ToolKind.Markup ? (true, settings.QuickErase ? toolSet.AreaEraser : null) : (false, null);
        Canvas.PenEraser = () => toolSet.Eraser(settings.Eraser);
        Canvas.TouchOptions = () => new PointerRouterOptions { FingerDrawing = settings.FingerDrawing };
        Canvas.OverlayFor = OverlayFor;
        Canvas.LiveIds = LiveIds;
        Canvas.LiveFor = LiveFor;
        Canvas.MarksFor = MarksFor;
        viewModel.LiveLink.Flashed += Flash;
        viewModel.AiNotes.Flashed += Flash;
        viewModel.ToolBar.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ToolBarViewModel.Current)) UseTool(viewModel.ToolBar.Current); };
        selectTool.SelectionChanged += (_, _) =>
        {
            styles.Refresh();
            if (selectTool.SelectedIds.Count > 0) viewModel.Ribbon.Auto(RibbonTab.Home);
        };
        // Drawing, typing or erasing on the canvas shows the 필기 tab; the selection tool's panning and PDF text
        // selection do not (spec 3.2 automatic tab switch).
        Canvas.ToolPressed += (_, _) => { if (Canvas.ActiveTool != selectTool || selectTool.SelectedIds.Count > 0) viewModel.Ribbon.Auto(RibbonTab.Home); };
        Canvas.TemporaryToolEnded += (_, _) => viewModel.ToolBar.Refresh();
        SetTool(ToolKind.Pen);
    }

    private static void CheckImage(byte[] bytes) => ImageFiles.Check(bytes);

    /// <summary>Puts the canvas tool for <paramref name="kind"/> in place (and clears a selection the tool does not keep).</summary>
    private void UseTool(ToolKind kind)
    {
        Canvas.Tool = toolSet.For(kind);
        if (!ToolSwitch.KeepsSelection(kind)) selectTool.Select(null);
    }

    private void ReturnAfterAutoSelect() => viewModel.ToolBar.ReturnAfterAutoSelect();

    // ----- the shape menu (spec 8) -----

    private void ShapeMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OpenShapeMenu();

    /// <summary>The shape button menu opened last (tests).</summary>
    internal ContextMenu? LastShapeMenu { get; private set; }

    /// <summary>The menu of the shape button (spec 8): line, curve, then the four shapes; the current one checked.</summary>
    public ContextMenu OpenShapeMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
        void Add(string header, ToolKind tool, ShapeKind shape, Action choose)
        {
            bool current = tools.Current == tool && (tool != ToolKind.Shape || settings.ShapeKind == shape);
            menu.Items.Add(Menus.Item(header, () => { choose(); Canvas.Focus(); }, isChecked: current, outline: AppIcon.ShapeOutline(tool, shape)));
        }
        Add(Strings.Get("tool.line"), ToolKind.Line, default, () => SetTool(ToolKind.Line));
        Add(Strings.Get("tool.curve"), ToolKind.Curve, default, () => SetTool(ToolKind.Curve));
        menu.Items.Add(new Separator());
        foreach (var (name, kind) in new[] { (Strings.Get("shape.rectangle"), ShapeKind.Rectangle), (Strings.Get("shape.ellipse"), ShapeKind.Ellipse), (Strings.Get("shape.triangle"), ShapeKind.Triangle), (Strings.Get("shape.polygon"), ShapeKind.Polygon) })
            Add(name, ToolKind.Shape, kind, () => viewModel.ToolBar.SetShapeKind(kind));
        menu.Open(ShapeToolButton);
        return LastShapeMenu = menu;
    }

    /// <summary>The ribbon icon of a tool (the quick menu, 2026-10-15).</summary>
    private object ToolIcon(ToolKind kind) => kind switch
    {
        ToolKind.Lasso => new AppIcon { Glyph = "Lasso" },
        ToolKind.Pen => new AppIcon { Glyph = "Pen" },
        ToolKind.FreeHighlight => new AppIcon { Glyph = "Highlighter" },
        ToolKind.Markup => new TextBlock { Text = styles.MarkKind == TextMarkupKind.StrikeOut ? "S" : "U", TextDecorations = styles.MarkKind == TextMarkupKind.StrikeOut ? Avalonia.Media.TextDecorations.Strikethrough : Avalonia.Media.TextDecorations.Underline, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Width = 16 },
        ToolKind.StrokeEraser or ToolKind.AreaEraser => new AppIcon { Glyph = "Eraser" },
        ToolKind.Line or ToolKind.Curve => new AppIcon { Outline = AppIcon.ShapeOutline(kind, settings.ShapeKind) },
        _ => new AppIcon { Glyph = "Select" }
    };

    // ----- notes: the note tool (the card is MainView.Notes.cs) -----

    private bool SelectNoteAt(CanvasPointer pointer)
    {
        if (Document is null || Canvas.HitTestPage(pointer.Position) is not { } anchor) return false;
        var page = Document.Session.Current.Pages[anchor.PageIndex];
        var model = Document.Session.Current;
        if (page.Annotations.OfType<StickyNote>().LastOrDefault(n => !viewModel.AiNotes.Hides(model, n) && AnnotationGeometry.Hit(n, anchor.PagePoint, 0)) is not { Locked: false } note) return false;
        SetTool(ToolKind.Select);
        tools.Return = ToolKind.Note;
        selectTool.Select(note.Id);
        return selectTool.PointerDown(pointer with { Modifiers = InputModifiers.None, ClickCount = 1 });
    }

    // ----- what the canvas draws: taken on the UI thread for each frame -----

    /// <summary>
    /// The annotation layer of a page: nothing while the notes are hidden, and not the text
    /// box being edited (its editor draws it). Annotations a drag is changing are left out here and drawn by
    /// <see cref="LiveFor"/>, so the canvas keeps the rest of the frame while they move (roadmap P5d A2).
    /// </summary>
    private PagePainter? OverlayFor(int index, Page page, ViewTransform transform, IReadOnlyCollection<Guid> live)
    {
        if (viewModel.Display.NotesHidden || Document is not { } doc) return null;
        var model = doc.Session.Current;
        var ai = viewModel.AiNotes;
        var shown = new List<Annotation>(page.Annotations.Count);
        foreach (var original in page.Annotations)
            if (original.Id != EditingTextId && !live.Contains(original.Id) && !ai.Hides(model, original)) shown.Add(original);
        // 강조 (MCP_LIVE_PLAN 6): a soft glow in the halo color under AI writing, and a thin edge on top of framed kinds.
        var halos = ai.IsEmphasized && !model.Tags.IsEmpty ? shown.Where(a => Authorship.IsAi(model, a.Id)).ToList() : null;
        var haloColor = new SKColor(ai.HaloColor.R, ai.HaloColor.G, ai.HaloColor.B);
        // A new sticky note is drawn while its card is open (it joins the document when closed with text).
        foreach (var card in viewModel.NoteCard.Cards)
            if (card.IsNew && card.PageId == page.Id) shown.Add(card.Note);
        // AI notes carry a small mark on the screen (spec 24.9).
        List<StickyNote> aiNotes = model.Tags.IsEmpty ? [] : [.. shown.OfType<StickyNote>().Where(n => Authorship.IsAi(model, n.Id))];
        var resources = doc.Session.Current.Resources;
        // The box being edited has its edit frame: a thin frame with a white halo when the box has no
        // border of its own, so it shows on dark pages too (D13c); a real border is the box's and the editor draws it.
        (Affine Transform, double Width, double Height)? editFrame = null;
        if (editSession is { } editing && editingPageId == page.Id && !editing.Model.Box.BoxStyle.BorderEnabled)
        {
            var box = editing.Model.DisplayBox;
            editFrame = (box.Transform, box.WidthPt, PdfNotes.TextEditor.Avalonia.SkiaMeasure.Layout(box).Height);
        }
        return (canvas, _, _, t) =>
        {
            if (halos is not null) foreach (var a in halos) SkiaAiHalo.PaintGlow(canvas, a, t, haloColor, PdfNotes.Text.TextLayout.Bounds);
            foreach (var a in shown) SkiaAnnotationPainter.Paint(canvas, a, t, id => resources.GetValueOrDefault(id));
            if (halos is not null) foreach (var a in halos) SkiaAiHalo.PaintEdge(canvas, a, t, haloColor, PdfNotes.Text.TextLayout.Bounds);
            foreach (var note in aiNotes) PaintAiBadge(canvas, note, t);
            if (editFrame is { } f)
            {
                var m = f.Transform.Then(t.PageToDip);
                using var path = new SKPath();
                path.AddPoly([.. new[] { new Point2(0, 0), new Point2(f.Width, 0), new Point2(f.Width, f.Height), new Point2(0, f.Height) }.Select(p => m.Transform(p)).Select(p => new SKPoint((float)p.X, (float)p.Y))], close: true);
                using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = SKColors.White }; // Overlay.Halo
                using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f, Color = new SKColor(0x64, 0x95, 0xED) }; // Overlay.TextEditFrame
                canvas.DrawPath(path, halo);
                canvas.DrawPath(path, line);
            }
        };
    }

    /// <summary>The annotations a drag is changing now: the selection being moved or resized, those an eraser is cutting.</summary>
    private IReadOnlyCollection<Guid> LiveIds()
    {
        if (selectTool.IsDragging) return selectTool.SelectedIds;
        // The page the eraser is on, not a search of every page (plan C6).
        if (Canvas.ActiveTool is EraserTool { PreviewPageId: { } pageId } eraser && eraser.Preview(pageId) is { Count: > 0 } erased)
            return [.. erased.Keys];
        return [];
    }

    /// <summary>The live annotations of a page as the drag shows them (moved copies, the pieces an eraser leaves).</summary>
    private PagePainter? LiveFor(int index, Page page, ViewTransform transform, IReadOnlyCollection<Guid> live)
    {
        if (viewModel.Display.NotesHidden || Document is not { } doc) return null;
        var replacements = selectTool.IsDragging ? selectTool.DisplayAnnotations.ToDictionary(a => a.Id) : [];
        var erased = (Canvas.ActiveTool as EraserTool)?.Preview(page.Id);
        var across = selectTool.CrossPage;
        bool leaving = across is not null && selectTool.PageId == page.Id;
        var shown = new List<Annotation>();
        foreach (var original in page.Annotations)
        {
            if (!live.Contains(original.Id) || original.Id == EditingTextId || leaving) continue;
            if (erased is not null && erased.TryGetValue(original.Id, out var rest)) shown.AddRange(rest);
            else shown.Add(replacements.GetValueOrDefault(original.Id, original));
        }
        if (across is { } landing && landing.PageIndex == index) shown.AddRange(landing.Annotations);
        if (shown.Count == 0) return null;
        var resources = doc.Session.Current.Resources;
        return (canvas, _, _, t) => { foreach (var a in shown) SkiaAnnotationPainter.Paint(canvas, a, t, id => resources.GetValueOrDefault(id)); };
    }

    /// <summary>Selection marks of a page: text selection, then the frame, handles or line handles.</summary>
    /// <summary>A place an AI client points at (MCP_LIVE_PLAN 3, <c>live flash</c>): marked for a moment like the current search result.</summary>
    private (int Page, Rect2 Rect)? flashed;
    private DispatcherTimer? flashTimer;

    private void Flash(int pageIndex, Rect2 rect)
    {
        flashed = (pageIndex, rect);
        flashTimer?.Stop();
        flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        flashTimer.Tick += (_, _) => { flashTimer?.Stop(); flashed = null; Canvas.RefreshOverlays(); };
        flashTimer.Start();
        Canvas.RefreshOverlays();
    }

    private PagePainter? MarksFor(int index, Page page, ViewTransform transform)
    {
        var flash = flashed is { } fl && fl.Page == index
            ? new Quad(new(fl.Rect.X, fl.Rect.Y), new(fl.Rect.X + fl.Rect.Width, fl.Rect.Y), new(fl.Rect.X, fl.Rect.Y + fl.Rect.Height), new(fl.Rect.X + fl.Rect.Width, fl.Rect.Y + fl.Rect.Height))
            : (Quad?)null;
        var colors = Canvas.MarkColors;
        var quads = selectTool.TextSelectionPage == index ? selectTool.TextSelectionQuads(index).ToArray() : null;
        // Search results (spec 12): temporary marks inside the page, the current one outlined.
        var search = viewModel.Search;
        var hits = search.HitsOn(page.Id);
        var matches = hits.Where(i => i != search.Index).SelectMany(i => search.Hits[i].Quads).ToArray();
        var current = hits.Contains(search.Index) ? search.Hits[search.Index].Quads.ToArray() : [];
        Action<SKCanvas>? frame = null;
        if (selectTool.CrossPage is null && selectTool.PageId == page.Id)
        {
            if ((!selectTool.CanTransform || selectTool.DisplayAnnotations.Any(AnnotationGeometry.MoveOnly)) && selectTool.SelectionFrame() is { } fixedFrame)
            {
                var corners = AnnotationGeometry.Corners(fixedFrame.Bounds, fixedFrame.Frame.Then(transform.PageToDip));
                frame = c => SkiaMarkPainter.PaintSelectionOutline(c, corners, colors);
            }
            else if (selectTool.DisplayAnnotations is [LineAnnotation])
            {
                var handles = selectTool.LineHandlePoints(transform);
                frame = c => SkiaMarkPainter.PaintLineHandles(c, handles, colors);
            }
            else
            {
                var handles = selectTool.HandlePoints(transform);
                Point2[]? frameCorners = selectTool.DisplayAnnotations is [TextBoxAnnotation] && selectTool.SelectionFrame() is { } f
                    ? AnnotationGeometry.Corners(f.Bounds, f.Frame.Then(transform.PageToDip)) : null;
                frame = c => SkiaMarkPainter.PaintSelectionFrame(c, handles, frameCorners, colors);
            }
        }
        var drop = DropMarkOn(index);
        if (quads is null && frame is null && hits.Count == 0 && drop is null && flash is null) return null;
        return (canvas, _, _, t) =>
        {
            if (flash is { } q) SkiaMarkPainter.PaintFlash(canvas, q, t, colors);
            if (hits.Count > 0)
            {
                canvas.Save();
                canvas.ClipRect(SKRect.Create((float)t.PageOriginDip.X, (float)t.PageOriginDip.Y, (float)t.PageWidthDip, (float)t.PageHeightDip));
                SkiaMarkPainter.PaintSearch(canvas, matches, current, t, colors);
                canvas.Restore();
            }
            if (drop is { } d) PaintDropMark(canvas, d);
            if (quads is not null) SkiaMarkPainter.PaintTextSelection(canvas, quads, t, colors);
            frame?.Invoke(canvas);
        };
    }
}
