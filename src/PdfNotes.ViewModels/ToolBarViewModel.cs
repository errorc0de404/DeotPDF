using PdfNotes.Core.Models;
using PdfNotes.Editor.Tools;

namespace PdfNotes.ViewModels;

/// <summary>The canvas side of a tool change (P1 V7b): the platform's tool objects stay in the view.</summary>
public interface IToolView
{
    /// <summary>Puts the canvas tool for <paramref name="kind"/> in place (and clears a selection the tool does not keep).</summary>
    void UseTool(ToolKind kind);

    /// <summary>Shows the hidden notes again (a drawing tool was chosen, spec 24.10, D14g).</summary>
    void ShowNotes();

    /// <summary>Ends an automatic selection visit: back to the tool it came from once the current input is over (spec 5.4).</summary>
    void ReturnAfterAutoSelect();
}

/// <summary>
/// Which tool buttons are on (spec 3.2): the current tool, the shape group button (line, curve or shape, its icon showing
/// the tool it selects next), the eraser button and the eraser bar's two modes.
/// </summary>
public sealed record ToolButtons(ToolKind Current, ToolKind ShapeGroupTool, ShapeKind ShapeKind, ToolKind Eraser)
{
    public bool ShapeGroup => Current is ToolKind.Line or ToolKind.Curve or ToolKind.Shape;
    public bool EraserOn => Current is ToolKind.StrokeEraser or ToolKind.AreaEraser;
}

/// <summary>
/// Switching the canvas tool (spec 5.6; P1 V7b): the toolbar, the quick menu, favorites and shortcuts use this one path.
/// Text input is finished first, hidden notes come back for a drawing tool, the tool switch picks the tool (the eraser and
/// shape buttons remember theirs), the buttons and the style bar follow, and the 필기 tab is shown (spec 3.2).
/// </summary>
public sealed class ToolBarViewModel(MainViewModel owner, IMainView view) : ObservableObject
{
    public ToolKind Current => owner.Tools.Current;

    public ToolButtons Buttons => new(owner.Tools.Current, owner.Settings.ShapeGroupTool, owner.Settings.ShapeKind, owner.Settings.Eraser);

    public void SetTool(ToolKind kind)
    {
        if (owner.Display.NotesHidden && kind != ToolKind.Select) view.Tools.ShowNotes();
        bool hidden = owner.Display.NotesHidden;
        view.FinishTextEditing();
        kind = owner.Tools.Choose(kind, hidden);
        view.Tools.UseTool(kind);
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Buttons));
        owner.ToolStyles.Refresh();
        owner.Ribbon.Auto(RibbonTab.Home); // a tool change, also by shortcut or quick menu (spec 3.2)
    }

    /// <summary>A tool button was checked: its tag names the tool (`Eraser` and `ShapeGroup` the one used last).</summary>
    public void Press(string tag)
    {
        var kind = owner.Tools.ForButton(tag);
        if (kind != owner.Tools.Current) SetTool(kind);
    }

    /// <summary>Chooses the shape the shape tool draws next and switches to it (shape button menu).</summary>
    public void SetShapeKind(ShapeKind kind)
    {
        if (kind == ShapeKind.Polyline) throw new ArgumentOutOfRangeException(nameof(kind));
        owner.Settings.ShapeKind = kind;
        owner.SaveSettings();
        SetTool(ToolKind.Shape);
    }
}
