using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;

namespace PdfNotes.Editor.Tools;

/// <summary>
/// Annotations the tools pass over as if they were not there: AI writing while the AI tab hides it (MCP_LIVE_PLAN 6).
/// Selection, the lasso, the erasers and the highlighter's pick do not catch them; the document keeps them.
/// </summary>
public sealed class AnnotationMask(Func<bool> aiHidden)
{
    public static AnnotationMask None { get; } = new(() => false);

    public bool AiHidden => aiHidden();

    public bool Hides(DocumentModel model, Annotation annotation) => aiHidden() && Authorship.IsAi(model, annotation.Id);
}
