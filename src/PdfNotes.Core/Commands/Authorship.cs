using PdfNotes.Core.Models;

namespace PdfNotes.Core.Commands;

/// <summary>
/// Who owns an annotation (MCP_LIVE_PLAN 5): what an AI made stays AI writing whatever the user does to it — moving,
/// restyling, retyping, locking, reordering, erasing part of it — until the user takes it with 내 필기로 (the canvas menu
/// or the work list). So "erase AI writing" removes all of it, touched or not. The pieces an eraser leaves stay AI
/// writing too (EraseGesture); a pasted copy is the user's own. What an AI changes of the user's writing stays the user's.
/// </summary>
public static class Authorship
{
    /// <summary>The Undo label of an edit an AI client sent to the open document (one request, one Undo unit).</summary>
    public const string AiLabel = "undo.ai";

    /// <summary>The Undo label of erasing AI writing from the AI tab or the work list.</summary>
    public const string EraseLabel = "undo.aiErase";

    /// <summary>The Undo label of taking AI writing as the user's own.</summary>
    public const string AdoptLabel = "undo.aiAdopt";

    /// <summary>True when an AI client made <paramref name="annotationId"/> and nobody has changed it since.</summary>
    public static bool IsAi(DocumentModel model, Guid annotationId) =>
        model.Tags.TryGetValue(annotationId, out var tag) && tag.Author == AnnotationTag.AiAuthor;

    /// <summary>The AI writing of the document (or of one page), page by page in stacking order.</summary>
    public static IEnumerable<(Page Page, Annotation Annotation)> AiAnnotations(DocumentModel model, Guid? pageId = null)
    {
        if (model.Tags.IsEmpty) yield break;
        foreach (var page in model.Pages)
        {
            if (pageId is { } only && page.Id != only) continue;
            foreach (var annotation in page.Annotations)
                if (IsAi(model, annotation.Id)) yield return (page, annotation);
        }
    }

    /// <summary>
    /// Deletes the given annotations that are still AI writing, locked ones too (MCP_LIVE_PLAN 6: only an AI locks AI
    /// writing, and page-wide commands ignore locks, spec 5.2), with their tags, as one Undo unit. Null when none is left.
    /// </summary>
    public static EditCommand? Erase(DocumentModel model, IEnumerable<Guid> annotationIds)
    {
        var ids = annotationIds.Where(id => IsAi(model, id) && model.FindAnnotation(id) is not null).Distinct().ToList();
        if (ids.Count == 0) return null;
        var delete = DocumentEdits.DeleteAnnotations(model, ids, EraseLabel, includeLocked: true);
        var untag = ids.Select(id => (EditOperation)new SetTagOperation(id, model.Tags[id], null));
        return new EditCommand(EraseLabel, untag.Concat(delete.Operations)); // tags first: Undo puts the annotations back before their tags
    }

    /// <summary>Takes the given AI writing as the user's own: the tags go, the annotations stay. Null when none is AI writing.</summary>
    public static EditCommand? Adopt(DocumentModel model, IEnumerable<Guid> annotationIds)
    {
        var untag = annotationIds.Distinct().Where(id => IsAi(model, id)).Select(id => (EditOperation)new SetTagOperation(id, model.Tags[id], null)).ToList();
        return untag.Count == 0 ? null : new EditCommand(AdoptLabel, untag);
    }
}
