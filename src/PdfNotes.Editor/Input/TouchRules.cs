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
}
