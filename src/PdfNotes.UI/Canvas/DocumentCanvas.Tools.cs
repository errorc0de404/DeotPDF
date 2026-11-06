using PdfNotes.Editor.Input;
using PdfNotes.Editor.Tools;

namespace PdfNotes.UI.Canvas;

/// <summary>The active canvas tool (input in DocumentCanvas.Input).</summary>
public sealed partial class DocumentCanvas
{
    private ICanvasTool? tool;

    /// <summary>Active canvas tool. Switching cancels an operation of the previous tool.</summary>
    public ICanvasTool? Tool
    {
        get => tool;
        set
        {
            if (ReferenceEquals(tool, value)) return;
            CancelTool();
            (tool as IHoverTool)?.PointerHover(null, InputModifiers.None);
            tool = value;
            UpdateCursor();
        }
    }

    /// <summary>True while a tool has an operation in progress (a stroke being drawn, a temporary tool held).</summary>
    public bool IsToolActive => tool?.IsActive == true || middleTool is not null || touchTool is not null;

    /// <summary>Cancels the active tool's operation; the document is not changed (Esc, spec 4.3).</summary>
    public void CancelTool()
    {
        CancelTouch();
        if (tool?.IsActive == true) tool.Cancel();
        EndMiddle(commit: null);
        if (toolCaptured)
        {
            toolCaptured = false;
            Release();
        }
    }
}
