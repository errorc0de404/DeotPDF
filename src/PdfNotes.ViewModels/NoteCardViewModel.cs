using System.Collections.ObjectModel;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Tools;

namespace PdfNotes.ViewModels;

/// <summary>
/// One open sticky note card (spec 24.9): its note, the text typed in it, and how the card is shown — its size and
/// where the user dragged it. All of that is view state: not saved, not in Undo, forgotten with the document.
/// </summary>
public sealed class NoteCardItem : ObservableObject
{
    public const double DefaultWidth = 220, MinWidth = 160, MaxWidth = 480, MinHeight = 80, MaxSizedHeight = 600;

    /// <summary>The most a card grows by its text alone, before the user sized it.</summary>
    public const double MaxAutoHeight = 264;

    private StickyNote note;
    private string text;
    private bool ai;
    private double width = DefaultWidth, height = double.NaN;
    private Point2? offset;

    internal NoteCardItem(NoteCardViewModel cards, StickyNote note, Guid pageId, bool isNew, bool ai)
    {
        this.note = note;
        this.ai = ai;
        text = Committed = note.Text;
        PageId = pageId;
        IsNew = isNew;
        CloseCommand = new(() => cards.Close(this));
    }

    public Guid Id => note.Id;

    /// <summary>The note as the document has it (a new one: as it will be added).</summary>
    public StickyNote Note
    {
        get => note;
        internal set
        {
            var before = note;
            note = value;
            if (before.Color != value.Color) OnPropertyChanged(nameof(Color));
            if (before.Locked != value.Locked) OnPropertyChanged(nameof(IsLocked));
        }
    }

    public Guid PageId { get; internal set; }

    /// <summary>True while the note is not in the document yet (its icon is drawn by the view meanwhile).</summary>
    public bool IsNew { get; internal set; }

    /// <summary>The note's text as the document has it: what <see cref="Text"/> is compared with.</summary>
    internal string Committed { get; set; }

    public string Text { get => text; set => SetProperty(ref text, value ?? ""); }

    /// <summary>A locked note is read in its card, not typed in (its text is never written).</summary>
    public bool IsLocked => note.Locked;

    /// <summary>The note's color: the card's top edge.</summary>
    public RgbColor Color => note.Color;

    /// <summary>The note is AI writing (spec 24.14): the card says so.</summary>
    public bool IsAi { get => ai; internal set => SetProperty(ref ai, value); }

    public double Width { get => width; private set => SetProperty(ref width, value); }

    /// <summary>NaN until the user sizes the card: it is then as tall as its text, up to <see cref="MaxAutoHeight"/>.</summary>
    public double Height { get => height; private set { height = value; OnPropertyChanged(); OnPropertyChanged(nameof(MaxHeight)); } }

    public double MaxHeight => double.IsNaN(height) ? MaxAutoHeight : MaxSizedHeight;

    /// <summary>
    /// Where the user dragged the card: its top left corner from the icon's, in page points, so it follows the icon
    /// through scrolling and zooming. Null: beside the icon.
    /// </summary>
    public Point2? Offset { get => offset; set => SetProperty(ref offset, value); }

    public Command CloseCommand { get; }

    /// <summary>The corner drag: 160–480 by 80–600 DIP.</summary>
    public void Resize(double w, double h)
    {
        Width = Math.Clamp(w, MinWidth, MaxWidth);
        Height = Math.Clamp(h, MinHeight, MaxSizedHeight);
    }
}

/// <summary>
/// The open sticky note cards (spec 24.9; P1 V8). One card at most is typed in (<see cref="Active"/>); a card stays open
/// after its typing ends until it is closed (its close button, Esc, its icon pressed again). A new note is added only when its typing ends with text (one
/// Undo unit); an existing one is updated, or deleted when its text was cleared. The cards themselves are the view's.
/// </summary>
public sealed class NoteCardViewModel : ObservableObject
{
    private readonly MainViewModel owner;
    private NoteCardItem? active;

    public NoteCardViewModel(MainViewModel owner)
    {
        this.owner = owner;
        ExpandCommand = new(ToggleExpand);
        owner.DocumentChanged += Sync;
    }

    public ObservableCollection<NoteCardItem> Cards { get; } = [];

    /// <summary>The cards, their notes or their places changed: the view lays them out again.</summary>
    public event Action? LayoutChanged;

    /// <summary>The card being typed in (null: none).</summary>
    public NoteCardItem? Active => active;

    /// <summary>A note is being typed in.</summary>
    public bool IsOpen => active is not null;

    /// <summary>The note being typed in (null when none is).</summary>
    public StickyNote? Note => active?.Note;

    /// <summary>Some card is open (보기 › 메모 펼치기 is on).</summary>
    public bool HasOpen => Cards.Count > 0;

    /// <summary>보기 › 메모 펼치기: opens every note of the current page; with cards open, closes them all.</summary>
    public Command ExpandCommand { get; }

    private void Changed()
    {
        OnPropertyChanged(nameof(Active));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(HasOpen));
        LayoutChanged?.Invoke();
    }

    /// <summary>A new note centered on <paramref name="point"/> of page <paramref name="pageIndex"/>, in the note color (note tool click).</summary>
    public StickyNote? Create(int pageIndex, Point2 point)
    {
        if (owner.Document is null || owner.Display.NotesHidden) return null;
        double half = StickyNote.IconPt / 2;
        return new StickyNote(Guid.NewGuid(), false, Affine.Translation(point.X - half, point.Y - half), "", owner.ToolStyles.NoteColor);
    }

    /// <summary>
    /// Opens a note for typing (a card already open for it is typed in again); false without a document, while the notes
    /// are hidden or when the page is gone. The card typed in before ends first.
    /// </summary>
    public bool Open(StickyNote value, Guid page, bool asNew)
    {
        if (owner.Document is not { } doc || owner.Display.NotesHidden || doc.Session.Current.IndexOfPage(page) < 0) return false;
        if (Cards.FirstOrDefault(c => c.Id == value.Id) is { } open)
        {
            Activate(open);
            return true;
        }
        if (active is { } before) Finish(before);
        var card = new NoteCardItem(this, value, page, asNew, !asNew && Authorship.IsAi(doc.Session.Current, value.Id));
        Cards.Add(card);
        active = card;
        Changed();
        return true;
    }

    /// <summary>The keyboard went into <paramref name="card"/>: the card typed in before ends.</summary>
    public void Activate(NoteCardItem card)
    {
        if (ReferenceEquals(active, card) || !Cards.Contains(card)) return;
        if (active is { } before) End(before);
        if (!Cards.Contains(card)) return;
        active = card;
        Changed();
    }

    /// <summary>
    /// Typing in <paramref name="card"/> ended (the keyboard left it, a save): its text goes into the document and the card
    /// stays open (an emptied note's card closes). Returns the id of a note just added.
    /// </summary>
    public Guid? End(NoteCardItem card)
    {
        if (!Cards.Contains(card)) return null;
        var added = Write(card);
        if (ReferenceEquals(active, card)) active = null;
        Changed();
        owner.Navigation.RefreshOverlays(); // a new note's icon was drawn by the view while it was open
        return added;
    }

    /// <summary><see cref="End"/>, and a note it added is selected.</summary>
    public void Finish(NoteCardItem card)
    {
        if (End(card) is { } added) owner.Selection.Tool.Select(added);
    }

    /// <summary>The card's close button, Esc, its icon pressed again: the text is kept as when typing ends, and the card closes.</summary>
    public Guid? Close(NoteCardItem card)
    {
        if (!Cards.Contains(card)) return null;
        var added = End(card);
        if (Cards.Remove(card)) Changed();
        return added;
    }

    /// <summary>The card open for the note <paramref name="id"/> (null: none).</summary>
    public NoteCardItem? CardFor(Guid id) => Cards.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Before a save, an Undo, a tool change: the typing ends (a new note with text is added and selected) and what was
    /// typed in any card is in the document. The cards stay open.
    /// </summary>
    public void Commit()
    {
        if (Cards.Count == 0) return;
        if (active is { } typing) Finish(typing);
        foreach (var card in Cards.ToList())
            if (Cards.Contains(card) && Normalized(card.Text) != card.Committed) End(card);
    }

    /// <summary>Commits and closes every card: the notes are hidden, another document is shown.</summary>
    public void CloseAll()
    {
        Commit();
        if (Cards.Count == 0) return;
        Cards.Clear();
        active = null;
        Changed();
    }

    /// <summary>Ends the typing with <paramref name="text"/> and closes that card; returns the id of a note just added.</summary>
    public Guid? Close(string text)
    {
        if (active is not { } card) return null;
        card.Text = text;
        return Close(card);
    }

    private void ToggleExpand()
    {
        owner.EndInput();
        if (HasOpen)
        {
            foreach (var card in Cards.ToList()) Close(card);
            return;
        }
        Commit();
        if (owner.Document is not { } doc || owner.Display.NotesHidden || doc.Session.Current.Pages.Count == 0) return;
        var model = doc.Session.Current;
        var page = model.Pages[Math.Clamp(owner.Viewport.CurrentPageIndex, 0, model.Pages.Count - 1)];
        foreach (var note in page.Annotations.OfType<StickyNote>())
        {
            if (owner.AiNotes.Hides(model, note) || Cards.Any(c => c.Id == note.Id)) continue;
            Cards.Add(new NoteCardItem(this, note, page.Id, isNew: false, Authorship.IsAi(model, note.Id)));
        }
        Changed();
    }

    /// <summary>
    /// The document or what is shown of it changed: a card whose note is gone (deleted, an Undo) or hidden closes, the
    /// others follow their notes (moved, recolored, taken over from the AI, rewritten by an Undo).
    /// </summary>
    public void Sync()
    {
        if (Cards.Count == 0) return;
        var model = owner.Document?.Session.Current;
        foreach (var card in Cards.ToList())
        {
            bool keep = model is not null && !owner.Display.NotesHidden;
            if (keep && card.IsNew) keep = model!.IndexOfPage(card.PageId) >= 0;
            else if (keep)
            {
                if (model!.FindAnnotation(card.Id) is { Annotation: StickyNote note } found && !owner.AiNotes.Hides(model, note))
                {
                    card.Note = note;
                    card.PageId = found.Page.Id;
                    card.IsAi = Authorship.IsAi(model, note.Id);
                    if (note.Text != card.Committed) card.Text = card.Committed = note.Text;
                }
                else keep = false;
            }
            if (keep) continue;
            Cards.Remove(card);
            if (ReferenceEquals(active, card)) active = null;
        }
        Changed();
    }

    /// <summary>Puts the card's text into the document: a new note with text is added, an existing one updated or deleted when empty.</summary>
    private Guid? Write(NoteCardItem card)
    {
        string text = Normalized(card.Text);
        if (owner.Document is not { } doc || doc.Session.Current.IndexOfPage(card.PageId) < 0) { Drop(card); return null; }
        var edited = card.Note with { Text = text };
        if (card.IsNew)
        {
            if (edited.IsBlank) { Drop(card); return null; }
            card.IsNew = false;
            card.Note = edited;
            card.Committed = text;
            doc.Session.Execute(DocumentEdits.AddAnnotation(doc.Session.Current, card.PageId, edited, label: "undo.addNote"));
            return edited.Id;
        }
        if (doc.Session.Current.FindAnnotation(card.Id)?.Annotation is StickyNote { Locked: false } live)
        {
            if (edited.IsBlank)
            {
                Drop(card);
                doc.Session.Execute(DocumentEdits.DeleteAnnotations(doc.Session.Current, [live.Id], "undo.deleteNote"));
            }
            else if (live.Text != text)
            {
                card.Committed = text;
                doc.Session.Execute(DocumentEdits.UpdateAnnotations(doc.Session.Current, [live with { Text = text }], "undo.editNote"));
            }
        }
        return null;
    }

    /// <summary>The card's text as a note holds it: line feeds only, within the length limit.</summary>
    private static string Normalized(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return text.Length > ModelLimits.MaxTextLength ? text[..ModelLimits.MaxTextLength] : text;
    }

    /// <summary>An empty note's card closes (the note itself is gone or was never added).</summary>
    private void Drop(NoteCardItem card)
    {
        Cards.Remove(card);
        if (ReferenceEquals(active, card)) active = null;
    }
}
