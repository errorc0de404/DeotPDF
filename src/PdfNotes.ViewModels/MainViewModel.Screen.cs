using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Tools;
using PdfNotes.Text;

namespace PdfNotes.ViewModels;

/// <summary>
/// The steps the view did until R4f and the view models now do: the text-aware Undo with showing what it changed, and
/// hiding the notes.
/// </summary>
public sealed partial class MainViewModel
{
    // ----- Undo and Redo (spec 13.2–13.3, D14g) -----

    /// <summary>
    /// While a text box is edited, Undo and Redo are the editor's own word steps, whatever has the keyboard (the quick
    /// buttons too); with none left nothing happens, never a document step (spec 13.2). Otherwise the document step runs
    /// and what it changed is shown.
    /// </summary>
    public void UndoOrRedo(bool redo)
    {
        if (Document is not { } doc) return;
        Editing.CancelTool();
        if (TextStyle.UndoInEditor(redo)) return;
        NoteCard.Commit();
        var operations = redo ? doc.Session.RedoOperations : doc.Session.UndoOperations;
        if (redo ? doc.Session.Redo() : doc.Session.Undo()) RevealHistoryChange(operations);
    }

    /// <summary>
    /// After an Undo or Redo (spec 13.3, D14g): what the step left off screen is scrolled to and selected (an automatic
    /// selection visit); on screen the tool stays. A page command only makes its first remaining page active. The tab stays.
    /// </summary>
    private void RevealHistoryChange(IReadOnlyList<EditOperation> operations)
    {
        if (Document is not { } doc) return;
        var model = doc.Session.Current;
        var (pageIds, annotations) = EditTargets.Of(operations);
        var left = annotations.Select(model.FindAnnotation).OfType<AnnotationLocation>().ToList();
        if (left.Count > 0 && !EditTargets.ChangesPages(operations))
        {
            var page = left[0];
            var shown = left.Where(l => l.PageIndex == page.PageIndex).Select(l => l.Annotation).ToList();
            var b = AnnotationGeometry.Bounds(shown, TextLayout.Bounds);
            var box = new Quad(new(b.X, b.Y), new(b.X + b.Width, b.Y), new(b.X, b.Y + b.Height), new(b.X + b.Width, b.Y + b.Height));
            bool offScreen = !Viewport.IsPartlyInView(page.PageIndex, [box]);
            if (offScreen) Viewport.ScrollToPagePoint(page.PageIndex, new(b.X + b.Width / 2, b.Y + b.Height / 2));
            var selectable = shown.Where(a => !a.Locked).Select(a => a.Id).ToList();
            if (selectable.Count == 0 || Display.NotesHidden || !offScreen && Tools.Current != ToolKind.Select || Selection.Attached is not { } select) return;
            var back = Tools.Current;
            Ribbon.Hold = true; // an Undo is not canvas work (spec 3.3)
            try
            {
                if (back != ToolKind.Select) { ToolBar.SetTool(ToolKind.Select); Tools.Return = back; }
                select.SetSelection(selectable);
            }
            finally { Ribbon.Hold = false; }
            return;
        }
        if (pageIds.FirstOrDefault(p => model.IndexOfPage(p) >= 0) is { } pageId && pageId != Guid.Empty)
        {
            int index = model.IndexOfPage(pageId);
            if (Viewport.CurrentPageIndex != index) Viewport.GoToPage(index);
            Pages.Activate(pageId, scroll: false);
        }
    }

    // ----- hide the notes (spec 24.10) -----

    /// <summary>
    /// Hides or shows the notes. While hidden the selection tool selects no annotation and PDF text can still be selected,
    /// copied and searched; choosing a drawing tool, pasting or inserting shows them again (spec 24.10, D14g). A view
    /// change only: no Undo, no unsaved mark.
    /// </summary>
    public void SetNotesHidden(bool hidden)
    {
        if (Display.NotesHidden == hidden) return;
        Editing.FinishTextEditing();
        NoteCard.Commit();
        Editing.CancelTool();
        Display.NotesHidden = hidden;
        if (hidden) { ToolBar.SetTool(ToolKind.Select); Selection.Attached?.Select(null); }
        if (Selection.Attached is { } select) select.IgnoreAnnotations = hidden;
        ToolStyles.Refresh();
        Viewport.RefreshOverlays();
        _ = Pages.InvalidateThumbnailsAsync();
    }
}
