using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;

namespace PdfNotes.TextEditor;

/// <summary>
/// Keeps the document in step with a <see cref="TextEditorModel"/> while its box is edited (what the WPF app's
/// <c>FlushTextEdit</c>/<c>FinishTextEditing</c> do; roadmap P2 S4). Every change goes into the document at once, so
/// other views see it, but one edit is one document Undo step (2026-11-03 user decision, spec 13.2): a new box comes
/// and goes as a whole, an edited box goes back to how it was before the edit. Undo while editing is the editor's own
/// (per word) and only moves the box inside that step. Blank text is not written while editing; <see cref="Finish"/>
/// removes a box left blank in the same step, and an edit that changed nothing leaves no step.
/// </summary>
public sealed class TextEditSession : IDisposable
{
    public const string TypeLabel = "undo.typeText", DeleteEmptyLabel = "undo.deleteEmptyText";

    private readonly DocumentSession session;
    private readonly Guid pageId;
    private readonly TextEditorModel model;
    private readonly string key;
    private readonly TextBoxAnnotation? original; // the box in the document when editing began (null for a new box)
    private readonly int startCount;
    private bool finished;

    public TextEditSession(DocumentSession session, Guid pageId, TextEditorModel model)
    {
        this.session = session; this.pageId = pageId; this.model = model;
        key = "text:" + model.Box.Id;
        original = session.Current.FindAnnotation(model.Box.Id)?.Annotation as TextBoxAnnotation;
        session.SealCoalescing();
        startCount = session.UndoCount;
        model.Edited += OnEdited;
    }

    public TextEditorModel Model => model;

    private void OnEdited(object? sender, TextEditedEventArgs e)
    {
        if (!finished) Write(e.Box);
    }

    /// <summary>Writes the box into this edit's one document step, unless it is blank or already there.</summary>
    private void Write(TextBoxAnnotation after)
    {
        if (after.IsBlank) return; // removal waits for Finish; the last non-blank content stays in the document
        if (ModelValidator.ValidateAnnotation(after, session.Current.Resources.ContainsKey) is not null) return;
        var existing = session.Current.FindAnnotation(after.Id);
        if (existing?.Annotation is TextBoxAnnotation prior && Same(prior, after)) return;
        var command = existing is null
            ? DocumentEdits.AddAnnotation(session.Current, pageId, after, label: TypeLabel)
            : DocumentEdits.UpdateAnnotations(session.Current, [after], TypeLabel, key);
        if (existing is null) command = new EditCommand(command.Label, command.Operations, key);
        session.Execute(command);
    }

    /// <summary>
    /// Ends editing: the composition is completed and a box left blank is removed, both in the edit's step. When the
    /// document ends as it was before the edit (a new box emptied again, a change undone), the step is taken back.
    /// </summary>
    public void Finish()
    {
        if (finished) return;
        model.CompleteInput();
        finished = true;
        model.Edited -= OnEdited;
        var box = model.Box;
        if (box.IsBlank && session.Current.FindAnnotation(box.Id) is not null)
            session.Execute(new EditCommand(original is null ? TypeLabel : DeleteEmptyLabel,
                DocumentEdits.DeleteAnnotations(session.Current, [box.Id]).Operations, key));
        var now = session.Current.FindAnnotation(box.Id)?.Annotation as TextBoxAnnotation;
        // Nothing changed in the end: no step. The edit is usually one step, but a save in the middle (it ends the
        // coalescing) splits it, so every step it made is taken back. A Redo of them changes nothing either.
        if (now is null ? original is null : original is not null && Same(now, original))
            while (session.UndoCount > startCount && session.UndoLabel is TypeLabel or DeleteEmptyLabel) session.Undo();
        session.SealCoalescing();
    }

    public void Dispose() => Finish();

    private static bool Same(TextBoxAnnotation a, TextBoxAnnotation b) => ReferenceEquals(a, b) ||
        a.Text == b.Text && a.WidthPt == b.WidthPt && a.LayoutScale == b.LayoutScale && a.Alignment == b.Alignment && a.Transform == b.Transform &&
        a.BoxStyle == b.BoxStyle && a.Locked == b.Locked && a.Runs.SequenceEqual(b.Runs) && a.Highlights.SequenceEqual(b.Highlights);
}
