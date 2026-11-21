using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Session;
using PdfNotes.Text;

namespace PdfNotes.Editor.Tools;

/// <summary>What the canvas tools read from the window: styles and settings as they are when a tool uses them.</summary>
public sealed record ToolSources(
    Func<int, IReadOnlyList<TextGlyph>> Glyphs,
    Func<bool> AllowCrossPage,
    Func<StrokeStyle> Pen,
    Func<bool> PenPressure,
    Func<StrokeStyle> Highlighter,
    Func<bool> HighlighterSnap,
    Func<TextHighlightStyle> TextHighlight,
    Func<(TextMarkupKind Kind, StrokeStyle Style)> Markup,
    Func<StrokeStyle> Line,
    Func<LineOptions> LineOptions,
    Func<ShapeKind> ShapeKind,
    Func<ShapeStyle> Shape,
    Func<double> StrokeEraserPt,
    Func<double> AreaEraserPt,
    Func<bool>? AiNotesHidden = null);

/// <summary>What the text and note tools hand back to the window (it owns the text editor and the note cards).</summary>
public sealed record ToolActions(
    Action<int, Point2, double> NewText,
    Func<CanvasPointer, bool> SelectText,
    Action<int, Point2> NewNote,
    Func<CanvasPointer, bool> SelectNote);

/// <summary>
/// The canvas tools of one window and how they hand over to each other (spec 5.4, D14g): a line, a shape or a mark that
/// selects what it made switches to the selection tool, and the tool comes back when the selection is empty again. The
/// window only puts <see cref="For"/> on the canvas.
/// </summary>
public sealed class ToolSet
{
    public SelectTool Select { get; }
    public StrokeTool Pen { get; }
    public HighlighterTool Highlighter { get; }

    /// <summary>Underline and strikeout: the glyph tool in markup mode (always text; away from text it makes nothing).</summary>
    public TextHighlightTool Markup { get; }
    public TextTool Text { get; }
    public NoteTool Note { get; }
    public LineTool Line { get; }
    public LineTool Curve { get; }
    public LassoTool Lasso { get; }
    public ShapeTool Shape { get; }
    public EraserTool StrokeEraser { get; }
    public EraserTool AreaEraser { get; }

    private readonly ToolSwitch tools;
    private readonly Action<ToolKind> setTool;
    private readonly Action returnAfterAutoSelect;
    private readonly Action<Action> later;

    /// <param name="setTool">Switches the window's tool (the tool row's one path).</param>
    /// <param name="returnAfterAutoSelect">Gives the tool back after an automatic switch to the selection tool.</param>
    /// <param name="later">Runs an action after the current input event (the UI thread's queue).</param>
    /// <param name="released">The window's part of a released selection drag (a text box starts editing), before the tool may come back.</param>
    public ToolSet(ICanvasView canvas, Func<DocumentSession?> session, ToolSwitch tools, ToolSources sources, ToolActions actions,
        Action<ToolKind> setTool, Action returnAfterAutoSelect, Action<Action> later, Action<bool>? released = null)
    {
        this.tools = tools;
        this.setTool = setTool;
        this.returnAfterAutoSelect = returnAfterAutoSelect;
        this.later = later;
        Select = new SelectTool(canvas, session, sources.Glyphs) { AllowCrossPage = sources.AllowCrossPage };
        Pen = new StrokeTool(canvas, session, sources.Pen, AnnotationKind.Ink, sources.PenPressure);
        var highlighterStroke = new StrokeTool(canvas, session, sources.Highlighter, AnnotationKind.FreeHighlight);
        Text = new TextTool(canvas, actions.NewText, actions.SelectText);
        Line = new LineTool(canvas, session, Select, sources.Line, sources.LineOptions, curve: false, SelectFrom(ToolKind.Line));
        Curve = new LineTool(canvas, session, Select, sources.Line, sources.LineOptions, curve: true, SelectFrom(ToolKind.Curve));
        Lasso = new LassoTool(canvas, session, Select);
        Shape = new ShapeTool(canvas, session, Select, sources.ShapeKind, sources.Shape, SelectFrom(ToolKind.Shape));
        Note = new NoteTool(canvas, actions.NewNote, actions.SelectNote);
        var highlighterText = new TextHighlightTool(canvas, session, sources.Glyphs, sources.TextHighlight, SelectFrom(ToolKind.FreeHighlight));
        Highlighter = new HighlighterTool(canvas, session, highlighterStroke, highlighterText, sources.HighlighterSnap, SelectFrom(ToolKind.FreeHighlight));
        Markup = new TextHighlightTool(canvas, session, sources.Glyphs, sources.TextHighlight, SelectFrom(ToolKind.Markup), () => sources.Markup());
        StrokeEraser = new EraserTool(canvas, session, sources.StrokeEraserPt, EraserMode.Stroke);
        AreaEraser = new EraserTool(canvas, session, sources.AreaEraserPt, EraserMode.Area);
        // Hidden AI writing is passed over by every tool that picks annotations (MCP_LIVE_PLAN 6).
        var mask = new AnnotationMask(sources.AiNotesHidden ?? (() => false));
        Select.Mask = Highlighter.Mask = Markup.Mask = StrokeEraser.Mask = AreaEraser.Mask = mask;

        Lasso.Selected += (_, _) => later(() =>
        {
            if (tools.Current != ToolKind.Lasso) return;
            setTool(ToolKind.Select);
            tools.Return = ToolKind.Lasso;
        });
        Select.Released += (_, changed) =>
        {
            released?.Invoke(changed);
            if (Select.SelectedIds.Count == 0 && !Select.HasTextSelection) returnAfterAutoSelect();
            else if (Select.HasTextSelection) tools.Return = null;
        };
    }

    /// <summary>The canvas tool of <paramref name="kind"/>.</summary>
    public ICanvasTool For(ToolKind kind) => kind switch
    {
        ToolKind.Text => Text,
        ToolKind.Select => Select,
        ToolKind.Pen => Pen,
        ToolKind.FreeHighlight => Highlighter,
        ToolKind.Markup => Markup,
        ToolKind.Line => Line,
        ToolKind.Curve => Curve,
        ToolKind.Lasso => Lasso,
        ToolKind.StrokeEraser => StrokeEraser,
        ToolKind.AreaEraser => AreaEraser,
        ToolKind.Shape => Shape,
        ToolKind.Note => Note,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>The eraser the pen's eraser end uses with the 지우개 setting <paramref name="setting"/>.</summary>
    public EraserTool Eraser(ToolKind setting) => setting == ToolKind.StrokeEraser ? StrokeEraser : AreaEraser;

    /// <summary>A tool made an annotation: the selection tool takes it, and <paramref name="tool"/> comes back after.</summary>
    private Action<Guid> SelectFrom(ToolKind tool) => id => later(() =>
    {
        setTool(ToolKind.Select);
        Select.Select(id);
        tools.Return = tool;
    });

    /// <summary>
    /// The selection tool was taken because <paramref name="used"/> selected something; it is given back when the selection
    /// empties (<see cref="ToolSwitch.Return"/>).
    /// </summary>
    public void ReturnAfter(ToolKind used) => tools.Return = used;
}
