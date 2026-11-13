using PdfNotes.Core.Models;

namespace PdfNotes.Core.Commands;

/// <summary>
/// Who owns an annotation after an edit (MCP_LIVE_PLAN 5): tags only ever move toward the user. When the user changes an
/// annotation an AI made (moves, resizes, restyles, retypes, locks or reorders it), the same command removes its tag, so
/// it becomes the user's own writing: "remove AI writing" and a new CLI run leave it alone, and Undo brings the tag back
/// with the edit. Eraser pieces and pasted copies get new IDs and so never had a tag. Commands the AI sends
/// (<see cref="AiLabel"/>) are left as they are.
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

    /// <summary>A <see cref="DocumentSession.Filter"/>: adds tag removals for tagged annotations the user's command changes.</summary>
    public static EditCommand ForUserEdit(DocumentModel model, EditCommand command)
    {
        if (command.Label == AiLabel || model.Tags.IsEmpty) return command;
        var removed = new HashSet<Guid>();
        var changed = new List<Guid>();
        foreach (var operation in command.Operations)
        {
            switch (operation)
            {
                case ReplaceAnnotationOperation replace when replace.Before.Id == replace.After.Id:
                    changed.Add(replace.After.Id);
                    break;
                case RemoveAnnotationOperation remove:
                    removed.Add(remove.Annotation.Id);
                    break;
                case InsertAnnotationOperation insert when removed.Contains(insert.Annotation.Id):
                    changed.Add(insert.Annotation.Id); // taken out and put back: moved in the stacking order
                    break;
                case SetTagOperation:
                    return command; // a command that sets tags itself decides them
            }
        }
        var untag = changed.Distinct().Where(model.Tags.ContainsKey).Select(id => (EditOperation)new SetTagOperation(id, model.Tags[id], null)).ToList();
        return untag.Count == 0 ? command : new EditCommand(command.Label, command.Operations.Concat(untag), command.CoalesceKey);
    }
}
