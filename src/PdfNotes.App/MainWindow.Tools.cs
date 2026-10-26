using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PdfNotes.App.Canvas;
using PdfNotes.App.Services;
using PdfNotes.App.Tools;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.App.Themes;
using Page = PdfNotes.Core.Models.Page;
using DashStyle = PdfNotes.Core.Models.DashStyle;
using LineGeometry = PdfNotes.Core.Geometry.LineGeometry;

using PdfNotes.Editor.Session;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

/// <summary>Canvas tools and the contextual style bar (spec 3.1–3.2, 7.1–7.2, 9.1–9.2).</summary>
public partial class MainWindow
{
    private TextTool textTool = null!;
    private SelectTool selectTool = null!;
    private StrokeTool penTool = null!, highlighterStrokeTool = null!;
    private HighlighterTool highlighterTool = null!;
    private NoteTool noteTool = null!;
    private StrokeStyle underlineStyle = StrokeStyle.DefaultMarkup, strikeOutStyle = StrokeStyle.DefaultMarkup;
    private TextMarkupKind? markKind;
    private bool highlighterAuto = true;

    /// <summary>What the highlighter makes now (spec 9.1): null for highlights, or underline / strikeout.</summary>
    public TextMarkupKind? MarkKind => markKind;

    /// <summary>The style of the current underline or strikeout kind (each kind remembers its own).</summary>
    public StrokeStyle MarkupToolStyle => markKind == TextMarkupKind.StrikeOut ? strikeOutStyle : underlineStyle;

    /// <summary>Glyph highlights of the highlighter are the glyph height (on) or the width as height (off).</summary>
    public bool HighlighterAuto => highlighterAuto;

    /// <summary>The highlighter's 글자에 맞추기 toggle (spec 9.1, app setting).</summary>
    public bool HighlighterSnap => settings.HighlighterSnap;
    private LineTool lineTool = null!, curveTool = null!;
    private LassoTool lassoTool = null!;
    private ShapeTool shapeTool = null!;
    private ShapeStyle shapeStyle = ShapeStyle.Default;
    private bool shapeFillTarget = true;
    private ComboBox shapeBorderBox = null!;
    private RadioButton shapeFillButton = null!, shapeBorderButton = null!;
    private EraserTool strokeEraserTool = null!, areaEraserTool = null!;
    private StrokeStyle penStyle = StrokeStyle.DefaultPen, highlighterStyle = StrokeStyle.DefaultHighlighter, lineStyle = StrokeStyle.DefaultPen;
    private LineOptions lineOptions = LineOptions.Default;
    private ComboBox dashBox = null!, shapeBox = null!;
    private ToggleButton startArrowButton = null!, endArrowButton = null!;
    private List<RgbColor> recentColors = [];
    private bool syncingStyleBar, syncingTools;

    public ToolKind CurrentTool => tools.Current;

    /// <summary>The tool to go back to after the selection tool was entered automatically (lasso or highlight), else null.</summary>
    public ToolKind? ReturnTool => tools.Return;
    public SelectTool Selection => selectTool;
    public StrokeStyle PenStyle => penStyle;
    public StrokeStyle HighlighterStyle => highlighterStyle;
    public StrokeStyle LineStyle => lineStyle;
    public LineOptions LineToolOptions => lineOptions;

    /// <summary>
    /// The glyph highlight style of the highlighter (spec 9.1): the highlight kind's color and transparency, and either
    /// the glyph height or its width (1–80pt) as the height.
    /// </summary>
    public TextHighlightStyle TextHighlightToolStyle => new(highlighterStyle.ClampFor(AnnotationKind.PdfTextHighlight), highlighterAuto);
    public ShapeStyle ShapeToolStyle => shapeStyle;
    public ShapeKind ShapeToolKind => settings.ShapeKind;

    /// <summary>True when the palette and transparency edit the fill of shapes, false for their border.</summary>
    public bool ShapeFillTarget => shapeFillTarget;
    public double StrokeEraserDiameter => settings.StrokeEraserPt;
    public double AreaEraserDiameter => settings.AreaEraserPt;
    public IReadOnlyList<RgbColor> RecentColors => recentColors;

    private void InitializeTools()
    {
        penStyle = settings.Pen.ToStyle(StrokeStyle.DefaultPen, AnnotationKind.Ink);
        highlighterStyle = settings.Highlighter.ToStyle(StrokeStyle.DefaultHighlighter, AnnotationKind.FreeHighlight);
        recentColors = [.. settings.GetRecentColors()];
        lineStyle = settings.Line.ToStyle(StrokeStyle.DefaultPen, AnnotationKind.Line);
        lineOptions = settings.LineOptions;
        highlighterAuto = settings.HighlighterAuto;
        tools.ReturnChanged += (_, _) => MarkReturnTool();
        selectTool = new SelectTool(View, () => document?.Session, PageGlyphs) { AllowCrossPage = () => settings.PageDrag && View.Mode == ViewMode.Continuous };
        selectTool.SelectionChanged += (_, _) =>
        {
            View.RefreshOverlays();
            UpdateStyleBar();
            if (selectTool.SelectedIds.Count > 0) AutoRibbonTab(RibbonTab.Home);
        };
        // Drawing, typing or erasing on the canvas shows the 필기 tab; the selection tool's panning and PDF text
        // selection do not (spec 3.2 automatic tab switch).
        View.ToolPressed += (_, _) => { if (View.ActiveTool != selectTool || selectTool.SelectedIds.Count > 0) AutoRibbonTab(RibbonTab.Home); };
        penTool = new StrokeTool(View, () => document?.Session, () => penStyle, AnnotationKind.Ink);
        highlighterStrokeTool = new StrokeTool(View, () => document?.Session, () => highlighterStyle, AnnotationKind.FreeHighlight);
        textTool = new TextTool(View, BeginNewTextAtClick, SelectTextAt);
        // Clicking an existing line or shape with a shape group tool edits it with the selection tool and comes back
        // afterwards (spec 5.4, D12; lines and curves 2026-10-13 user request).
        Action<Guid> SelectFrom(ToolKind tool) => id => Dispatcher.BeginInvoke(() => { SetTool(ToolKind.Select); selectTool.Select(id); tools.Return = tool; });
        lineTool = new LineTool(View, () => document?.Session, selectTool, () => lineStyle, () => lineOptions, curve: false, SelectFrom(ToolKind.Line));
        curveTool = new LineTool(View, () => document?.Session, selectTool, () => lineStyle, () => lineOptions, curve: true, SelectFrom(ToolKind.Curve));
        lassoTool = new LassoTool(View, () => document?.Session, selectTool);
        shapeStyle = settings.Shape.ToStyle();
        shapeTool = new ShapeTool(View, () => document?.Session, selectTool, () => settings.ShapeKind, () => shapeStyle, SelectFrom(ToolKind.Shape));
        underlineStyle = settings.Underline?.ToStyle(StrokeStyle.DefaultMarkup, AnnotationKind.TextMarkup) ?? StrokeStyle.DefaultMarkup;
        strikeOutStyle = settings.StrikeOut?.ToStyle(StrokeStyle.DefaultMarkup, AnnotationKind.TextMarkup) ?? StrokeStyle.DefaultMarkup;
        markKind = settings.MarkKind;
        noteColor = RgbColor.TryParse(settings.NoteColor, out var savedNote) ? savedNote : noteColor;
        noteTool = new NoteTool(View, CreateNote, SelectNoteAt);
        // The one highlighter (spec 9, D14g): free strokes, glyph highlights in its own color, underline and strikeout.
        // Clicking an existing result edits it with the selection tool (spec 5.4).
        var highlighterText = new TextHighlightTool(View, () => document?.Session, PageGlyphs, () => TextHighlightToolStyle, SelectFrom(ToolKind.FreeHighlight),
            () => markKind is { } k ? (k, MarkupToolStyle) : null);
        highlighterTool = new HighlighterTool(View, () => document?.Session, highlighterStrokeTool, highlighterText,
            () => settings.HighlighterSnap, () => markKind is not null, SelectFrom(ToolKind.FreeHighlight));
        // Spec 5.4 (2026-10-12): a lasso result is edited with the selection tool; the selection is kept.
        lassoTool.Selected += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (tools.Current != ToolKind.Lasso) return;
            SetTool(ToolKind.Select);
            tools.Return = ToolKind.Lasso;
        });
        // Spec 5.4 (D14g): after such an automatic switch, the original tool comes back when the selection is empty; a
        // move, resize or style change keeps it selected for more editing.
        selectTool.Released += (_, changed) =>
        {
            // A second click on the text box selected from the text tool edits it there (spec 5.4, D12).
            if (!changed && reclickText is { } again && selectTool.SelectedIds.SequenceEqual([again.Id])) { reclickText = null; BeginTextEdit(again.Id, again.At); return; }
            // A click on a sticky note opens it (spec 24.9); a drag only moved it.
            if (!changed && selectTool.SelectedAnnotations is [StickyNote note] && selectTool.PageId is { } notePage)
            { reclickText = null; Dispatcher.BeginInvoke(() => OpenNote(note, notePage, isNew: false)); return; }
            reclickText = null;
            if (selectTool.SelectedIds.Count == 0 && !selectTool.HasTextSelection) ReturnAfterAutoSelect();
            else if (selectTool.HasTextSelection) tools.Return = null; // selecting PDF text is new work with the selection tool
        };
        strokeEraserTool = new EraserTool(View, () => document?.Session, () => settings.StrokeEraserPt, EraserMode.Stroke);
        areaEraserTool = new EraserTool(View, () => document?.Session, () => settings.AreaEraserPt, EraserMode.Area);
        // Spec 7.5: in the pen and the highlighter (any kind) the middle button is the area eraser while held (when
        // enabled); the toolbar and the style bar keep showing the drawing tool.
        View.MiddleButton = () => tools.Current is ToolKind.Pen or ToolKind.FreeHighlight ? (true, settings.QuickErase ? areaEraserTool : null) : (false, null);
        InitializeLineOptions();
        InitializeShapeOptions();
        InitializeTextEditing();
        View.OverlayPainter = PaintOverlay;
        View.MarksPainter = PaintMarks;
        InitializeSelectionCommands();
        foreach (var color in ColorPalette.Basic) PalettePanel.Children.Add(CreateSwatch(color, ColorName(color), SetColor));
        var (customButton, customPopup) = CustomColorEntry("CustomColor", () => StyleTarget?.Style.Color ?? penStyle.Color, SetColor);
        PaletteGroup.Children.Add(customButton);
        PaletteGroup.Children.Add(customPopup);
        InitializeEyedropper();
        BuildFavorites();
        SetTool(ToolKind.Pen);
    }

    /// <summary>Switches the canvas tool (toolbar, and later the quick menu, use this same path; spec 5.6).</summary>
    public void SetTool(ToolKind kind)
    {
        if (NotesHidden && kind != ToolKind.Select) SetNotesHidden(false); // a drawing tool shows the notes again (spec 24.10, D14g)
        bool hidden = NotesHidden;
        FinishTextEditing();
        kind = tools.Choose(kind, hidden);
        View.Tool = kind switch
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
        // Showing the state must not switch tools: Checked of the eraser mode buttons would select an eraser.
        syncingTools = true;
        try
        {
        TextToolButton.IsChecked = kind == ToolKind.Text;
        SelectToolButton.IsChecked = kind == ToolKind.Select;
        PenToolButton.IsChecked = kind == ToolKind.Pen;
        HighlighterToolButton.IsChecked = kind == ToolKind.FreeHighlight;
        ShapeToolButton.IsChecked = kind is ToolKind.Line or ToolKind.Curve or ToolKind.Shape;
        ShapeToolIcon.Content = ShapeIcon(settings.ShapeGroupTool, settings.ShapeKind);
        LassoToolButton.IsChecked = kind == ToolKind.Lasso;
        EraserToolButton.IsChecked = kind is ToolKind.StrokeEraser or ToolKind.AreaEraser;
        StrokeEraserButton.IsChecked = settings.Eraser == ToolKind.StrokeEraser;
        AreaEraserButton.IsChecked = settings.Eraser == ToolKind.AreaEraser;
        }
        finally { syncingTools = false; }
        UpdateStyleBar();
        AutoRibbonTab(RibbonTab.Home); // a tool change, also by shortcut or quick menu (spec 3.2)
    }

    /// <summary>
    /// Ends a selection-tool visit that started automatically (spec 5.4, D14g): when the selection became empty — a click
    /// on an empty place, Esc, Delete — the previous tool comes back. A selection tool the user chose keeps going.
    /// </summary>
    private void ReturnAfterAutoSelect()
    {
        if (tools.TakeReturn() is not { } previous) return;
        Dispatcher.BeginInvoke(() => { if (tools.Current == ToolKind.Select && !View.IsToolActive) SetTool(previous); });
    }

    /// <summary>The tool button an automatic selection visit goes back to has an accent border (spec 5.4, D14g).</summary>
    private void MarkReturnTool()
    {
        var target = tools.Return switch
        {
            ToolKind.Pen => PenToolButton,
            ToolKind.FreeHighlight => HighlighterToolButton,
            ToolKind.Text => TextToolButton,
            ToolKind.Lasso => LassoToolButton,
            ToolKind.Line or ToolKind.Curve or ToolKind.Shape => ShapeToolButton,
            ToolKind.Note => NoteToolButton,
            ToolKind.StrokeEraser or ToolKind.AreaEraser => EraserToolButton,
            _ => (System.Windows.Controls.Primitives.ButtonBase?)null
        };
        foreach (var button in new System.Windows.Controls.Primitives.ButtonBase[] { PenToolButton, HighlighterToolButton, TextToolButton, LassoToolButton, ShapeToolButton, NoteToolButton, EraserToolButton })
            Themes.ToolMarks.SetIsReturnTarget(button, ReferenceEquals(button, target));
    }

    /// <summary>Clicking the selection button, even while it is already on, is a direct choice: no automatic return.</summary>
    private void SelectToolClick(object sender, RoutedEventArgs e)
    {
        if (tools.Current == ToolKind.Select) tools.Return = null;
    }

    private void ToolChecked(object sender, RoutedEventArgs e)
    {
        if (syncingTools || sender is not FrameworkElement { Tag: string tag }) return;
        // The toolbar eraser button selects the eraser used last; the eraser bar switches between the two.
        var kind = tools.ForButton(tag);
        if (kind != tools.Current) SetTool(kind);
    }

    private void PaintOverlay(DrawingContext context, int pageIndex, Page page, ViewTransform transform)
    {
        var replacements = selectTool.DisplayAnnotations.ToDictionary(a => a.Id);
        var erased = (View.ActiveTool as EraserTool)?.Preview(page.Id); // also the quick erase of the middle button
        // A selection dragged onto another page (spec 5.3, D14g) shows there, not on its own page.
        var across = selectTool.CrossPage;
        var leaving = across is not null && selectTool.PageId == page.Id ? selectTool.SelectedIds : null;
        // Annotations are clipped to the page (spec 5.3); the selection handles drawn after the clip are not.
        context.PushClip(new RectangleGeometry(new Rect(0, 0, transform.PageWidthDip, transform.PageHeightDip)));
        PaintPageNumber(context, pageIndex, transform);
        foreach (var original in NotesHidden ? [] : page.Annotations)
        {
            if (textEditor is not null && original.Id == editingText?.Id) continue;
            if (leaving?.Contains(original.Id) == true) continue;
            if (erased is not null && erased.TryGetValue(original.Id, out var rest))
            {
                foreach (var piece in rest) AnnotationPainter.Paint(context, piece, transform); // eraser in progress
                continue;
            }
            var a = replacements.GetValueOrDefault(original.Id, original);
            if (a is ImageAnnotation image && document!.Session.Current.Resources.TryGetValue(image.ResourceId, out var resource))
                AnnotationPainter.PaintImage(context, image, resource, transform);
            else AnnotationPainter.Paint(context, a, transform);
        }
        if (across is { } landing && landing.PageIndex == pageIndex)
            foreach (var a in landing.Annotations)
                if (a is ImageAnnotation image && document!.Session.Current.Resources.TryGetValue(image.ResourceId, out var resource)) AnnotationPainter.PaintImage(context, image, resource, transform);
                else AnnotationPainter.Paint(context, a, transform);
        PaintOpenNote(context, page, transform);
        if (textEditor is not null && editingText is not null && editingPage == pageIndex)
        {
            var box = ReadEditor(); var b = PdfNotes.Text.TextLayout.Bounds(box); var m = box.Transform.Then(transform.PageToDip);
            context.PushTransform(new MatrixTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY)));
            context.DrawDrawing(PdfNotes.Text.WpfText.Measure(box).Highlights); // the live editor draws only the text
            var frameRect = new Rect(0, 0, b.Width, b.Height);
            // The edit frame of a borderless box has a white halo so it shows on dark pages too (D13c); a real border is document data.
            if (!box.BoxStyle.BorderEnabled) context.DrawRectangle(null, new Pen(Theme.Brush("Overlay.Halo"), 1.5), frameRect);
            context.DrawRectangle(PdfNotes.Text.WpfText.Brush(box.BoxStyle.BackgroundColor, box.BoxStyle.BackgroundOpacity),
                new Pen(box.BoxStyle.BorderEnabled ? PdfNotes.Text.WpfText.Brush(box.BoxStyle.BorderColor, box.BoxStyle.BorderOpacity) : Theme.Brush("Overlay.TextEditFrame"),
                    box.BoxStyle.BorderEnabled ? 1 : .5), frameRect);
            context.Pop();
        }
        context.Pop();
    }

    /// <summary>Screen marks above the page content (never inverted by the reading dark page, spec 24.12; never saved).</summary>
    private void PaintMarks(DrawingContext context, int pageIndex, Page page, ViewTransform transform)
    {
        context.PushClip(new RectangleGeometry(new Rect(0, 0, transform.PageWidthDip, transform.PageHeightDip)));
        PaintSearch(context, page, transform); // temporary, inside the page clip
        context.Pop();
        if (selectTool.TextSelectionPage == pageIndex) AnnotationPainter.PaintTextSelection(context, selectTool.TextSelectionQuads(pageIndex), transform);
        if (selectTool.CrossPage is not null) return; // no frame while the selection is over another page
        if (selectTool.PageId == page.Id && (!selectTool.CanTransform || selectTool.DisplayAnnotations.Any(AnnotationGeometry.MoveOnly)) && selectTool.SelectionFrame() is { } fixedFrame)
            AnnotationPainter.PaintSelectionOutline(context, AnnotationGeometry.Corners(fixedFrame.Bounds, fixedFrame.Frame.Then(transform.PageToDip)));
        else if (selectTool.PageId == page.Id && selectTool.DisplayAnnotations is [LineAnnotation])
            AnnotationPainter.PaintLineHandles(context, selectTool.LineHandlePoints(transform));
        else if (selectTool.PageId == page.Id)
        {
            Point2[]? frameCorners = selectTool.DisplayAnnotations is [TextBoxAnnotation] && selectTool.SelectionFrame() is { } f
                ? AnnotationGeometry.Corners(f.Bounds, f.Frame.Then(transform.PageToDip)) : null;
            AnnotationPainter.PaintSelectionFrame(context, selectTool.HandlePoints(transform), frameCorners);
        }
    }

    private Typeface? pageNumberFace;
    private PageNumberSize pageNumberSize = PageNumberSize.Normal;

    /// <summary>Page number size in DIP (2026-10-15: small 11 / normal 14 / large 18; the token FontSize.PageNumber was 11).</summary>
    public double PageNumberDip => pageNumberSize switch { PageNumberSize.Small => 11, PageNumberSize.Large => 18, _ => 14 };

    /// <summary>
    /// The page's number at the bottom-right of the displayed paper (spec 4.5): faint, under the annotations, the same size
    /// at every zoom. Only on screen: saving, Flatten, backups and thumbnails never draw this overlay.
    /// </summary>
    private void PaintPageNumber(DrawingContext context, int pageIndex, ViewTransform transform)
    {
        if (!showPageNumbers) return;
        pageNumberFace ??= new Typeface((FontFamily)FindResource("Font.Ui"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double size = PageNumberDip, margin = (double)FindResource("Space.PageNumber");
        var text = new FormattedText((pageIndex + 1).ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            pageNumberFace, size, Theme.Brush("Overlay.PageNumber"), transform.DpiScale);
        context.DrawText(text, new Point(transform.PageWidthDip - margin - text.Width, transform.PageHeightDip - margin - text.Height));
    }

    private void DeleteSelection()
    {
        if (document is null || selectTool.SelectedIds.Count == 0) return;
        View.CancelTool();
        document.Session.Execute(DocumentEdits.DeleteAnnotations(document.Session.Current, selectTool.SelectedIds));
        ReturnAfterAutoSelect();
    }

    // ----- style target: the active drawing tool, or the selected stroke/line -----

    /// <summary>The single selected, unlocked line (also the line just drawn with a line tool).</summary>
    public LineAnnotation? SelectedLine => selectTool.SelectedAnnotations is [LineAnnotation line] ? line : null;

    /// <summary>The single selected, unlocked PDF glyph highlight.</summary>
    public PdfTextHighlight? SelectedTextHighlight => selectTool.SelectedAnnotations is [PdfTextHighlight h] ? h : null;

    /// <summary>
    /// The selected annotations the style bar edits together (spec 3.1, 9.4): every unlocked selected one, when all of
    /// them have a color, width and transparency (strokes, lines, glyph highlights). A selection that also holds text
    /// boxes or images offers only the commands they share, so the bar is off.
    /// </summary>
    public Annotation[] StyleSelection => tools.Current is ToolKind.Select or ToolKind.Lasso && selectTool.SelectedAnnotations is { Length: > 0 } all &&
        all.All(ToolStyles.HasStrokeStyle) ? all : [];

    private StrokeStyle StyleOf(Annotation a) => ToolStyles.StyleOf(a, shapeFillTarget);

    /// <summary>The single selected, unlocked shape (also the shape just drawn with the shape tool).</summary>
    public ShapeAnnotation? SelectedShape => selectTool.SelectedAnnotations is [ShapeAnnotation shape] ? shape : null;

    /// <summary>The fill or the border of a shape style as the color/width/transparency the bar shows (width: the border).</summary>
    private StrokeStyle ShapeStroke(ShapeStyle s) => ToolStyles.ShapeStroke(s, shapeFillTarget);

    /// <summary>Writes a bar change back to the fill or the border (<see cref="ToolStyles.WithStroke"/>).</summary>
    private ShapeStyle WithStroke(ShapeStyle s, StrokeStyle stroke) => ToolStyles.WithStroke(s, stroke, shapeFillTarget);

    /// <summary>Which values differ across a multi-selection: the bar shows them empty (mixed) until one is set for all.</summary>
    public (bool Color, bool Width, bool Transparency) StyleMixed => ToolStyles.Mixed(StyleSelection.Select(StyleOf).ToArray());

    /// <summary>
    /// The style the bar shows and edits, or null when there is nothing to style (select tool, no selection).
    /// With a line tool the bar shows the line just drawn while it is selected; edits then change it and the tool.
    /// </summary>
    public (StrokeStyle Style, AnnotationKind Kind)? StyleTarget => tools.Current switch
    {
        ToolKind.Pen => (penStyle, AnnotationKind.Ink),
        ToolKind.FreeHighlight => markKind is null ? (highlighterStyle, AnnotationKind.FreeHighlight) : (MarkupToolStyle, AnnotationKind.TextMarkup),
        ToolKind.Line or ToolKind.Curve => (SelectedLine is { } l ? StrokeStyle.Of(l) : lineStyle, AnnotationKind.Line),
        ToolKind.Note => (new StrokeStyle(noteColor, StrokeStyle.DefaultPen.WidthPt, 0), AnnotationKind.Note),
        ToolKind.Shape => (ShapeStroke(SelectedShape?.Style ?? shapeStyle), AnnotationKind.Shape),
        _ => StyleSelection is [var first, ..] ? (StyleOf(first), first.Kind) : null
    };

    /// <summary>Applies a color to the target. Colors outside the basic palette become user colors.</summary>
    public void SetColor(RgbColor color)
    {
        RememberColor(color);
        ApplyStyle(s => s with { Color = color }, a => a with { Color = color }, l => l with { Color = color }, null, h => h with { Color = color });
    }

    /// <summary>Applies a width. <paramref name="continuous"/> changes (slider drag) merge into one Undo unit until committed.</summary>
    public void SetWidth(double widthPt, bool continuous = false)
    {
        if (!double.IsFinite(widthPt)) return;
        ApplyStyle(s => s with { WidthPt = widthPt }, a => a.IsOutlined ? a : a with { WidthPt = ClampWidth(widthPt, a.Kind) }, l => l with { WidthPt = ClampWidth(widthPt, l.Kind) },
            continuous ? "width" : null, h => h with { HeightMode = HighlightHeightMode.Manual, ManualHeightPt = ClampWidth(widthPt, AnnotationKind.PdfTextHighlight) });
    }

    public void SetTransparency(double percent, bool continuous = false)
    {
        if (!double.IsFinite(percent)) return;
        percent = Math.Clamp(percent, 0, 100);
        double opacity = PdfNotes.Core.Models.Opacity.FromTransparencyPercent(percent);
        ApplyStyle(s => s with { TransparencyPercent = percent }, a => a with { Opacity = opacity }, l => l with { Opacity = opacity },
            continuous ? "transparency" : null, h => h with { Opacity = opacity });
    }

    /// <summary>Ends a slider drag: the merged change becomes one Undo unit and tool settings are stored.</summary>
    public void CommitStyleChange()
    {
        document?.Session.SealCoalescing();
        settings.Save();
    }

    private static double ClampWidth(double width, AnnotationKind kind) => ToolStyles.ClampWidth(width, kind);

    /// <summary>Glyph highlight height: automatic (glyph height) or the width as the manual height (spec 9.1).</summary>
    public void SetHighlightAuto(bool auto)
    {
        if (tools.Current == ToolKind.FreeHighlight && markKind is null)
        {
            highlighterAuto = auto;
            settings.HighlighterAuto = auto;
            settings.Save();
        }
        else if (StyleSelection is { Length: > 0 } targets && targets.All(a => a is PdfTextHighlight))
        {
            UpdateSelected(targets, targets.Cast<PdfTextHighlight>().Select(h => (Annotation)(auto ? h with { HeightMode = HighlightHeightMode.Auto, ManualHeightPt = null }
                : h with { HeightMode = HighlightHeightMode.Manual, ManualHeightPt = ClampWidth(StrokeStyle.Of(h).WidthPt, AnnotationKind.PdfTextHighlight) })).ToArray(), null);
        }
        UpdateStyleBar();
    }

    private void HighlightAutoClick(object sender, RoutedEventArgs e) => SetHighlightAuto(HighlightAutoButton.IsChecked == true);

    private void MarkKindChecked(object sender, RoutedEventArgs e)
    {
        if (syncingStyleBar || sender is not FrameworkElement { Tag: string tag }) return;
        SetMarkKind(tag switch { "Underline" => TextMarkupKind.Underline, "StrikeOut" => TextMarkupKind.StrikeOut, _ => null });
    }

    /// <summary>Chooses what the highlighter makes: a highlight, an underline or a strikeout (spec 9.1). App setting.</summary>
    public void SetMarkKind(TextMarkupKind? kind)
    {
        markKind = kind;
        settings.MarkKind = kind;
        settings.Save();
        UpdateStyleBar();
    }

    /// <summary>The highlighter's 글자에 맞추기 toggle (spec 9.1): highlight strokes that start on text snap to it. App setting.</summary>
    public void SetHighlighterSnap(bool on)
    {
        settings.HighlighterSnap = on;
        settings.Save();
        UpdateStyleBar();
    }

    private void HighlighterSnapClick(object sender, RoutedEventArgs e) => SetHighlighterSnap(HighlighterSnapButton.IsChecked == true);

    private void ApplyStyle(Func<StrokeStyle, StrokeStyle> toolChange, Func<StrokeAnnotation, StrokeAnnotation> strokeChange,
        Func<LineAnnotation, LineAnnotation> lineChange, string? coalescePart, Func<PdfTextHighlight, PdfTextHighlight>? highlightChange = null)
    {
        switch (tools.Current)
        {
            case ToolKind.FreeHighlight when markKind == TextMarkupKind.StrikeOut:
                strikeOutStyle = toolChange(strikeOutStyle).ClampFor(AnnotationKind.TextMarkup);
                settings.StrikeOut = AppSettings.StyleSettings.From(strikeOutStyle);
                break;
            case ToolKind.FreeHighlight when markKind == TextMarkupKind.Underline:
                underlineStyle = toolChange(underlineStyle).ClampFor(AnnotationKind.TextMarkup);
                settings.Underline = AppSettings.StyleSettings.From(underlineStyle);
                break;
            case ToolKind.Note:
                noteColor = toolChange(new StrokeStyle(noteColor, StrokeStyle.DefaultPen.WidthPt, 0)).Color;
                settings.NoteColor = noteColor.ToHex();
                break;
            case ToolKind.Pen:
                penStyle = toolChange(penStyle).ClampFor(AnnotationKind.Ink);
                settings.Pen = AppSettings.StyleSettings.From(penStyle);
                break;
            case ToolKind.FreeHighlight:
                highlighterStyle = toolChange(highlighterStyle).ClampFor(AnnotationKind.FreeHighlight);
                settings.Highlighter = AppSettings.StyleSettings.From(highlighterStyle);
                break;
            case ToolKind.Shape:
                shapeStyle = WithStroke(shapeStyle, toolChange(ShapeStroke(shapeStyle)));
                settings.Shape = AppSettings.ShapeSettings.From(shapeStyle);
                if (SelectedShape is { } drawnShape) UpdateSelected(drawnShape, drawnShape with { Style = WithStroke(drawnShape.Style, toolChange(ShapeStroke(drawnShape.Style))) }, coalescePart);
                break;
            case ToolKind.Line or ToolKind.Curve:
                lineStyle = toolChange(lineStyle).ClampFor(AnnotationKind.Line);
                settings.Line = AppSettings.StyleSettings.From(lineStyle);
                if (SelectedLine is { } drawn) UpdateSelected(drawn, lineChange(drawn), coalescePart);
                break;
            default:
                var targets = StyleSelection;
                if (targets.Length == 0) return;
                UpdateSelected(targets, targets.Select(a => a switch
                {
                    StrokeAnnotation s => strokeChange(s),
                    LineAnnotation l => lineChange(l),
                    PdfTextHighlight h => highlightChange?.Invoke(h) ?? h,
                    PdfTextMarkup m => toolChange(StrokeStyle.Of(m)).ClampFor(AnnotationKind.TextMarkup) is var ms ? m with { Color = ms.Color, WidthPt = ms.WidthPt, Opacity = ms.Opacity } : m,
                    StickyNote n => n with { Color = toolChange(StyleOf(n)).Color },
                    ShapeAnnotation sh => sh with { Style = WithStroke(sh.Style, toolChange(ShapeStroke(sh.Style))) },
                    _ => a
                }).ToArray(), coalescePart);
                break;
        }
        if (coalescePart is null) settings.Save();
        UpdateStyleBar();
    }

    /// <summary>Replaces one selected annotation; slider drags (<paramref name="coalescePart"/>) merge into one Undo unit.</summary>
    private void UpdateSelected(Annotation before, Annotation after, string? coalescePart, string label = "undo.changeStyle") =>
        UpdateSelected([before], [after], coalescePart, label);

    /// <summary>Replaces selected annotations together (one Undo unit for the whole selection).</summary>
    private void UpdateSelected(IReadOnlyList<Annotation> before, IReadOnlyList<Annotation> after, string? coalescePart, string label = "undo.changeStyle")
    {
        if (document is not { } doc || before.Count == 0) return;
        var changed = after.Where((a, i) => !a.Equals(before[i])).ToArray();
        if (changed.Length > 0)
            doc.Session.Execute(DocumentEdits.UpdateAnnotations(doc.Session.Current, changed, label,
                coalescePart is null ? null : $"style:{string.Join(",", before.Select(a => a.Id))}:{coalescePart}"));
        if (coalescePart is null) doc.Session.SealCoalescing();
    }

    /// <summary>The lines the line options edit: the line just drawn with a line tool, or a selection of lines only.</summary>
    private LineAnnotation[] OptionLines => tools.Current is ToolKind.Line or ToolKind.Curve ? SelectedLine is { } l ? [l] : []
        : StyleSelection is { Length: > 0 } s && s.All(a => a is LineAnnotation) ? s.Cast<LineAnnotation>().ToArray() : [];

    // ----- shapes: fill/border target, border style (spec 8 preface, D11) -----

    /// <summary>The shapes the shape options edit: the shape just drawn with the shape tool, or a selection of shapes only.</summary>
    private ShapeAnnotation[] OptionShapes => tools.Current == ToolKind.Shape ? SelectedShape is { } s ? [s] : []
        : StyleSelection is { Length: > 0 } all && all.All(a => a is ShapeAnnotation) ? all.Cast<ShapeAnnotation>().ToArray() : [];

    private static readonly string[] BorderNames = [Strings.Get("border.none"), Strings.Get("dash.solid"), Strings.Get("dash.dashed"), Strings.Get("dash.dotted"), Strings.Get("dash.dashDot")];

    private void InitializeShapeOptions()
    {
        shapeFillButton = new RadioButton { Content = Strings.Get("shape.fill"), GroupName = "ShapeTarget", Style = (Style)FindResource("ToolButton"), IsChecked = true };
        shapeBorderButton = new RadioButton { Content = Strings.Get("shape.border"), GroupName = "ShapeTarget", Style = (Style)FindResource("ToolButton") };
        shapeFillButton.Checked += (_, _) => { if (!syncingStyleBar) SetShapeColorTarget(true); };
        shapeBorderButton.Checked += (_, _) => { if (!syncingStyleBar) SetShapeColorTarget(false); };
        ShapeOptionsPanel.Children.Add(shapeFillButton);
        ShapeOptionsPanel.Children.Add(shapeBorderButton);
        shapeBorderBox = new ComboBox { Width = 92, Margin = new Thickness(8, 0, 0, 0), ItemsSource = BorderNames, VerticalAlignment = VerticalAlignment.Center, ToolTip = Strings.Get("shape.borderStyle") };
        shapeBorderBox.SelectionChanged += (_, _) =>
        {
            if (!syncingStyleBar && shapeBorderBox.SelectedIndex >= 0) SetShapeBorder(shapeBorderBox.SelectedIndex == 0 ? null : (DashStyle)(shapeBorderBox.SelectedIndex - 1));
        };
        ShapeOptionsPanel.Children.Add(shapeBorderBox);
        ShapeOptionsPanel.Children.Add(new Separator()); // the palette follows (target first, like the text bar)
    }

    /// <summary>Chooses what the palette and transparency edit for shapes: the fill or the border (not document state).</summary>
    public void SetShapeColorTarget(bool fill)
    {
        shapeFillTarget = fill;
        UpdateStyleBar();
    }

    /// <summary>The border of the shape tool and of the shapes it edits: null removes it (kept when there is no fill), else its dash.</summary>
    public void SetShapeBorder(DashStyle? dash)
    {
        ShapeStyle Apply(ShapeStyle s) => dash is { } d ? s with { BorderEnabled = true, BorderDash = d } : s.FillOpacity > 0 ? s with { BorderEnabled = false } : s;
        if (tools.Current == ToolKind.Shape)
        {
            shapeStyle = Apply(shapeStyle);
            settings.Shape = AppSettings.ShapeSettings.From(shapeStyle);
            settings.Save();
        }
        if (OptionShapes is { Length: > 0 } shapes)
        {
            UpdateSelected(shapes, shapes.Select(s => (Annotation)(s with { Style = Apply(s.Style) })).ToArray(), null);
        }
        UpdateStyleBar();
    }

    /// <summary>Chooses the shape the shape tool draws next and switches to it (shape button menu).</summary>
    public void SetShapeKind(ShapeKind kind)
    {
        if (kind == ShapeKind.Polyline) throw new ArgumentOutOfRangeException(nameof(kind));
        settings.ShapeKind = kind;
        settings.Save();
        SetTool(ToolKind.Shape);
    }

    private void UpdateShapeOptions()
    {
        var shapes = OptionShapes;
        bool visible = tools.Current == ToolKind.Shape || shapes.Length > 0;
        ShapeOptionsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        bool wasSyncing = syncingStyleBar;
        syncingStyleBar = true;
        try
        {
            shapeFillButton.IsChecked = shapeFillTarget;
            shapeBorderButton.IsChecked = !shapeFillTarget;
            var styles = shapes.Length > 0 ? shapes.Select(s => s.Style).ToArray() : [shapeStyle];
            int Index(ShapeStyle s) => s.BorderEnabled ? 1 + (int)s.BorderDash : 0;
            shapeBorderBox.SelectedIndex = styles.All(s => Index(s) == Index(styles[0])) ? Index(styles[0]) : -1;
        }
        finally { syncingStyleBar = wasSyncing; }
    }

    private void ShapeMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ShapeToolButton, Placement = PlacementMode.Bottom };
        void Add(string header, ToolKind tool, ShapeKind shape, Action choose)
        {
            bool current = tools.Current == tool && (tool != ToolKind.Shape || settings.ShapeKind == shape);
            var item = new MenuItem { Header = header, Icon = ShapeIcon(tool, shape), IsChecked = current };
            item.Click += (_, _) => { choose(); View.Focus(); };
            menu.Items.Add(item);
        }
        Add(Strings.Get("tool.line"), ToolKind.Line, default, () => SetTool(ToolKind.Line));
        Add(Strings.Get("tool.curve"), ToolKind.Curve, default, () => SetTool(ToolKind.Curve));
        menu.Items.Add(new Separator());
        foreach (var (name, kind) in new[] { (Strings.Get("shape.rectangle"), ShapeKind.Rectangle), (Strings.Get("shape.ellipse"), ShapeKind.Ellipse), (Strings.Get("shape.triangle"), ShapeKind.Triangle), (Strings.Get("shape.polygon"), ShapeKind.Polygon) })
            Add(name, ToolKind.Shape, kind, () => SetShapeKind(kind));
        menu.IsOpen = true;
        LastShapeMenu = menu;
    }

    /// <summary>The shape button menu opened last (validation harness).</summary>
    public ContextMenu? LastShapeMenu { get; private set; }

    /// <summary>An outline icon of a shape group tool in the glyph cell and stroke of the Fluent icons (D13c), drawn in the button's text color.</summary>
    private FrameworkElement ShapeIcon(ToolKind tool, ShapeKind shape)
    {
        string data = tool switch
        {
            ToolKind.Line => "M2,14 L14,2",
            ToolKind.Curve => "M1.5,14 C3.5,1.5 10,15.5 14.5,2",
            _ => shape switch
            {
                ShapeKind.Ellipse => "M8,2.5 A6.5,5.5 0 1 1 7.99,2.5 Z",
                ShapeKind.Triangle => "M8,2 L14.5,14 L1.5,14 Z",
                ShapeKind.Polygon => "M2,5.5 L8.5,1.5 L14.5,6 L12,14 L3.5,13 Z",
                _ => "M1.5,3 L14.5,3 L14.5,13 L1.5,13 Z"
            }
        };
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data), StrokeThickness = (double)FindResource("Icon.Stroke"), Width = (double)FindResource("Icon.Size"), Height = (double)FindResource("Icon.Size"),
            Margin = (Thickness)FindResource("Space.IconGap"), Stretch = Stretch.None,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center
        };
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1) });
        return path;
    }

    // ----- line options: dash, arrows, shape (spec 8.1–8.2) -----

    private static readonly string[] DashNames = [Strings.Get("dash.solid"), Strings.Get("dash.dashed"), Strings.Get("dash.dotted"), Strings.Get("dash.dashDot")];

    private void InitializeLineOptions()
    {
        LineOptionsPanel.Children.Add(new Separator());
        dashBox = new ComboBox { Width = 82, ItemsSource = DashNames, VerticalAlignment = VerticalAlignment.Center, ToolTip = Strings.Get("line.dashStyle") };
        dashBox.SelectionChanged += (_, _) =>
        {
            if (!syncingStyleBar && dashBox.SelectedIndex >= 0) SetLineDash((DashStyle)dashBox.SelectedIndex);
        };
        LineOptionsPanel.Children.Add(dashBox);
        startArrowButton = ArrowToggle("\uE72B", Strings.Get("line.startArrow"), on => SetLineArrows(start: on));
        endArrowButton = ArrowToggle("\uE72A", Strings.Get("line.endArrow"), on => SetLineArrows(end: on));
        shapeBox = new ComboBox { Width = 104, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = Strings.Get("line.controls") };
        shapeBox.SelectionChanged += (_, _) =>
        {
            if (!syncingStyleBar && shapeBox.SelectedIndex >= 0) SetLineShape(shapeBox.SelectedIndex);
        };
        LineOptionsPanel.Children.Add(shapeBox);
    }

    private ToggleButton ArrowToggle(string glyph, string tip, Action<bool> apply)
    {
        var button = new ToggleButton
        {
            Content = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), Margin = new Thickness(0) },
            Width = 30, Margin = new Thickness(4, 0, 0, 0), Focusable = false, ToolTip = tip, Style = (Style)FindResource("ToolButton")
        };
        button.Click += (_, _) => apply(button.IsChecked == true);
        LineOptionsPanel.Children.Add(button);
        return button;
    }

    public void SetLineDash(DashStyle dash) => ApplyLineOptions(o => o with { Dash = dash }, l => l with { Dash = dash });

    public void SetLineArrows(bool? start = null, bool? end = null) => ApplyLineOptions(
        o => o with { StartArrow = start ?? o.StartArrow, EndArrow = end ?? o.EndArrow },
        l => l with { StartArrow = start ?? l.StartArrow, EndArrow = end ?? l.EndArrow });

    /// <summary>
    /// With a selected line: 0 straight, 1 or 2 control points (raising keeps the shape, lowering approximates).
    /// Otherwise the curve tool's choice for new curves: 0 automatic, 1 or 2 forced.
    /// </summary>
    public void SetLineShape(int controls)
    {
        if (controls is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(controls));
        if (SelectedLine is { } line)
        {
            UpdateSelected(line, LineGeometry.Convert(line, (LineKind)controls), null, "undo.changeLineShape");
        }
        else if (tools.Current == ToolKind.Curve)
        {
            lineOptions = lineOptions with { CurveControls = controls };
            settings.LineOptions = lineOptions;
            settings.Save();
        }
        UpdateStyleBar();
    }

    private void ApplyLineOptions(Func<LineOptions, LineOptions> toolChange, Func<LineAnnotation, LineAnnotation> lineChange)
    {
        if (tools.Current is ToolKind.Line or ToolKind.Curve)
        {
            lineOptions = toolChange(lineOptions).Validated();
            settings.LineOptions = lineOptions;
            settings.Save();
        }
        if (OptionLines is { Length: > 0 } lines) UpdateSelected(lines, lines.Select(lineChange).ToArray(), null);
        UpdateStyleBar();
    }

    private void UpdateLineOptions()
    {
        var lines = OptionLines;
        var line = lines.Length == 1 ? lines[0] : null;
        bool tool = tools.Current is ToolKind.Line or ToolKind.Curve;
        LineOptionsPanel.Visibility = lines.Length > 0 || tool ? Visibility.Visible : Visibility.Collapsed;
        if (LineOptionsPanel.Visibility != Visibility.Visible) return;
        var options = lines.Length == 0 ? lineOptions : new LineOptions(lines[0].Dash, lines[0].StartArrow, lines[0].EndArrow, 0);
        // Several lines with different values show nothing chosen (mixed) until a value is set for all of them.
        dashBox.SelectedIndex = lines.Any(l => l.Dash != options.Dash) ? -1 : (int)options.Dash;
        startArrowButton.IsChecked = options.StartArrow && lines.All(l => l.StartArrow);
        endArrowButton.IsChecked = options.EndArrow && lines.All(l => l.EndArrow);
        string[] shapes = line is not null ? [Strings.Get("lineShape.straight"), Strings.Get("lineShape.oneControl"), Strings.Get("lineShape.twoControls")] : tools.Current == ToolKind.Curve && lines.Length == 0 ? [Strings.Get("common.auto"), Strings.Get("lineShape.oneControl"), Strings.Get("lineShape.twoControls")] : [];
        if (shapeBox.ItemsSource is not string[] current || !current.SequenceEqual(shapes)) shapeBox.ItemsSource = shapes;
        shapeBox.Visibility = shapes.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        shapeBox.SelectedIndex = shapes.Length == 0 ? -1 : line is not null ? (int)line.LineKind : lineOptions.CurveControls;
    }

    // ----- style bar UI -----

    private void UpdateStyleBar()
    {
        var target = StyleTarget;
        UpdateTextStyleBar();
        bool eraser = tools.Current is ToolKind.StrokeEraser or ToolKind.AreaEraser;
        EraserBorder.Visibility = eraser ? Visibility.Visible : Visibility.Collapsed;
        StyleBar.Visibility = eraser || TextStyleBorder.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        StyleBar.IsEnabled = target is not null;
        StyleBar.Opacity = target is null ? 0.55 : 1;
        // An area-eraser piece keeps its shape: its width is no longer editable.
        var selection = StyleSelection;
        var mixed = StyleMixed;
        bool fixedWidth = selection.Length > 0 && selection.All(a => a is StrokeAnnotation { IsOutlined: true });
        UpdateShapeOptions();
        bool highlighter = tools.Current == ToolKind.FreeHighlight;
        bool highlight = highlighter && markKind is null || selection.Length > 0 && selection.All(a => a is PdfTextHighlight);
        MarkKindPanel.Visibility = highlighter ? Visibility.Visible : Visibility.Collapsed;
        HighlighterSnapButton.Visibility = highlighter ? Visibility.Visible : Visibility.Collapsed;
        HighlighterSnapButton.IsChecked = settings.HighlighterSnap;
        HighlighterSnapButton.IsEnabled = markKind is null; // underline and strikeout always go on text (spec 9.1)
        (markKind switch { TextMarkupKind.Underline => MarkUnderlineButton, TextMarkupKind.StrikeOut => MarkStrikeOutButton, _ => MarkHighlightButton }).IsChecked = true;
        // A sticky note has a color only (spec 24.9).
        bool colorOnly = tools.Current == ToolKind.Note || selection.Length > 0 && selection.All(a => a is StickyNote);
        TransparencySlider.IsEnabled = TransparencyBox.IsEnabled = !colorOnly;
        HighlightAutoButton.Visibility = highlight ? Visibility.Visible : Visibility.Collapsed;
        HighlightAutoButton.IsChecked = highlighter ? highlighterAuto
            : selection.Length > 0 && selection.All(a => a is PdfTextHighlight { HeightMode: HighlightHeightMode.Auto });
        // Automatic glyph height leaves no width to set on selected glyph highlights; the highlighter's width still
        // sets its free strokes (spec 9.1).
        WidthSlider.IsEnabled = WidthBox.IsEnabled = !fixedWidth && !colorOnly && !(highlight && !highlighter && HighlightAutoButton.IsChecked == true);
        if (eraser) UpdateEraserBar();
        syncingStyleBar = true;
        try
        {
            var (style, kind) = target ?? (penStyle, AnnotationKind.Ink);
            var (min, max) = StrokeStyle.WidthRange(kind);
            WidthSlider.Minimum = min;
            WidthSlider.Maximum = max;
            WidthSlider.Value = Math.Clamp(style.WidthPt, min, max);
            if (!WidthBox.IsKeyboardFocusWithin) WidthBox.Text = target is null || mixed.Width ? "" : FormatNumber(style.WidthPt);
            TransparencySlider.Value = style.TransparencyPercent;
            if (!TransparencyBox.IsKeyboardFocusWithin) TransparencyBox.Text = target is null || mixed.Transparency ? "" : FormatNumber(style.TransparencyPercent);
            UpdateLineOptions();
            bool showColor = target is not null && !mixed.Color;
            foreach (Button swatch in PalettePanel.Children) MarkSwatch(swatch, showColor && Equals(swatch.DataContext, style.Color));
            RecentPanel.Children.Clear();
            foreach (var color in recentColors)
            {
                var swatch = CreateSwatch(color, color.ToHex(), SetColor, removable: true);
                MarkSwatch(swatch, showColor && color == style.Color);
                RecentPanel.Children.Add(swatch);
            }
        }
        finally { syncingStyleBar = false; }
    }

    private static void MarkSwatch(Button swatch, bool current) => swatch.Tag = current ? "Current" : null;

    /// <summary>A palette swatch; a user color (<paramref name="removable"/>) has the context menu 제거 (spec 3.2, 2026-10-13).</summary>
    private Button CreateSwatch(RgbColor color, string name, Action<RgbColor> pick, bool removable = false)
    {
        var swatch = new Button
        {
            Style = (Style)FindResource("Swatch"), DataContext = color, ToolTip = name, Focusable = false,
            Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B))
        };
        swatch.Click += (_, _) => pick(color);
        if (removable)
        {
            var remove = new MenuItem { Header = Strings.Get("color.remove") };
            remove.Click += (_, _) => RemoveUserColor(color);
            swatch.ContextMenu = new ContextMenu { Items = { remove } };
        }
        return swatch;
    }

    /// <summary>
    /// Adds a color outside the basic palette to the user colors every palette shares (spec 3.4, D14g): at the end, never
    /// pushing one out; a full list (12) takes no more until one is removed.
    /// </summary>
    private void RememberColor(RgbColor color)
    {
        if (ColorPalette.Basic.Contains(color)) return;
        recentColors = ColorPalette.Add(recentColors, color);
        settings.RecentColors = [.. recentColors.Select(c => c.ToHex())];
    }

    /// <summary>
    /// Removes a user color from every palette (spec 3.2, 2026-10-13). An app setting: no Undo, the document stays as it
    /// is, and annotations in that color keep it. Basic colors cannot be removed.
    /// </summary>
    public void RemoveUserColor(RgbColor color)
    {
        if (!recentColors.Contains(color)) return;
        recentColors = ColorPalette.Remove(recentColors, color);
        settings.RecentColors = [.. recentColors.Select(c => c.ToHex())];
        settings.Save();
        UpdateStyleBar();
    }

    /// <summary>
    /// The + of a palette (spec 3.2, 2026-10-13 user requests): a popup with the color plane and hue strip, the color code,
    /// the eyedropper and 추가. 추가 or Enter applies the color with <paramref name="apply"/> (it becomes a user color),
    /// Esc closes. The popup goes next to the button so it is in the window's tree (focus inside it is focus inside the window).
    /// </summary>
    private (ToggleButton Button, Popup Popup) CustomColorEntry(string name, Func<RgbColor> current, Action<RgbColor> apply)
    {
        var button = new ToggleButton { Name = name + "Button", Style = (Style)FindResource("ToolButton"), Padding = new Thickness(6, 3, 6, 3), ToolTip = Strings.Get("color.custom"),
            Content = new TextBlock { Text = "\uE790", Style = (Style)FindResource("Icon"), Margin = new Thickness(0), FontSize = 13 } };
        var picker = new Views.ColorPickerPanel(name);
        var frame = new Border { BorderThickness = new Thickness(1), Child = picker };
        frame.SetResourceReference(Border.BackgroundProperty, "Surface.Background");
        frame.SetResourceReference(Border.BorderBrushProperty, "Border");
        frame.SetResourceReference(Border.PaddingProperty, "Space.Popup");
        var popup = new Popup { Name = name + "Popup", PlacementTarget = button, StaysOpen = false, Child = frame };
        button.SetBinding(ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(Popup.IsOpen)) { Source = popup, Mode = System.Windows.Data.BindingMode.TwoWay });
        void Back() => (textEditor as IInputElement ?? View).Focus();
        // A full user color list (spec 3.4: 12) adds nothing: 추가 stays off until a color is removed.
        popup.Opened += (_, _) => { picker.Add.IsEnabled = recentColors.Count < ColorPalette.MaxUser; picker.ShowColor(current()); picker.Code.Focus(); picker.Code.SelectAll(); };
        picker.Picked += color => { popup.IsOpen = false; apply(color); Back(); };
        picker.EyedropperRequested += () => { popup.IsOpen = false; StartEyedropper(apply); };
        picker.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            popup.IsOpen = false;
            Back();
            e.Handled = true;
        };
        CustomColorPickers[name] = picker;
        return (button, popup);
    }

    /// <summary>The color pickers of the palettes by name (validation harness).</summary>
    public Dictionary<string, Views.ColorPickerPanel> CustomColorPickers { get; } = [];

    // ----- eyedropper (spec 3.2, 2026-10-13 user request): one click anywhere in the window picks that pixel's color -----

    private Action<RgbColor>? eyedropper;

    public bool IsEyedropperActive => eyedropper is not null;

    /// <summary>
    /// Waits for one click in the window and applies the color shown there (page, annotation or frame) with
    /// <paramref name="apply"/>. Esc or a right click cancels. The click does nothing else (no tool input).
    /// </summary>
    public void StartEyedropper(Action<RgbColor> apply)
    {
        eyedropper = apply;
        Mouse.OverrideCursor = Cursors.Cross;
        StatusText.Text = Strings.Get("color.eyedropperTip");
    }

    private void EndEyedropper()
    {
        eyedropper = null;
        Mouse.OverrideCursor = null;
        StatusText.Text = "";
    }

    private void InitializeEyedropper()
    {
        PreviewMouseDown += (_, e) =>
        {
            if (eyedropper is not { } apply) return;
            e.Handled = true;
            if (e.ChangedButton != MouseButton.Left) { EndEyedropper(); return; }
            suppressEyedropperUp = true; // its button-up must not reach a tool either
            var color = SampleColor(e.GetPosition(this));
            EndEyedropper();
            apply(color);
            (textEditor as IInputElement ?? View).Focus();
        };
        PreviewMouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Left && suppressEyedropperUp) { suppressEyedropperUp = false; e.Handled = true; } };
        PreviewKeyDown += (_, e) => { if (eyedropper is not null && e.Key == Key.Escape) { EndEyedropper(); e.Handled = true; } };
        Deactivated += (_, _) => { if (eyedropper is not null) EndEyedropper(); };
    }

    private bool suppressEyedropperUp;

    /// <summary>The color of the window's pixel at a point (DIPs from the window's top-left), as drawn now.</summary>
    public RgbColor SampleColor(Point windowPoint)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var content = (FrameworkElement)Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        // The eyedropper takes the color before the reading dark page inverts it (spec 24.12).
        var effect = Resources["Page.Effect"];
        Resources["Page.Effect"] = null;
        content.UpdateLayout();
        bitmap.Render(content);
        Resources["Page.Effect"] = effect;
        var at = TranslatePoint(windowPoint, content);
        int x = Math.Clamp((int)(at.X * dpi.DpiScaleX), 0, bitmap.PixelWidth - 1), y = Math.Clamp((int)(at.Y * dpi.DpiScaleY), 0, bitmap.PixelHeight - 1);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return new(pixel[2], pixel[1], pixel[0]); // the window is opaque: premultiplied = plain
    }

    private static string ColorName(RgbColor color)
    {
        int index = ((IList<RgbColor>)ColorPalette.Basic).IndexOf(color);
        string[] names = [Strings.Get("color.black"), Strings.Get("color.gray"), Strings.Get("color.red"), Strings.Get("color.orange"), Strings.Get("color.yellow"), Strings.Get("color.green"), Strings.Get("color.blue"), Strings.Get("color.navy"), Strings.Get("color.purple"), Strings.Get("color.white")];
        return index >= 0 && index < names.Length ? names[index] : color.ToHex();
    }

    private static string FormatNumber(double value) => Math.Round(value, 1).ToString("0.#", CultureInfo.CurrentCulture);

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Trim().TrimEnd('%').Replace("pt", "", StringComparison.OrdinalIgnoreCase).Trim(),
            NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);

    private void WidthSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!syncingStyleBar) SetWidth(Math.Round(e.NewValue * 2) / 2, continuous: true);
    }

    private void TransparencySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!syncingStyleBar) SetTransparency(Math.Round(e.NewValue), continuous: true);
    }

    private void SliderCommitted(object sender, RoutedEventArgs e) => CommitStyleChange();

    private void WidthBoxCommitted(object sender, RoutedEventArgs e)
    {
        if (TryParseNumber(WidthBox.Text, out double value)) SetWidth(value);
        WidthBox.Text = StyleTarget is { } t && !StyleMixed.Width ? FormatNumber(t.Style.WidthPt) : "";
    }

    private void TransparencyBoxCommitted(object sender, RoutedEventArgs e)
    {
        if (TryParseNumber(TransparencyBox.Text, out double value)) SetTransparency(value);
        TransparencyBox.Text = StyleTarget is { } t && !StyleMixed.Transparency ? FormatNumber(t.Style.TransparencyPercent) : "";
    }

    private void StyleBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        if (e.Key == Key.Escape) UpdateStyleBarText(sender);
        View.Focus(); // losing focus commits (Enter) or shows the reverted value (Esc)
        e.Handled = true;
    }

    private void UpdateStyleBarText(object box)
    {
        if (StyleTarget is not { } t) return;
        if (ReferenceEquals(box, WidthBox)) WidthBox.Text = FormatNumber(t.Style.WidthPt);
        else TransparencyBox.Text = FormatNumber(t.Style.TransparencyPercent);
    }

    private static bool TryParseColor(string text, out RgbColor color) => Views.ColorPickerPanel.TryParse(text, out color);

    // ----- eraser bar: stroke/area mode and diameter (spec 7.3–7.4) -----

    /// <summary>Sets the diameter of the active eraser (points, 2–100) and stores it.</summary>
    public void SetEraserSize(double diameterPt)
    {
        if (!double.IsFinite(diameterPt)) return;
        if (tools.Current == ToolKind.StrokeEraser) settings.StrokeEraserPt = EraserSizes.Clamp(diameterPt, EraserMode.Stroke);
        else if (tools.Current == ToolKind.AreaEraser) settings.AreaEraserPt = EraserSizes.Clamp(diameterPt, EraserMode.Area);
        else return;
        UpdateEraserBar();
    }

    private double ActiveEraserSize => tools.Current == ToolKind.StrokeEraser ? settings.StrokeEraserPt : settings.AreaEraserPt;

    private void UpdateEraserBar()
    {
        syncingStyleBar = true;
        try
        {
            EraserSizeSlider.Value = ActiveEraserSize;
            if (!EraserSizeBox.IsKeyboardFocusWithin) EraserSizeBox.Text = FormatNumber(ActiveEraserSize);
        }
        finally { syncingStyleBar = false; }
    }

    private void EraserSizeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!syncingStyleBar) SetEraserSize(Math.Round(e.NewValue));
    }

    private void EraserSizeCommitted(object sender, RoutedEventArgs e) => settings.Save();

    private void EraserSizeBoxCommitted(object sender, RoutedEventArgs e)
    {
        if (TryParseNumber(EraserSizeBox.Text, out double value)) SetEraserSize(value);
        EraserSizeBox.Text = FormatNumber(ActiveEraserSize);
        settings.Save();
    }

    private void EraserSizeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        if (e.Key == Key.Escape) EraserSizeBox.Text = FormatNumber(ActiveEraserSize);
        View.Focus();
        e.Handled = true;
    }
}
