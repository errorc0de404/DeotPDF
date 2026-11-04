using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using Page = PdfNotes.Core.Models.Page;
using PdfNotes.Desktop.Canvas;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Session;
using PdfNotes.Editor.Tools;
using PdfNotes.Render.Skia;
using PdfNotes.Text;
using PdfNotes.ViewModels;
using SkiaSharp;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// The canvas tools (roadmap P4 A3): the same Editor tools and the same wiring as the WPF window (MainWindow.Tools), with
/// the tool state in the view model (<see cref="ToolBarViewModel"/>, <see cref="ToolSwitch"/>). A plain tool row stands in
/// for the ribbon until A5.
/// </summary>
public sealed partial class MainWindow : IToolView
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
    private readonly Dictionary<ToolKind, ToggleButton> toolButtons = [];

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
        viewModel.ToolBar.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ToolBarViewModel.Buttons)) ShowToolButtons(); };
        BuildToolRow();
        SetTool(ToolKind.Pen);
    }

    private static void CheckImage(byte[] bytes)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes)) ?? throw new InvalidDataException();
    }

    void IToolView.UseTool(ToolKind kind)
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

    void IToolView.ShowNotes() => viewModel.Display.NotesHidden = false;

    void IToolView.ReturnAfterAutoSelect() => ReturnAfterAutoSelect();

    private void ReturnAfterAutoSelect()
    {
        if (tools.TakeReturn() is not { } previous) return;
        Dispatcher.UIThread.Post(() => { if (tools.Current == ToolKind.Select && !Canvas.IsToolActive) SetTool(previous); });
    }

    // ----- the plain tool row (A3; the ribbon is A5) -----

    private void BuildToolRow()
    {
        Button Command(string name, string tip, Action run)
        {
            var button = new Button { Name = name, Content = tip, Margin = new(0, 0, 4, 0) };
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => run();
            ToolRow.Children.Add(button);
            return button;
        }
        Command("OpenButton", Strings.Get("ribbon.open"), () => _ = OpenWithDialogAsync());
        Command("SaveButton", Strings.Get("ribbon.save"), () => _ = SaveAsync(saveAs: false));
        Command("UndoButton", Strings.Get("ribbon.undoTip"), Undo);
        Command("RedoButton", Strings.Get("ribbon.redoTip"), Redo);
        foreach (var (kind, key) in new (ToolKind, string)[]
        {
            (ToolKind.Select, "tool.select"), (ToolKind.Pen, "tool.pen"), (ToolKind.FreeHighlight, "ribbon.highlighter"), (ToolKind.AreaEraser, "tool.areaEraser"),
            (ToolKind.StrokeEraser, "tool.strokeEraser"), (ToolKind.Lasso, "tool.lasso"), (ToolKind.Line, "tool.line"), (ToolKind.Curve, "tool.curve"),
            (ToolKind.Shape, "ribbon.shape"), (ToolKind.Text, "ribbon.text"), (ToolKind.Note, "ribbon.note"),
        })
        {
            var button = new ToggleButton { Name = kind + "ToolButton", Content = Strings.Get(key), Margin = new(0, 0, 2, 0) };
            button.Click += (_, _) => SetTool(kind);
            toolButtons[kind] = button;
            ToolRow.Children.Add(button);
        }
        ShowToolButtons();
    }

    private void ShowToolButtons()
    {
        foreach (var (kind, button) in toolButtons) button.IsChecked = tools.Current == kind;
    }

    private void UpdateCommands()
    {
        if (this.FindControl<Button>("UndoButton") is { } undo) undo.IsEnabled = Document?.Session.CanUndo == true;
        if (this.FindControl<Button>("RedoButton") is { } redo) redo.IsEnabled = Document?.Session.CanRedo == true;
    }

    public void Undo()
    {
        FinishTextEditing();
        Canvas.CancelTool();
        Document?.Session.Undo();
    }

    public void Redo()
    {
        FinishTextEditing();
        Canvas.CancelTool();
        Document?.Session.Redo();
    }

    // ----- notes (the card that types into them comes with A5) -----

    private void CreateNote(int pageIndex, Point2 point)
    {
        if (Document is not { } doc || viewModel.NoteCard.Create(pageIndex, point) is not { } note) return;
        doc.Session.Execute(DocumentEdits.AddAnnotation(doc.Session.Current, doc.Session.Current.Pages[pageIndex].Id, note));
    }

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
        var resources = doc.Session.Current.Resources;
        return (canvas, _, _, t) =>
        {
            foreach (var a in shown) SkiaAnnotationPainter.Paint(canvas, a, t, id => resources.GetValueOrDefault(id));
        };
    }

    /// <summary>Selection marks of a page (the WPF PaintMarks): text selection, then the frame, handles or line handles.</summary>
    private PagePainter? MarksFor(int index, Page page, ViewTransform transform)
    {
        var colors = Canvas.MarkColors;
        var quads = selectTool.TextSelectionPage == index ? selectTool.TextSelectionQuads(index).ToArray() : null;
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
        if (quads is null && frame is null) return null;
        return (canvas, _, _, t) =>
        {
            if (quads is not null) SkiaMarkPainter.PaintTextSelection(canvas, quads, t, colors);
            frame?.Invoke(canvas);
        };
    }
}
