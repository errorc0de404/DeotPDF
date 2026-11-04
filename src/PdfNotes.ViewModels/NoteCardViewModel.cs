using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Tools;

namespace PdfNotes.ViewModels;

/// <summary>
/// The open sticky note (spec 24.9; P1 V8): which note is open on which page and whether it is in the document yet. A new
/// note is added only when it is closed with text (one Undo unit); an existing one is updated, or deleted when its text
/// was cleared. The card and its text box are the view's.
/// </summary>
public sealed class NoteCardViewModel(MainViewModel owner) : ObservableObject
{
    private StickyNote? note;
    private Guid pageId;
    private bool isNew;

    /// <summary>The open note (null when none is open).</summary>
    public StickyNote? Note => note;

    public Guid PageId => pageId;

    /// <summary>True while the open note is not in the document yet (its icon is drawn by the view meanwhile).</summary>
    public bool IsNew => isNew;

    /// <summary>The page index of the open note, or -1 (none open, or its page is gone).</summary>
    public int PageIndex => note is null || owner.Document is not { } doc ? -1 : doc.Session.Current.IndexOfPage(pageId);

    /// <summary>A new note centered on <paramref name="point"/> of page <paramref name="pageIndex"/>, in the note color (note tool click).</summary>
    public StickyNote? Create(int pageIndex, Point2 point)
    {
        if (owner.Document is not { } doc || owner.Display.NotesHidden) return null;
        double half = StickyNote.IconPt / 2;
        return new StickyNote(Guid.NewGuid(), false, Affine.Translation(point.X - half, point.Y - half), "", owner.ToolStyles.NoteColor);
    }

    /// <summary>Opens a note for typing; false (nothing open) without a document, while the notes are hidden or when the page is gone.</summary>
    public bool Open(StickyNote value, Guid page, bool asNew)
    {
        if (owner.Document is not { } doc || owner.Display.NotesHidden || doc.Session.Current.IndexOfPage(page) < 0) return false;
        note = value; pageId = page; isNew = asNew;
        OnPropertyChanged(nameof(Note));
        return true;
    }

    /// <summary>Commits the open note with its typed <paramref name="text"/>; returns the id of a note just added (the view selects it).</summary>
    public Guid? Close(string text)
    {
        if (note is not { } closing) return null;
        note = null;
        OnPropertyChanged(nameof(Note));
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.Length > ModelLimits.MaxTextLength) text = text[..ModelLimits.MaxTextLength];
        if (owner.Document is not { } doc || doc.Session.Current.IndexOfPage(pageId) < 0) return null;
        var edited = closing with { Text = text };
        if (isNew)
        {
            if (edited.IsBlank) return null;
            doc.Session.Execute(DocumentEdits.AddAnnotation(doc.Session.Current, pageId, edited, label: "undo.addNote"));
            return edited.Id;
        }
        if (doc.Session.Current.FindAnnotation(closing.Id)?.Annotation is StickyNote { Locked: false } live)
        {
            if (edited.IsBlank) doc.Session.Execute(DocumentEdits.DeleteAnnotations(doc.Session.Current, [live.Id], "undo.deleteNote"));
            else if (live.Text != text) doc.Session.Execute(DocumentEdits.UpdateAnnotations(doc.Session.Current, [live with { Text = text }], "undo.editNote"));
        }
        return null;
    }
}
