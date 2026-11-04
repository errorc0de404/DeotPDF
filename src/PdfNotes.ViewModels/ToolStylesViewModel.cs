using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;

namespace PdfNotes.ViewModels;

/// <summary>
/// What the style bar shows (spec 3.1–3.2, 7.3, 8, 9.1; P1 V7a): which of the three bars is up, the values and which of
/// them are mixed across a selection, and which parts are on. The view only copies it into its controls.
/// </summary>
public sealed record StyleBarState(
    (StrokeStyle Style, AnnotationKind Kind)? Target, bool Eraser, (bool Color, bool Width, bool Transparency) Mixed,
    bool Highlighter, bool MarkKindShown, bool SnapEnabled, bool HighlightAutoShown, bool HighlightAutoChecked,
    bool TransparencyEnabled, bool WidthEnabled, bool LineOptionsShown, bool ShapeOptionsShown)
{
    /// <summary>The style the bar's controls show (the pen's when there is nothing to style).</summary>
    public (StrokeStyle Style, AnnotationKind Kind) Shown(StrokeStyle pen) => Target ?? (pen, AnnotationKind.Ink);
}

/// <summary>The line options the bar shows: dash (-1 mixed), arrows, and the control point choices with the chosen one.</summary>
public sealed record LineOptionsState(int DashIndex, bool StartArrow, bool EndArrow, LineShapeChoice Shapes, int ShapeIndex);

/// <summary>The line shape box: none, a selected line's straight/one/two controls, or the curve tool's automatic/one/two.</summary>
public enum LineShapeChoice { None, SelectedLine, CurveTool }

/// <summary>The shape options the bar shows: fill or border target, and the border box (0 none, 1+ dash; -1 mixed).</summary>
public sealed record ShapeOptionsState(bool FillTarget, int BorderIndex);

/// <summary>
/// The drawing tools' styles and the style bar's commands (spec 3.1–3.2, 7, 8, 9, 24.7, 24.9; P1 V7a): the pen, highlighter
/// (and its underline/strikeout kinds), line, shape and note styles (app settings), the user colors every palette shares,
/// the eraser sizes, and applying a change to the tool and to the selected annotations (one Undo unit; slider drags merge
/// until committed). <see cref="Changed"/> asks the view to show the bar again.
/// </summary>
public sealed class ToolStylesViewModel : ObservableObject
{
    private readonly MainViewModel owner;
    private SelectTool? selection;
    private StrokeStyle pen, highlighter, line, underline, strikeOut;
    private ShapeStyle shape;
    private LineOptions lineOptions;
    private TextMarkupKind? markKind;
    private bool highlighterAuto, shapeFillTarget = true;
    private List<RgbColor> userColors;
    private RgbColor noteColor = new(0xFF, 0xD5, 0x4F);

    public ToolStylesViewModel(MainViewModel owner)
    {
        this.owner = owner;
        var s = owner.Settings;
        pen = s.Pen.ToStyle(StrokeStyle.DefaultPen, AnnotationKind.Ink);
        highlighter = s.Highlighter.ToStyle(StrokeStyle.DefaultHighlighter, AnnotationKind.FreeHighlight);
        userColors = [.. s.GetRecentColors()];
        line = s.Line.ToStyle(StrokeStyle.DefaultPen, AnnotationKind.Line);
        lineOptions = s.LineOptions;
        highlighterAuto = s.HighlighterAuto;
        shape = s.Shape.ToStyle();
        underline = s.Underline?.ToStyle(StrokeStyle.DefaultMarkup, AnnotationKind.TextMarkup) ?? StrokeStyle.DefaultMarkup;
        strikeOut = s.StrikeOut?.ToStyle(StrokeStyle.DefaultMarkup, AnnotationKind.TextMarkup) ?? StrokeStyle.DefaultMarkup;
        markKind = s.MarkKind;
        if (RgbColor.TryParse(s.NoteColor, out var savedNote)) noteColor = savedNote;
    }

    /// <summary>The style bar should show the current state again.</summary>
    public event EventHandler? Changed;

    private void Show() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>The tool or the selection changed: the bar shows the new state.</summary>
    public void Refresh() => Show();

    /// <summary>The selection tool whose selection the bar edits (made by the view with its canvas).</summary>
    public void Attach(SelectTool tool) => selection = tool;

    private AppSettings Settings => owner.Settings;
    private ToolKind Current => owner.Tools.Current;
    private Annotation[] Selected => selection?.SelectedAnnotations ?? [];

    public StrokeStyle Pen => pen;
    public StrokeStyle Highlighter => highlighter;
    public StrokeStyle Line => line;
    public LineOptions LineOptions => lineOptions;
    public ShapeStyle Shape => shape;
    public RgbColor NoteColor => noteColor;

    /// <summary>What the highlighter makes now (spec 9.1): null for highlights, or underline / strikeout.</summary>
    public TextMarkupKind? MarkKind => markKind;

    /// <summary>The style of the current underline or strikeout kind (each kind remembers its own).</summary>
    public StrokeStyle MarkupStyle => markKind == TextMarkupKind.StrikeOut ? strikeOut : underline;

    /// <summary>Glyph highlights of the highlighter are the glyph height (on) or the width as height (off).</summary>
    public bool HighlighterAuto => highlighterAuto;

    /// <summary>The glyph highlight style of the highlighter (spec 9.1): the highlight kind's color and transparency, and the height choice.</summary>
    public TextHighlightStyle TextHighlightStyle => new(highlighter.ClampFor(AnnotationKind.PdfTextHighlight), highlighterAuto);

    /// <summary>True when the palette and transparency edit the fill of shapes, false for their border.</summary>
    public bool ShapeFillTarget => shapeFillTarget;

    /// <summary>The user colors every palette shares (spec 3.4: up to 12, app setting).</summary>
    public IReadOnlyList<RgbColor> UserColors => userColors;

    // ----- what the bar edits -----

    /// <summary>The single selected, unlocked line (also the line just drawn with a line tool).</summary>
    public LineAnnotation? SelectedLine => Selected is [LineAnnotation l] ? l : null;

    /// <summary>The single selected, unlocked PDF glyph highlight.</summary>
    public PdfTextHighlight? SelectedTextHighlight => Selected is [PdfTextHighlight h] ? h : null;

    /// <summary>The single selected, unlocked shape (also the shape just drawn with the shape tool).</summary>
    public ShapeAnnotation? SelectedShape => Selected is [ShapeAnnotation s] ? s : null;

    /// <summary>
    /// The selected annotations the style bar edits together (spec 3.1, 9.4): every unlocked selected one, when all of
    /// them have a color, width and transparency. A selection that also holds text boxes or images offers only the
    /// commands they share, so the bar is off.
    /// </summary>
    public Annotation[] StyleSelection => Current is ToolKind.Select or ToolKind.Lasso && Selected is { Length: > 0 } all && all.All(ToolStyles.HasStrokeStyle) ? all : [];

    private StrokeStyle StyleOf(Annotation a) => ToolStyles.StyleOf(a, shapeFillTarget);
    private StrokeStyle ShapeStroke(ShapeStyle s) => ToolStyles.ShapeStroke(s, shapeFillTarget);
    private ShapeStyle WithStroke(ShapeStyle s, StrokeStyle stroke) => ToolStyles.WithStroke(s, stroke, shapeFillTarget);

    /// <summary>Which values differ across a multi-selection: the bar shows them empty (mixed) until one is set for all.</summary>
    public (bool Color, bool Width, bool Transparency) StyleMixed => ToolStyles.Mixed(StyleSelection.Select(StyleOf).ToArray());

    /// <summary>
    /// The style the bar shows and edits, or null when there is nothing to style (select tool, no selection).
    /// With a line tool the bar shows the line just drawn while it is selected; edits then change it and the tool.
    /// </summary>
    public (StrokeStyle Style, AnnotationKind Kind)? StyleTarget => Current switch
    {
        ToolKind.Pen => (pen, AnnotationKind.Ink),
        ToolKind.FreeHighlight => markKind is null ? (highlighter, AnnotationKind.FreeHighlight) : (MarkupStyle, AnnotationKind.TextMarkup),
        ToolKind.Line or ToolKind.Curve => (SelectedLine is { } l ? StrokeStyle.Of(l) : line, AnnotationKind.Line),
        ToolKind.Note => (new StrokeStyle(noteColor, StrokeStyle.DefaultPen.WidthPt, 0), AnnotationKind.Note),
        ToolKind.Shape => (ShapeStroke(SelectedShape?.Style ?? shape), AnnotationKind.Shape),
        _ => StyleSelection is [var first, ..] ? (StyleOf(first), first.Kind) : null
    };

    /// <summary>The lines the line options edit: the line just drawn with a line tool, or a selection of lines only.</summary>
    public LineAnnotation[] OptionLines => Current is ToolKind.Line or ToolKind.Curve ? SelectedLine is { } l ? [l] : []
        : StyleSelection is { Length: > 0 } s && s.All(a => a is LineAnnotation) ? [.. s.Cast<LineAnnotation>()] : [];

    /// <summary>The shapes the shape options edit: the shape just drawn with the shape tool, or a selection of shapes only.</summary>
    public ShapeAnnotation[] OptionShapes => Current == ToolKind.Shape ? SelectedShape is { } s ? [s] : []
        : StyleSelection is { Length: > 0 } all && all.All(a => a is ShapeAnnotation) ? [.. all.Cast<ShapeAnnotation>()] : [];

    /// <summary>The style bar as it should look now.</summary>
    public StyleBarState Bar
    {
        get
        {
            var target = StyleTarget;
            var selected = StyleSelection;
            bool eraser = Current is ToolKind.StrokeEraser or ToolKind.AreaEraser;
            bool highlighterTool = Current == ToolKind.FreeHighlight;
            bool highlight = highlighterTool && markKind is null || selected.Length > 0 && selected.All(a => a is PdfTextHighlight);
            // A sticky note has a color only (spec 24.9); an area-eraser piece keeps its shape: its width is fixed.
            bool colorOnly = Current == ToolKind.Note || selected.Length > 0 && selected.All(a => a is StickyNote);
            bool fixedWidth = selected.Length > 0 && selected.All(a => a is StrokeAnnotation { IsOutlined: true });
            bool autoChecked = highlighterTool ? highlighterAuto : selected.Length > 0 && selected.All(a => a is PdfTextHighlight { HeightMode: HighlightHeightMode.Auto });
            // Automatic glyph height leaves no width to set on selected glyph highlights; the highlighter's width still
            // sets its free strokes (spec 9.1).
            bool widthEnabled = !fixedWidth && !colorOnly && !(highlight && !highlighterTool && autoChecked);
            return new(target, eraser, StyleMixed, highlighterTool, highlighterTool, markKind is null, highlight, autoChecked,
                !colorOnly, widthEnabled, OptionLines.Length > 0 || Current is ToolKind.Line or ToolKind.Curve, Current == ToolKind.Shape || OptionShapes.Length > 0);
        }
    }

    /// <summary>The line options the bar shows (several lines with different values show nothing chosen).</summary>
    public LineOptionsState LineBar
    {
        get
        {
            var lines = OptionLines;
            var single = lines.Length == 1 ? lines[0] : null;
            var options = lines.Length == 0 ? lineOptions : new LineOptions(lines[0].Dash, lines[0].StartArrow, lines[0].EndArrow, 0);
            var shapes = single is not null ? LineShapeChoice.SelectedLine : Current == ToolKind.Curve && lines.Length == 0 ? LineShapeChoice.CurveTool : LineShapeChoice.None;
            return new(lines.Any(l => l.Dash != options.Dash) ? -1 : (int)options.Dash, options.StartArrow && lines.All(l => l.StartArrow),
                options.EndArrow && lines.All(l => l.EndArrow), shapes,
                shapes == LineShapeChoice.None ? -1 : single is not null ? (int)single.LineKind : lineOptions.CurveControls);
        }
    }

    /// <summary>The shape options the bar shows.</summary>
    public ShapeOptionsState ShapeBar
    {
        get
        {
            var shapes = OptionShapes;
            var styles = shapes.Length > 0 ? shapes.Select(s => s.Style).ToArray() : [shape];
            static int Index(ShapeStyle s) => s.BorderEnabled ? 1 + (int)s.BorderDash : 0;
            return new(shapeFillTarget, styles.All(s => Index(s) == Index(styles[0])) ? Index(styles[0]) : -1);
        }
    }

    // ----- color, width, transparency -----

    /// <summary>Applies a color to the target. Colors outside the basic palette become user colors.</summary>
    public void SetColor(RgbColor color)
    {
        RememberColor(color);
        Apply(s => s with { Color = color }, a => a with { Color = color }, l => l with { Color = color }, null, h => h with { Color = color });
    }

    /// <summary>Applies a width. <paramref name="continuous"/> changes (slider drag) merge into one Undo unit until committed.</summary>
    public void SetWidth(double widthPt, bool continuous = false)
    {
        if (!double.IsFinite(widthPt)) return;
        Apply(s => s with { WidthPt = widthPt }, a => a.IsOutlined ? a : a with { WidthPt = ToolStyles.ClampWidth(widthPt, a.Kind) }, l => l with { WidthPt = ToolStyles.ClampWidth(widthPt, l.Kind) },
            continuous ? "width" : null, h => h with { HeightMode = HighlightHeightMode.Manual, ManualHeightPt = ToolStyles.ClampWidth(widthPt, AnnotationKind.PdfTextHighlight) });
    }

    public void SetTransparency(double percent, bool continuous = false)
    {
        if (!double.IsFinite(percent)) return;
        percent = Math.Clamp(percent, 0, 100);
        double opacity = Opacity.FromTransparencyPercent(percent);
        Apply(s => s with { TransparencyPercent = percent }, a => a with { Opacity = opacity }, l => l with { Opacity = opacity },
            continuous ? "transparency" : null, h => h with { Opacity = opacity });
    }

    /// <summary>Ends a slider drag: the merged change becomes one Undo unit and tool settings are stored.</summary>
    public void Commit()
    {
        owner.Document?.Session.SealCoalescing();
        owner.SaveSettings();
    }

    private void Apply(Func<StrokeStyle, StrokeStyle> toolChange, Func<StrokeAnnotation, StrokeAnnotation> strokeChange,
        Func<LineAnnotation, LineAnnotation> lineChange, string? coalescePart, Func<PdfTextHighlight, PdfTextHighlight>? highlightChange = null)
    {
        switch (Current)
        {
            case ToolKind.FreeHighlight when markKind == TextMarkupKind.StrikeOut:
                strikeOut = toolChange(strikeOut).ClampFor(AnnotationKind.TextMarkup);
                Settings.StrikeOut = AppSettings.StyleSettings.From(strikeOut);
                break;
            case ToolKind.FreeHighlight when markKind == TextMarkupKind.Underline:
                underline = toolChange(underline).ClampFor(AnnotationKind.TextMarkup);
                Settings.Underline = AppSettings.StyleSettings.From(underline);
                break;
            case ToolKind.Note:
                noteColor = toolChange(new StrokeStyle(noteColor, StrokeStyle.DefaultPen.WidthPt, 0)).Color;
                Settings.NoteColor = noteColor.ToHex();
                break;
            case ToolKind.Pen:
                pen = toolChange(pen).ClampFor(AnnotationKind.Ink);
                Settings.Pen = AppSettings.StyleSettings.From(pen);
                break;
            case ToolKind.FreeHighlight:
                highlighter = toolChange(highlighter).ClampFor(AnnotationKind.FreeHighlight);
                Settings.Highlighter = AppSettings.StyleSettings.From(highlighter);
                break;
            case ToolKind.Shape:
                shape = WithStroke(shape, toolChange(ShapeStroke(shape)));
                Settings.Shape = AppSettings.ShapeSettings.From(shape);
                if (SelectedShape is { } drawnShape) Update(drawnShape, drawnShape with { Style = WithStroke(drawnShape.Style, toolChange(ShapeStroke(drawnShape.Style))) }, coalescePart);
                break;
            case ToolKind.Line or ToolKind.Curve:
                line = toolChange(line).ClampFor(AnnotationKind.Line);
                Settings.Line = AppSettings.StyleSettings.From(line);
                if (SelectedLine is { } drawn) Update(drawn, lineChange(drawn), coalescePart);
                break;
            default:
                var targets = StyleSelection;
                if (targets.Length == 0) return;
                Update(targets, [.. targets.Select(a => a switch
                {
                    StrokeAnnotation s => strokeChange(s),
                    LineAnnotation l => lineChange(l),
                    PdfTextHighlight h => highlightChange?.Invoke(h) ?? h,
                    PdfTextMarkup m => toolChange(StrokeStyle.Of(m)).ClampFor(AnnotationKind.TextMarkup) is var ms ? m with { Color = ms.Color, WidthPt = ms.WidthPt, Opacity = ms.Opacity } : m,
                    StickyNote n => n with { Color = toolChange(StyleOf(n)).Color },
                    ShapeAnnotation sh => sh with { Style = WithStroke(sh.Style, toolChange(ShapeStroke(sh.Style))) },
                    _ => a
                })], coalescePart);
                break;
        }
        if (coalescePart is null) owner.SaveSettings();
        Show();
    }

    /// <summary>Replaces one selected annotation; slider drags (<paramref name="coalescePart"/>) merge into one Undo unit.</summary>
    public void Update(Annotation before, Annotation after, string? coalescePart, string label = "undo.changeStyle") => Update([before], [after], coalescePart, label);

    /// <summary>Replaces selected annotations together (one Undo unit for the whole selection).</summary>
    public void Update(IReadOnlyList<Annotation> before, IReadOnlyList<Annotation> after, string? coalescePart, string label = "undo.changeStyle")
    {
        if (owner.Document is not { } doc || before.Count == 0) return;
        var changed = after.Where((a, i) => !a.Equals(before[i])).ToArray();
        if (changed.Length > 0)
            doc.Session.Execute(DocumentEdits.UpdateAnnotations(doc.Session.Current, changed, label,
                coalescePart is null ? null : $"style:{string.Join(",", before.Select(a => a.Id))}:{coalescePart}"));
        if (coalescePart is null) doc.Session.SealCoalescing();
    }

    // ----- the highlighter (spec 9.1) -----

    /// <summary>Glyph highlight height: automatic (glyph height) or the width as the manual height.</summary>
    public void SetHighlightAuto(bool auto)
    {
        if (Current == ToolKind.FreeHighlight && markKind is null)
        {
            highlighterAuto = auto;
            Settings.HighlighterAuto = auto;
            owner.SaveSettings();
        }
        else if (StyleSelection is { Length: > 0 } targets && targets.All(a => a is PdfTextHighlight))
        {
            Update(targets, [.. targets.Cast<PdfTextHighlight>().Select(h => (Annotation)(auto ? h with { HeightMode = HighlightHeightMode.Auto, ManualHeightPt = null }
                : h with { HeightMode = HighlightHeightMode.Manual, ManualHeightPt = ToolStyles.ClampWidth(StrokeStyle.Of(h).WidthPt, AnnotationKind.PdfTextHighlight) }))], null);
        }
        Show();
    }

    /// <summary>Chooses what the highlighter makes: a highlight, an underline or a strikeout. App setting.</summary>
    public void SetMarkKind(TextMarkupKind? kind)
    {
        markKind = kind;
        Settings.MarkKind = kind;
        owner.SaveSettings();
        Show();
    }

    /// <summary>The highlighter's 글자에 맞추기 toggle: highlight strokes that start on text snap to it. App setting.</summary>
    public void SetHighlighterSnap(bool on)
    {
        Settings.HighlighterSnap = on;
        owner.SaveSettings();
        Show();
    }

    // ----- shapes (spec 8 preface, D11) -----

    /// <summary>Chooses what the palette and transparency edit for shapes: the fill or the border (not document state).</summary>
    public void SetShapeColorTarget(bool fill)
    {
        shapeFillTarget = fill;
        Show();
    }

    /// <summary>The border of the shape tool and of the shapes it edits: null removes it (kept when there is no fill), else its dash.</summary>
    public void SetShapeBorder(DashStyle? dash)
    {
        ShapeStyle With(ShapeStyle s) => dash is { } d ? s with { BorderEnabled = true, BorderDash = d } : s.FillOpacity > 0 ? s with { BorderEnabled = false } : s;
        if (Current == ToolKind.Shape)
        {
            shape = With(shape);
            Settings.Shape = AppSettings.ShapeSettings.From(shape);
            owner.SaveSettings();
        }
        if (OptionShapes is { Length: > 0 } shapes) Update(shapes, [.. shapes.Select(s => (Annotation)(s with { Style = With(s.Style) }))], null);
        Show();
    }

    // ----- lines (spec 8.1–8.2) -----

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
        if (SelectedLine is { } selected) Update(selected, LineGeometry.Convert(selected, (LineKind)controls), null, "undo.changeLineShape");
        else if (Current == ToolKind.Curve)
        {
            lineOptions = lineOptions with { CurveControls = controls };
            Settings.LineOptions = lineOptions;
            owner.SaveSettings();
        }
        Show();
    }

    private void ApplyLineOptions(Func<LineOptions, LineOptions> toolChange, Func<LineAnnotation, LineAnnotation> lineChange)
    {
        if (Current is ToolKind.Line or ToolKind.Curve)
        {
            lineOptions = toolChange(lineOptions).Validated();
            Settings.LineOptions = lineOptions;
            owner.SaveSettings();
        }
        if (OptionLines is { Length: > 0 } lines) Update(lines, [.. lines.Select(lineChange)], null);
        Show();
    }

    // ----- user colors (spec 3.2, 3.4) -----

    /// <summary>
    /// Adds a color outside the basic palette to the user colors every palette shares (spec 3.4, D14g): at the end, never
    /// pushing one out; a full list (12) takes no more until one is removed. Stored with the next settings save.
    /// </summary>
    public void RememberColor(RgbColor color)
    {
        if (ColorPalette.Basic.Contains(color)) return;
        userColors = ColorPalette.Add(userColors, color);
        Settings.RecentColors = [.. userColors.Select(c => c.ToHex())];
    }

    /// <summary>
    /// Removes a user color from every palette (spec 3.2, 2026-10-13). An app setting: no Undo, the document stays as it
    /// is, and annotations in that color keep it. Basic colors cannot be removed.
    /// </summary>
    public void RemoveUserColor(RgbColor color)
    {
        if (!userColors.Contains(color)) return;
        userColors = ColorPalette.Remove(userColors, color);
        Settings.RecentColors = [.. userColors.Select(c => c.ToHex())];
        owner.SaveSettings();
        Show();
    }

    // ----- erasers (spec 7.3–7.4) -----

    /// <summary>The diameter of the active eraser (points).</summary>
    public double EraserSize => Current == ToolKind.StrokeEraser ? Settings.StrokeEraserPt : Settings.AreaEraserPt;

    /// <summary>Sets the diameter of the active eraser (points, 2–100); stored when the change is committed.</summary>
    public bool SetEraserSize(double diameterPt)
    {
        if (!double.IsFinite(diameterPt)) return false;
        if (Current == ToolKind.StrokeEraser) Settings.StrokeEraserPt = EraserSizes.Clamp(diameterPt, EraserMode.Stroke);
        else if (Current == ToolKind.AreaEraser) Settings.AreaEraserPt = EraserSizes.Clamp(diameterPt, EraserMode.Area);
        else return false;
        return true;
    }

    // ----- favorites (spec 24.7) -----

    /// <summary>Takes a favorite's style into its tool (the highlighter with its kind). The view then switches to <see cref="AppSettings.FavoriteSettings.Tool"/>.</summary>
    public void UseFavorite(AppSettings.FavoriteSettings favorite)
    {
        if (favorite.Tool == ToolKind.FreeHighlight)
        {
            markKind = Settings.MarkKind = favorite.MarkKind;
            switch (favorite.MarkKind)
            {
                case TextMarkupKind.Underline:
                    underline = favorite.Style.ToStyle(StrokeStyle.DefaultMarkup, AnnotationKind.TextMarkup);
                    Settings.Underline = AppSettings.StyleSettings.From(underline);
                    break;
                case TextMarkupKind.StrikeOut:
                    strikeOut = favorite.Style.ToStyle(StrokeStyle.DefaultMarkup, AnnotationKind.TextMarkup);
                    Settings.StrikeOut = AppSettings.StyleSettings.From(strikeOut);
                    break;
                default:
                    highlighter = favorite.Style.ToStyle(StrokeStyle.DefaultHighlighter, AnnotationKind.FreeHighlight);
                    Settings.Highlighter = AppSettings.StyleSettings.From(highlighter);
                    break;
            }
        }
        else
        {
            pen = favorite.Style.ToStyle(StrokeStyle.DefaultPen, AnnotationKind.Ink);
            Settings.Pen = AppSettings.StyleSettings.From(pen);
        }
        owner.SaveSettings();
    }

    /// <summary>A favorite of the current highlighter settings (with its kind) when the highlighter is the tool, otherwise of the pen's.</summary>
    public AppSettings.FavoriteSettings CurrentFavorite()
    {
        bool highlighterTool = Current == ToolKind.FreeHighlight;
        return new()
        {
            Tool = highlighterTool ? ToolKind.FreeHighlight : ToolKind.Pen,
            MarkKind = highlighterTool ? markKind : null,
            Style = AppSettings.StyleSettings.From(!highlighterTool ? pen : markKind is null ? highlighter : MarkupStyle)
        };
    }
}
