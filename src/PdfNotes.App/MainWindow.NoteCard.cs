using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PdfNotes.App.Tools;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using Page = PdfNotes.Core.Models.Page;

namespace PdfNotes.App;

/// <summary>
/// The open sticky note (spec 24.9): a card beside the icon with a plain text box. A new note is added to the document
/// only when it is closed with text (one Undo unit); an existing one is updated, or deleted when its text was cleared.
/// Esc, a click elsewhere, a tool change, saving or another document closes it. Documents always open with notes closed.
/// </summary>
public partial class MainWindow
{
    private Border? noteCard;
    private TextBox? noteBox;
    private bool closingNote;
    private NoteCardViewModel noteState => viewModel.NoteCard;

    /// <summary>The note being edited (validation harness).</summary>
    public StickyNote? OpenNoteAnnotation => noteState.Note;
    public TextBox? NoteTextBox => noteBox;


    /// <summary>The note tool's click: an icon centered on the point, opened for typing (not in the document yet).</summary>
    private void CreateNote(int pageIndex, Point2 point)
    {
        if (noteState.Create(pageIndex, point) is { } note) OpenNote(note, document!.Session.Current.Pages[pageIndex].Id, isNew: true);
    }

    /// <summary>A press with the note tool on an existing note selects it with the selection tool (a drag moves it).</summary>
    private bool SelectNoteAt(CanvasPointer pointer)
    {
        if (document is null || View.HitTestPage(pointer.Position) is not { } anchor) return false;
        var page = document.Session.Current.Pages[anchor.PageIndex];
        if (page.Annotations.OfType<StickyNote>().LastOrDefault(n => AnnotationGeometry.Hit(n, anchor.PagePoint, 0)) is not { Locked: false } note) return false;
        SetTool(ToolKind.Select);
        tools.Return = ToolKind.Note;
        selectTool.Select(note.Id);
        return selectTool.PointerDown(pointer with { Modifiers = InputModifiers.None, ClickCount = 1 });
    }

    public void OpenNote(StickyNote note, Guid pageId, bool isNew)
    {
        CloseNote();
        if (!noteState.Open(note, pageId, isNew)) return;
        noteBox = new TextBox
        {
            Name = "NoteTextBox", Text = note.Text, AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap,
            MinHeight = 80, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Padding = new Thickness(6, 4, 6, 4)
        };
        noteBox.SetResourceReference(ForegroundProperty, "Text.Primary");
        noteBox.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseNote(); View.Focus(); e.Handled = true; } };
        noteBox.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() => { if (noteCard is not null && !noteCard.IsKeyboardFocusWithin) CloseNote(); });
        var accent = new SolidColorBrush(Color.FromRgb(note.Color.R, note.Color.G, note.Color.B)); accent.Freeze();
        noteCard = new Border
        {
            Name = "NoteCard", Width = 220, BorderBrush = accent, BorderThickness = new Thickness(1, 4, 1, 1), Child = noteBox,
            CornerRadius = new CornerRadius(3), SnapsToDevicePixels = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.2 }
        };
        noteCard.SetResourceReference(Border.BackgroundProperty, "Surface.Background");
        NoteLayer.Children.Add(noteCard);
        PositionNoteCard();
        View.RefreshOverlays(); // a new note's icon is drawn while it is open
        noteBox.Focus();
        noteBox.CaretIndex = noteBox.Text.Length;
    }

    /// <summary>Keeps the card beside its icon (right of it, clamped into the document area) while the view scrolls or zooms.</summary>
    private void PositionNoteCard()
    {
        if (noteCard is null || noteState.Note is not { } open || noteState.PageIndex is not (>= 0 and var index)) return;
        var corner = View.PageTransform(index).PageToDip.Transform(new Point2(open.Transform.OffsetX + StickyNote.IconPt, open.Transform.OffsetY));
        var origin = View.TranslatePoint(new Point(0, 0), NoteLayer);
        double x = Math.Clamp(corner.X + origin.X + 6, 0, Math.Max(0, NoteLayer.ActualWidth - noteCard.Width));
        double y = Math.Clamp(corner.Y + origin.Y, 0, Math.Max(0, NoteLayer.ActualHeight - 100));
        System.Windows.Controls.Canvas.SetLeft(noteCard, x);
        System.Windows.Controls.Canvas.SetTop(noteCard, y);
    }

    /// <summary>Commits the open note: a new one with text is added, an existing one updated or deleted when empty.</summary>
    public void CloseNote()
    {
        if (closingNote || noteCard is null || noteState.Note is null) return;
        closingNote = true;
        try
        {
            string text = noteBox!.Text;
            NoteLayer.Children.Remove(noteCard);
            noteCard = null; noteBox = null;
            if (noteState.Close(text) is { } added) selectTool.Select(added);
            View.RefreshOverlays();
        }
        finally { closingNote = false; }
    }

    /// <summary>The icon of a new note that is open but not in the document yet.</summary>
    private void PaintOpenNote(DrawingContext context, Page page, ViewTransform transform)
    {
        if (noteState is { IsNew: true, Note: { } note } && page.Id == noteState.PageId) Canvas.AnnotationPainter.PaintNote(context, note, transform);
    }
}
