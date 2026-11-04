using PdfNotes.Editor.Tools;

namespace PdfNotes.Desktop.Canvas;

/// <summary>The active canvas tool (roadmap P4 A3 adds the pointer and key input in DocumentCanvas.Input).</summary>
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
            tool = value;
        }
    }

    public bool IsToolActive => tool?.IsActive == true;

    /// <summary>Cancels the active tool's operation; the document is not changed (Esc, spec 4.3).</summary>
    public void CancelTool()
    {
        if (tool?.IsActive == true) tool.Cancel();
    }
}
