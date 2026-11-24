using PdfNotes.Core.Models;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;

namespace PdfNotes.Editor.Session;

/// <summary>What Esc does now (spec 5.3, 17; 2026-10-14): one step back per press.</summary>
public enum EscapeStep { None, CancelOperation, ClearSelection, ReturnToTool, SelectTool }

/// <summary>
/// The current canvas tool and the automatic selection visit (spec 5.4, 5.6, 9.3, D14d). A lasso result, a clicked line,
/// shape or highlight is edited with the selection tool; <see cref="Return"/> remembers the tool to go back to.
/// </summary>
public sealed class ToolSwitch(AppSettings settings, Action saveSettings)
{
    public ToolKind Current { get; private set; } = ToolKind.Pen;

    /// <summary>The tool an automatic selection visit returns to; null for a selection tool the user chose.</summary>
    public ToolKind? Return
    {
        get => returnTool;
        set
        {
            if (returnTool == value) return;
            returnTool = value;
            ReturnChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private ToolKind? returnTool;

    /// <summary>Raised when <see cref="Return"/> changes, so the shell can mark the tool to go back to (spec 5.4, D14g).</summary>
    public event EventHandler? ReturnChanged;

    /// <summary>
    /// Makes <paramref name="kind"/> current (hidden notes allow only the selection tool, spec 24.10) and remembers the
    /// shape-group and eraser choice in the settings. Returns the tool that became current.
    /// </summary>
    public ToolKind Choose(ToolKind kind, bool notesHidden)
    {
        if (notesHidden || kind == ToolKind.Table && Table is null) kind = ToolKind.Select; // the table tool needs a size
        if (kind != ToolKind.Table) Table = null; // Esc or another tool cancels the armed table (1.4 plan 1.3)
        Return = null; // only an automatic switch remembers where to go back
        Current = kind;
        if (kind is ToolKind.Line or ToolKind.Curve or ToolKind.Shape && settings.ShapeGroupTool != kind)
        {
            settings.ShapeGroupTool = kind; // the shape button selects this tool next time
            saveSettings();
        }
        if (kind is ToolKind.StrokeEraser or ToolKind.AreaEraser && settings.Eraser != kind)
        {
            settings.Eraser = kind;
            saveSettings();
        }
        return kind;
    }

    /// <summary>The size of the armed table insert (<see cref="ToolKind.Table"/>), or null when no table is armed.</summary>
    public TableSize? Table { get; private set; }

    /// <summary>
    /// Arms a table of <paramref name="size"/> (the 표 grid, 1.4 plan 1.3): the table tool becomes current and the next click
    /// on a page puts it there. The size is kept inside 1..<see cref="TableAnnotation.MaxPick"/>. Returns the tool that became current.
    /// </summary>
    public ToolKind ArmTable(TableSize size, bool notesHidden)
    {
        Table = new(Math.Clamp(size.Rows, 1, TableAnnotation.MaxPick), Math.Clamp(size.Columns, 1, TableAnnotation.MaxPick));
        return Choose(ToolKind.Table, notesHidden);
    }

    /// <summary>The tool of a toolbar button tag: the eraser and shape buttons select the one used last.</summary>
    public ToolKind ForButton(string tag) =>
        tag == "Eraser" ? settings.Eraser : tag == "ShapeGroup" ? settings.ShapeGroupTool : Enum.TryParse<ToolKind>(tag, out var parsed) ? parsed : Current;

    /// <summary>Selection and lasso share the selection; every other tool starts without one.</summary>
    public static bool KeepsSelection(ToolKind kind) => kind is ToolKind.Select or ToolKind.Lasso;

    /// <summary>Ends an automatic selection visit: the tool to go back to, or null (the user's own selection tool keeps going).</summary>
    public ToolKind? TakeReturn()
    {
        if (Return is not { } previous || Current != ToolKind.Select) return null;
        Return = null;
        return previous;
    }

    /// <summary>The Esc step: the operation in progress, then the selection, then the visit, then the selection tool.</summary>
    public EscapeStep Escape(bool operationActive, bool hasSelection) =>
        operationActive ? EscapeStep.CancelOperation
        : hasSelection ? EscapeStep.ClearSelection
        : Return is not null ? EscapeStep.ReturnToTool
        : Current != ToolKind.Select ? EscapeStep.SelectTool
        : EscapeStep.None;
}
