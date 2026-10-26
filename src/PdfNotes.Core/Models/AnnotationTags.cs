namespace PdfNotes.Core.Models;

/// <summary>
/// Who made an annotation and how a later run finds it again (schema v7, cli_plan 6). Kept in the document's tag table
/// by annotation ID, not in the annotation records: moving, transforming or restyling keeps the ID and so the tag; a
/// copy, a paste or an eraser piece gets a new ID and so no tag (it counts as the user's own writing). The app never
/// shows tags.
/// </summary>
/// <param name="Author">Who made it, for example <c>ai</c>.</param>
/// <param name="BatchId">The run that made or last replaced it (history only, never an identity).</param>
/// <param name="SourceKey">The caller's identity of this one annotation; unique among the annotations of one author.</param>
/// <param name="GroupKey">A key shared by annotations that belong together (not unique).</param>
public sealed record AnnotationTag(string Author, string? BatchId = null, string? SourceKey = null, string? GroupKey = null)
{
    public const int MaxAuthorLength = 64;
    public const int MaxKeyLength = 512;

    /// <summary>The author of annotations made by an AI caller of the CLI.</summary>
    public const string AiAuthor = "ai";

    /// <summary>Null when valid, otherwise a short English reason (never content).</summary>
    public string? Validate()
    {
        if (!Valid(Author, MaxAuthorLength) || Author.Length == 0) return "Tag author is invalid.";
        if (BatchId is not null && (!Valid(BatchId, MaxKeyLength) || BatchId.Length == 0)) return "Tag batch id is invalid.";
        if (SourceKey is not null && (!Valid(SourceKey, MaxKeyLength) || SourceKey.Length == 0)) return "Tag source key is invalid.";
        if (GroupKey is not null && (!Valid(GroupKey, MaxKeyLength) || GroupKey.Length == 0)) return "Tag group key is invalid.";
        return null;
    }

    private static bool Valid(string text, int max) => text.Length <= max && !text.Any(char.IsControl);
}
