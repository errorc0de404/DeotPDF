using PdfNotes.Core.Models;
using PdfNotes.Editor.Tools;

namespace PdfNotes.ViewModels;

/// <summary>
/// Which tool buttons are on (spec 3.2): the current tool, the shape group button (line, curve or shape, its icon showing
/// the tool it selects next), the eraser button and the eraser bar's two modes.
/// </summary>
public sealed record ToolButtons(ToolKind Current, ToolKind ShapeGroupTool, ShapeKind ShapeKind, ToolKind Eraser)
{
    public bool ShapeGroup => Current is ToolKind.Line or ToolKind.Curve or ToolKind.Shape;
    public bool EraserOn => Current is ToolKind.StrokeEraser or ToolKind.AreaEraser;
}

/// <summary>The shape button's icon: the shape group tool it selects next and the shape the shape tool draws.</summary>
public sealed record ShapeIcon(ToolKind Tool, ShapeKind Shape);

/// <summary>
/// One tool button of the 필기 tab or the eraser bar (roadmap P4.5 R4d): on or off, whether an automatic selection visit
/// goes back to it (the accent border, spec 5.4), and the press.
/// </summary>
public sealed class ToolButtonViewModel(string tag, Func<ToolButtons, bool> isOn, Func<ToolKind?, bool> isReturn, Action<string> press) : ObservableObject
{
    private bool isChecked, returnsHere;

    /// <summary>The tool the button names (`Eraser` and `ShapeGroup` take the one used last).</summary>
    public string Tag => tag;

    public bool IsChecked { get => isChecked; private set => SetProperty(ref isChecked, value); }

    /// <summary>An automatic selection visit returns to this button's tool.</summary>
    public bool IsReturn { get => returnsHere; private set => SetProperty(ref returnsHere, value); }

    public Command Command => field ??= new(() => press(tag));

    internal void Update(ToolButtons buttons, ToolKind? returnTool)
    {
        isChecked = isOn(buttons);
        // A radio button the user clicked turns itself on: the view model's state wins even when it did not change.
        OnPropertyChanged(nameof(IsChecked));
        IsReturn = isReturn(returnTool);
    }
}

/// <summary>
/// Switching the canvas tool (spec 5.6; P1 V7b, R4d): the toolbar, the quick menu, favorites and shortcuts use this one
/// path. Text input is finished first, hidden notes come back for a drawing tool, the tool switch picks the tool (the
/// eraser and shape buttons remember theirs), the buttons and the style bar follow, and the 필기 tab is shown (spec 3.2).
/// The view puts the canvas tool for <see cref="Current"/> in place when it changes.
/// </summary>
public sealed class ToolBarViewModel : ObservableObject
{
    private readonly MainViewModel owner;
    private readonly ToolButtonViewModel[] all;

    public ToolBarViewModel(MainViewModel owner)
    {
        this.owner = owner;
        ToolButtonViewModel Button(string tag, Func<ToolButtons, bool> on, params ToolKind[] returns) =>
            new(tag, on, r => r is { } k && returns.Contains(k), Press);
        Select = Button("Select", b => b.Current == ToolKind.Select);
        Lasso = Button("Lasso", b => b.Current == ToolKind.Lasso, ToolKind.Lasso);
        Pen = Button("Pen", b => b.Current == ToolKind.Pen, ToolKind.Pen);
        Highlighter = Button("FreeHighlight", b => b.Current == ToolKind.FreeHighlight, ToolKind.FreeHighlight);
        Eraser = Button("Eraser", b => b.EraserOn, ToolKind.StrokeEraser, ToolKind.AreaEraser);
        Text = Button("Text", b => b.Current == ToolKind.Text, ToolKind.Text);
        Shape = Button("ShapeGroup", b => b.ShapeGroup, ToolKind.Line, ToolKind.Curve, ToolKind.Shape);
        Note = Button("Note", b => b.Current == ToolKind.Note, ToolKind.Note);
        StrokeEraser = Button("StrokeEraser", b => b.Eraser == ToolKind.StrokeEraser);
        AreaEraser = Button("AreaEraser", b => b.Eraser == ToolKind.AreaEraser);
        all = [Select, Lasso, Pen, Highlighter, Eraser, Text, Shape, Note, StrokeEraser, AreaEraser];
        owner.Tools.ReturnChanged += (_, _) => Refresh();
        Refresh();
    }

    public ToolKind Current => owner.Tools.Current;

    public ToolButtons Buttons => new(owner.Tools.Current, owner.Settings.ShapeGroupTool, owner.Settings.ShapeKind, owner.Settings.Eraser);

    public ToolButtonViewModel Select { get; }
    public ToolButtonViewModel Lasso { get; }
    public ToolButtonViewModel Pen { get; }
    public ToolButtonViewModel Highlighter { get; }
    public ToolButtonViewModel Eraser { get; }
    public ToolButtonViewModel Text { get; }
    public ToolButtonViewModel Shape { get; }
    public ToolButtonViewModel Note { get; }
    public ToolButtonViewModel StrokeEraser { get; }
    public ToolButtonViewModel AreaEraser { get; }

    /// <summary>The shape button's icon.</summary>
    public ShapeIcon ShapeIcon => new(owner.Settings.ShapeGroupTool, owner.Settings.ShapeKind);

    /// <summary>The buttons show the current tool again (also after a temporary tool such as the pen's eraser ends).</summary>
    public void Refresh()
    {
        var buttons = Buttons;
        foreach (var button in all) button.Update(buttons, owner.Tools.Return);
        OnPropertyChanged(nameof(Buttons));
        OnPropertyChanged(nameof(ShapeIcon));
    }

    public void SetTool(ToolKind kind)
    {
        if (owner.Display.NotesHidden && kind != ToolKind.Select) owner.SetNotesHidden(false);
        bool hidden = owner.Display.NotesHidden;
        owner.Editing.FinishTextEditing();
        owner.Tools.Choose(kind, hidden);
        OnPropertyChanged(nameof(Current)); // the view puts the canvas tool in place
        Refresh();
        owner.ToolStyles.Refresh();
        owner.Ribbon.Auto(RibbonTab.Home); // a tool change, also by shortcut or quick menu (spec 3.2)
    }

    /// <summary>A tool button was pressed: its tag names the tool (`Eraser` and `ShapeGroup` the one used last).</summary>
    public void Press(string tag)
    {
        // Clicking the selection button, even while it is on, is a direct choice: no automatic return (spec 5.4).
        if (tag == "Select" && owner.Tools.Current == ToolKind.Select) owner.Tools.Return = null;
        var kind = owner.Tools.ForButton(tag);
        if (kind != owner.Tools.Current) SetTool(kind);
        else Refresh();
    }

    /// <summary>Chooses the shape the shape tool draws next and switches to it (shape button menu).</summary>
    public void SetShapeKind(ShapeKind kind)
    {
        if (kind == ShapeKind.Polyline) throw new ArgumentOutOfRangeException(nameof(kind));
        owner.Settings.ShapeKind = kind;
        owner.SaveSettings();
        SetTool(ToolKind.Shape);
    }

    /// <summary>
    /// Ends an automatic selection visit (spec 5.4): back to the tool it came from once the current input is over, if the
    /// selection tool is still the tool then.
    /// </summary>
    public void ReturnAfterAutoSelect()
    {
        if (owner.Tools.TakeReturn() is not { } previous) return;
        owner.Ui.Post(() => { if (owner.Tools.Current == ToolKind.Select && !owner.Viewport.IsToolActive) SetTool(previous); });
    }

    // ----- pen favorites (spec 24.7) -----

    /// <summary>
    /// A favorite switches to its tool (the highlighter with its kind) with its color, width and transparency; an empty
    /// one stores the current settings. While the notes are hidden they are shown first (spec 24.10).
    /// </summary>
    public void ApplyFavorite(int slot)
    {
        var slots = owner.Settings.Favorites;
        if (slot < 0 || slot >= slots.Length) return;
        if (slots[slot] is not { } favorite) { StoreFavorite(slot); return; }
        if (owner.Display.NotesHidden) owner.SetNotesHidden(false);
        owner.ToolStyles.UseFavorite(favorite);
        SetTool(favorite.Tool);
    }

    /// <summary>즐겨찾기 교체: the slot takes the current pen (or highlighter) settings.</summary>
    public void StoreFavorite(int slot) => owner.Favorites.Store(slot, owner.ToolStyles.CurrentFavorite());
}
