using System.Collections.Immutable;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Commands;

/// <summary>
/// One user-visible, atomic edit (one Undo unit). A composite edit is simply several operations.
/// Commands with the same non-null <see cref="CoalesceKey"/> executed back to back merge into one Undo unit
/// (for example continuous typing or a slider drag) until <see cref="DocumentSession.SealCoalescing"/>.
/// </summary>
public sealed class EditCommand
{
    public EditCommand(string label, IEnumerable<EditOperation> operations, string? coalesceKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        Label = label;
        Operations = operations.ToImmutableArray();
        CoalesceKey = coalesceKey;
    }

    public string Label { get; }
    public ImmutableArray<EditOperation> Operations { get; }
    public string? CoalesceKey { get; }
    public bool IsEmpty => Operations.IsEmpty;
    public long EstimatedBytes => Operations.Sum(o => o.EstimatedBytes);

    /// <summary>Applies all operations or none. Document invariants are checked once on the final state.</summary>
    internal static DocumentModel Apply(DocumentModel model, IEnumerable<EditOperation> operations)
    {
        foreach (var operation in operations) model = operation.Apply(model);
        if (model.Pages.Count == 0) throw new EditConflictException("A document needs at least one page.");
        return model;
    }

    internal static DocumentModel Revert(DocumentModel model, IReadOnlyList<EditOperation> operations)
    {
        for (int i = operations.Count - 1; i >= 0; i--) model = operations[i].Inverse().Apply(model);
        return model;
    }
}

/// <summary>Rough retained-size estimate for the history budget. Shared resource payloads are counted conservatively.</summary>
public static class SizeEstimator
{
    public const long ObjectOverhead = 96;
    public const long PageSetupBytes = 160;

    public static long Estimate(Annotation annotation) => ObjectOverhead + annotation switch
    {
        StrokeAnnotation s => (long)s.Points.Length * 16 + (s.IsOutlined ? s.Outline.Sum(r => 32L + r.Length * 16L) : 0),
        LineAnnotation l => 64 + l.Controls.Length * 16L,
        PdfTextHighlight h => h.Quads.Length * 64L + (h.SourceTextHint?.Length ?? 0) * 2L,
        PdfTextMarkup m => m.Quads.Length * 64L + (m.SourceTextHint?.Length ?? 0) * 2L,
        StickyNote n => 64 + n.Text.Length * 2L,
        TextBoxAnnotation t => t.Text.Length * 2L + t.Runs.Sum(r => 80L + r.FontFamily.Length * 2L) + t.Highlights.Length * 64L,
        ImageAnnotation => 64,
        ShapeAnnotation s => 128 + s.Points.Length * 16L,
        _ => 0
    };

    public static long Estimate(Page page) => PageSetupBytes + page.Annotations.Sum(Estimate);

    public static long Estimate(Resource resource) => ObjectOverhead + resource.MediaType.Length * 2L + resource.Payload.Length;
}

public sealed record HistoryBudget(int MaxEntries, long MaxBytes)
{
    /// <summary>Spec 13.2: 200 entries or 128 MiB, whichever is reached first.</summary>
    public static HistoryBudget Default { get; } = new(200, 128L * 1024 * 1024);
}

/// <summary>Immutable, thread-safe view of one revision. Saving/exporting reads it while the UI keeps editing.</summary>
public sealed record DocumentSnapshot(Guid SessionId, DocumentModel Model, long Revision);
