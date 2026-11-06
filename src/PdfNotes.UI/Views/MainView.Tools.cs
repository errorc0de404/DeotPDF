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
/// The canvas tools (roadmap P4 A3): the same Editor tools and the same wiring as the WPF window (MainWindow.Tools), with
/// the tool state in the view model (<see cref="ToolBarViewModel"/>, <see cref="ToolSwitch"/>): the canvas tool follows
/// <see cref="ToolBarViewModel.Current"/>, and the 필기 tab's buttons are bound to it (R4d). Here also the shape menu.
/// </summary>
public sealed partial class MainView
{
    private SelectTool selectTool = null!;
    private StrokeTool penTool = null!, highlighterStrokeTool = null!;
    private HighlighterTool highlighterTool = null!;
    private TextTool textTool = null!;
    private NoteTool noteTool = null!;
    private LineTool lineTool = null!, curveTool = null!;
    private LassoTool lassoTool = null!;
    private ShapeTool shapeTool = null!;
    private EraserTool strokeEraserTool = null!, areaEraserTool = null!;

    private ToolStylesViewModel styles => viewModel.ToolStyles;
    private ToolSwitch tools => viewModel.Tools;

    public ToolKind CurrentTool => tools.Current;
    public SelectTool Selection => selectTool;

    /// <summary>Switches the canvas tool (the tool row, shortcuts and tests use this one path; spec 5.6).</summary>
    public void SetTool(ToolKind kind) => viewModel.ToolBar.SetTool(kind);

    private void InitializeTools()
    {
        selectTool = new SelectTool(Canvas, () => Document?.Session, viewModel.PageGlyphs) { AllowCrossPage = () => settings.PageDrag && Canvas.Mode == ViewMode.Continuous };
        styles.Attach(selectTool);
        viewModel.Selection.Attach(selectTool, CheckImage);
        selectTool.SelectionChanged += (_, _) => Canvas.RefreshOverlays();
        penTool = new StrokeTool(Canvas, () => Document?.Session, () => styles.Pen, AnnotationKind.Ink, () => settings.PenPressure);
        highlighterStrokeTool = new StrokeTool(Canvas, () => Document?.Session, () => styles.Highlighter, AnnotationKind.FreeHighlight);
        textTool = new TextTool(Canvas, BeginNewTextAtClick, SelectTextAt);
        Action<Guid> SelectFrom(ToolKind tool) => id => Dispatcher.UIThread.Post(() => { SetTool(ToolKind.Select); selectTool.Select(id); tools.Return = tool; });
        lineTool = new LineTool(Canvas, () => Document?.Session, selectTool, () => styles.Line, () => styles.LineOptions, curve: false, SelectFrom(ToolKind.Line));
        curveTool = new LineTool(Canvas, () => Document?.Session, selectTool, () => styles.Line, () => styles.LineOptions, curve: true, SelectFrom(ToolKind.Curve));
        lassoTool = new LassoTool(Canvas, () => Document?.Session, selectTool);
        shapeTool = new ShapeTool(Canvas, () => Document?.Session, selectTool, () => settings.ShapeKind, () => styles.Shape, SelectFrom(ToolKind.Shape));
        noteTool = new NoteTool(Canvas, CreateNote, SelectNoteAt);
        var highlighterText = new TextHighlightTool(Canvas, () => Document?.Session, viewModel.PageGlyphs, () => styles.TextHighlightStyle, SelectFrom(ToolKind.FreeHighlight),
            () => styles.MarkKind is { } k ? (k, styles.MarkupStyle) : null);
        highlighterTool = new HighlighterTool(Canvas, () => Document?.Session, highlighterStrokeTool, highlighterText,
            () => settings.HighlighterSnap, () => styles.MarkKind is not null, SelectFrom(ToolKind.FreeHighlight));
        lassoTool.Selected += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (tools.Current != ToolKind.Lasso) return;
            SetTool(ToolKind.Select);
            tools.Return = ToolKind.Lasso;
        });
        // Spec 5.4 (D14g): after an automatic switch the original tool comes back when the selection is empty.
        selectTool.Released += TextBoxReleased;
        selectTool.Released += (_, _) =>
        {
            if (selectTool.SelectedIds.Count == 0 && !selectTool.HasTextSelection) ReturnAfterAutoSelect();
            else if (selectTool.HasTextSelection) tools.Return = null;
        };
        strokeEraserTool = new EraserTool(Canvas, () => Document?.Session, () => settings.StrokeEraserPt, EraserMode.Stroke);
        areaEraserTool = new EraserTool(Canvas, () => Document?.Session, () => settings.AreaEraserPt, EraserMode.Area);
        Canvas.MiddleButton = () => tools.Current is ToolKind.Pen or ToolKind.FreeHighlight ? (true, settings.QuickErase ? areaEraserTool : null) : (false, null);
        Canvas.PenEraser = () => settings.Eraser == ToolKind.StrokeEraser ? strokeEraserTool : areaEraserTool;
        Canvas.TouchOptions = () => new PointerRouterOptions { FingerDrawing = settings.FingerDrawing };
        Canvas.OverlayFor = OverlayFor;
        Canvas.MarksFor = MarksFor;
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
        Canvas.Tool = kind switch
        {
            ToolKind.Text => textTool,
            ToolKind.Select => selectTool,
            ToolKind.Pen => penTool,
            ToolKind.FreeHighlight => highlighterTool,
            ToolKind.Line => lineTool,
            ToolKind.Curve => curveTool,
            ToolKind.Lasso => lassoTool,
            ToolKind.StrokeEraser => strokeEraserTool,
            ToolKind.AreaEraser => areaEraserTool,
            ToolKind.Shape => shapeTool,
            ToolKind.Note => noteTool,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (!ToolSwitch.KeepsSelection(kind)) selectTool.Select(null);
    }

    private void ReturnAfterAutoSelect() => viewModel.ToolBar.ReturnAfterAutoSelect();

    // ----- the shape menu (spec 8) -----

    private void ShapeMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OpenShapeMenu();

    /// <summary>The shape button menu opened last (tests).</summary>
    public ContextMenu? LastShapeMenu { get; private set; }

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
    private AppIcon ToolIcon(ToolKind kind) => kind switch
    {
        ToolKind.Lasso => new AppIcon { Glyph = "Lasso" },
        ToolKind.Pen => new AppIcon { Glyph = "Pen" },
        ToolKind.FreeHighlight => new AppIcon { Glyph = "Highlighter" },
        ToolKind.StrokeEraser or ToolKind.AreaEraser => new AppIcon { Glyph = "Eraser" },
        ToolKind.Line or ToolKind.Curve => new AppIcon { Outline = AppIcon.ShapeOutline(kind, settings.ShapeKind) },
        _ => new AppIcon { Glyph = "Select" }
    };

    // ----- notes: the note tool (the card is MainView.Notes.cs) -----

    private bool SelectNoteAt(CanvasPointer pointer)
    {
        if (Document is null || Canvas.HitTestPage(pointer.Position) is not { } anchor) return false;
        var page = Document.Session.Current.Pages[anchor.PageIndex];
        if (page.Annotations.OfType<StickyNote>().LastOrDefault(n => AnnotationGeometry.Hit(n, anchor.PagePoint, 0)) is not { Locked: false } note) return false;
        SetTool(ToolKind.Select);
        tools.Return = ToolKind.Note;
        selectTool.Select(note.Id);
        return selectTool.PointerDown(pointer with { Modifiers = InputModifiers.None, ClickCount = 1 });
    }

    // ----- what the canvas draws: taken on the UI thread for each frame -----

    /// <summary>
    /// The annotation layer of a page as the WPF overlay shows it: the selection while it is moved or resized, an erase in
    /// progress, nothing while the notes are hidden, and not the text box being edited (its editor draws it).
    /// </summary>
    private PagePainter? OverlayFor(int index, Page page, ViewTransform transform)
    {
        if (viewModel.Display.NotesHidden || Document is not { } doc) return null;
        var replacements = selectTool.DisplayAnnotations.ToDictionary(a => a.Id);
        var erased = (Canvas.ActiveTool as EraserTool)?.Preview(page.Id);
        var across = selectTool.CrossPage;
        var leaving = across is not null && selectTool.PageId == page.Id ? selectTool.SelectedIds : null;
        var shown = new List<Annotation>(page.Annotations.Count);
        foreach (var original in page.Annotations)
        {
            if (original.Id == EditingTextId || leaving?.Contains(original.Id) == true) continue;
            if (erased is not null && erased.TryGetValue(original.Id, out var rest)) { shown.AddRange(rest); continue; }
            shown.Add(replacements.GetValueOrDefault(original.Id, original));
        }
        if (across is { } landing && landing.PageIndex == index) shown.AddRange(landing.Annotations);
        // A new sticky note is drawn while its card is open (it joins the document when closed with text).
        if (viewModel.NoteCard is { IsNew: true, Note: { } open } card && card.PageId == page.Id) shown.Add(open);
        var resources = doc.Session.Current.Resources;
        // The box being edited has its edit frame (the WPF overlay): a thin frame with a white halo when the box has no
        // border of its own, so it shows on dark pages too (D13c); a real border is the box's and the editor draws it.
        (Affine Transform, double Width, double Height)? editFrame = null;
        if (editSession is { } editing && editingPageId == page.Id && !editing.Model.Box.BoxStyle.BorderEnabled)
        {
            var box = editing.Model.DisplayBox;
            editFrame = (box.Transform, box.WidthPt, PdfNotes.TextEditor.Avalonia.SkiaMeasure.Layout(box).Height);
        }
        return (canvas, _, _, t) =>
        {
            foreach (var a in shown) SkiaAnnotationPainter.Paint(canvas, a, t, id => resources.GetValueOrDefault(id));
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

    /// <summary>Selection marks of a page (the WPF PaintMarks): text selection, then the frame, handles or line handles.</summary>
    private PagePainter? MarksFor(int index, Page page, ViewTransform transform)
    {
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
        if (quads is null && frame is null && hits.Count == 0 && drop is null) return null;
        return (canvas, _, _, t) =>
        {
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
