using PdfNotes.Core.Models;

namespace PdfNotes.Core.Commands;

/// <summary>
/// Owns the current document state, the linear Undo/Redo history and the save point.
///
/// Revisions: every newly executed edit gets a never-reused revision number; Undo/Redo move between the
/// revisions recorded in history entries. The document is clean only when the current revision equals the
/// revision of the last successful save. Because numbers are never reused, a discarded redo branch or trimmed
/// history can never make an unrelated state look saved.
/// </summary>
public sealed class DocumentSession
{
    private sealed class Entry(string label, List<EditOperation> operations, long before, long after, string? key, long bytes)
    {
        public string Label { get; } = label;
        public List<EditOperation> Operations { get; } = operations;
        public long Before { get; } = before;
        public long After { get; set; } = after;
        public string? Key { get; set; } = key;
        public long Bytes { get; set; } = bytes;
    }

    private readonly object gate = new();
    private readonly LinkedList<Entry> undo = new();
    private readonly Stack<Entry> redo = new();
    private DocumentModel current;
    private long revision;
    private long lastIssuedRevision;
    private long savedRevision;
    private long historyBytes;

    /// <param name="unsaved">The initial state was never saved anywhere (a merged new document): dirty from the start, nothing to undo.</param>
    public DocumentSession(DocumentModel initial, HistoryBudget? budget = null, bool unsaved = false)
    {
        if (ModelValidator.ValidateDocument(initial) is { } error) throw new ArgumentException(error, nameof(initial));
        Budget = budget ?? HistoryBudget.Default;
        if (Budget.MaxEntries < 1 || Budget.MaxBytes < 1) throw new ArgumentOutOfRangeException(nameof(budget));
        current = initial;
        revision = lastIssuedRevision = 1;
        savedRevision = unsaved ? 0 : 1;
    }

    /// <summary>Raised after any change of document state, dirty state or history availability (outside the lock).</summary>
    public event EventHandler? StateChanged;

    public Guid SessionId { get; } = Guid.NewGuid();
    public HistoryBudget Budget { get; }

    public DocumentModel Current { get { lock (gate) return current; } }
    public long Revision { get { lock (gate) return revision; } }
    public long SavedRevision { get { lock (gate) return savedRevision; } }
    public bool IsDirty { get { lock (gate) return revision != savedRevision; } }
    public bool CanUndo { get { lock (gate) return undo.Count > 0; } }
    public bool CanRedo { get { lock (gate) return redo.Count > 0; } }
    public int UndoCount { get { lock (gate) return undo.Count; } }
    public int RedoCount { get { lock (gate) return redo.Count; } }
    public long HistoryBytes { get { lock (gate) return historyBytes; } }
    public string? UndoLabel { get { lock (gate) return undo.Last?.Value.Label; } }
    public string? RedoLabel { get { lock (gate) return redo.TryPeek(out var e) ? e.Label : null; } }

    /// <summary>The operations the next <see cref="Undo"/> reverts (empty when there is none): what it will change and where.</summary>
    public IReadOnlyList<EditOperation> UndoOperations { get { lock (gate) return undo.Last?.Value.Operations.ToArray() ?? []; } }

    /// <summary>The operations the next <see cref="Redo"/> applies (empty when there is none).</summary>
    public IReadOnlyList<EditOperation> RedoOperations { get { lock (gate) return redo.TryPeek(out var e) ? e.Operations.ToArray() : []; } }

    /// <summary>
    /// Applies a command atomically. Returns false for an empty command (no history, no dirty change).
    /// Throws <see cref="EditConflictException"/> and leaves everything unchanged if any operation fails.
    /// </summary>
    public bool Execute(EditCommand command)
    {
        if (command.IsEmpty) return false;
        lock (gate)
        {
            var next = EditCommand.Apply(current, command.Operations);
            current = next;
            ClearRedo();
            long after = ++lastIssuedRevision;
            var top = undo.Last?.Value;
            if (command.CoalesceKey is not null && top is not null && top.Key == command.CoalesceKey &&
                top.After == revision && savedRevision != revision)
            {
                top.Operations.AddRange(command.Operations);
                top.After = after;
                top.Bytes += command.EstimatedBytes;
                historyBytes += command.EstimatedBytes;
            }
            else
            {
                if (top is not null) top.Key = null; // only the newest entry may absorb later commands
                var entry = new Entry(command.Label, [.. command.Operations], revision, after, command.CoalesceKey, command.EstimatedBytes);
                undo.AddLast(entry);
                historyBytes += entry.Bytes;
            }
            revision = after;
            Trim();
        }
        OnStateChanged();
        return true;
    }

    public bool Undo()
    {
        lock (gate)
        {
            if (undo.Last is not { } node) return false;
            var entry = node.Value;
            current = EditCommand.Revert(current, entry.Operations);
            undo.RemoveLast();
            entry.Key = null; // a redone entry never absorbs later typing
            if (undo.Last is { } previous) previous.Value.Key = null;
            redo.Push(entry);
            revision = entry.Before;
        }
        OnStateChanged();
        return true;
    }

    public bool Redo()
    {
        lock (gate)
        {
            if (!redo.TryPeek(out var entry)) return false;
            current = EditCommand.Apply(current, entry.Operations);
            redo.Pop();
            undo.AddLast(entry);
            revision = entry.After;
        }
        OnStateChanged();
        return true;
    }

    /// <summary>Ends the current coalescing group (cursor move, formatting, paste, IME commit, drag end, ...).</summary>
    public void SealCoalescing()
    {
        lock (gate)
            if (undo.Last is { } node) node.Value.Key = null;
    }

    public DocumentSnapshot CreateSnapshot()
    {
        lock (gate) return new(SessionId, current, revision);
    }

    /// <summary>
    /// Records a successful save of <paramref name="snapshot"/>. Call only after the target file was replaced.
    /// Edits made after the snapshot keep the document dirty.
    /// </summary>
    public void MarkSaved(DocumentSnapshot snapshot)
    {
        if (snapshot.SessionId != SessionId) throw new ArgumentException("Snapshot belongs to another document session.", nameof(snapshot));
        lock (gate)
        {
            if (snapshot.Revision > lastIssuedRevision || snapshot.Revision < 1) throw new ArgumentOutOfRangeException(nameof(snapshot));
            savedRevision = snapshot.Revision;
            if (revision == savedRevision && undo.Last is { } node) node.Value.Key = null;
        }
        OnStateChanged();
    }

    private void ClearRedo()
    {
        while (redo.TryPop(out var discarded)) historyBytes -= discarded.Bytes;
    }

    private void Trim()
    {
        // Drop the oldest entries first; always keep at least the newest one, even when it alone exceeds the budget.
        while (undo.Count > 1 && (undo.Count > Budget.MaxEntries || historyBytes > Budget.MaxBytes))
        {
            historyBytes -= undo.First!.Value.Bytes;
            undo.RemoveFirst();
        }
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
