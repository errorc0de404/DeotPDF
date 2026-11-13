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
