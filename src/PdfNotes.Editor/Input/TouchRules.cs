using PdfNotes.Editor.Tools;

namespace PdfNotes.Editor.Input;

/// <summary>Which tools a finger reaches when it does not draw (spec 7.6).</summary>
public static class TouchRules
{
    /// <summary>
    /// A finger tap clicks the selection, text and note tools (pick an annotation, start editing a text box). Drawing
    /// tools ignore it unless finger drawing is on: a tap there is usually a hand resting on the screen.
    /// </summary>
    public static bool TapReachesTool(ICanvasTool? tool) => tool is SelectTool or TextTool or NoteTool;

    /// <summary>
    /// A finger dragging from here moves annotations instead of the page (roadmap P5d, as in other note apps): on the
    /// selection or an unlocked annotation with the selection tool, on the selection with the lasso.
    /// </summary>
    public static bool FingerGrabs(ICanvasTool? tool, PdfNotes.Core.Geometry.Point2 at) => tool switch
    {
        SelectTool select => select.Grabs(at),
        LassoTool lasso => lasso.Grabs(at),
        _ => false,
    };
}
