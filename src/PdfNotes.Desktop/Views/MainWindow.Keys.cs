using Avalonia.Input;
using Avalonia.Interactivity;
using PdfNotes.Editor.Tools;
using PdfNotes.ViewModels;
using EditorModifiers = PdfNotes.Editor.Input.InputModifiers;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// Keys of the window (roadmap P4 A3, spec 17): Esc steps, Delete, Undo/Redo, open/save, the edit commands, Space to pan,
/// and the shared shortcut table (<see cref="Shortcuts"/>) run by the view model's navigation. A text box being edited
/// takes its own keys first (A4).
/// </summary>
public sealed partial class MainWindow
{
    private void InitializeKeys()
    {
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
    }

    private static EditorModifiers Modifiers(KeyModifiers k) =>
        (k.HasFlag(KeyModifiers.Alt) ? EditorModifiers.Alt : 0) | (k.HasFlag(KeyModifiers.Control) ? EditorModifiers.Control : 0) |
        (k.HasFlag(KeyModifiers.Shift) ? EditorModifiers.Shift : 0);

    private static ShortcutKey? ShortcutKeyOf(Key key) => key switch
    {
        Key.Left => ShortcutKey.Left, Key.Right => ShortcutKey.Right, Key.Up => ShortcutKey.Up, Key.Down => ShortcutKey.Down,
        Key.PageUp => ShortcutKey.PageUp, Key.PageDown => ShortcutKey.PageDown, Key.Home => ShortcutKey.Home, Key.End => ShortcutKey.End,
        Key.OemPlus or Key.Add => ShortcutKey.Plus, Key.OemMinus or Key.Subtract => ShortcutKey.Minus,
        Key.D0 or Key.NumPad0 => ShortcutKey.Zero, Key.D1 => ShortcutKey.D1, Key.D2 => ShortcutKey.D2, Key.D3 => ShortcutKey.D3, Key.D4 => ShortcutKey.D4,
        _ => null
    };

    private bool repeatingKey;
    private Key lastKey;

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool repeat = repeatingKey && lastKey == e.Key;
        repeatingKey = true;
        lastKey = e.Key;
        if (IsEditingText && HandleTextKey(e)) return; // A4: the editor's keys first
        if (IsEditingText) return;
        var mods = Modifiers(e.KeyModifiers);
        e.Handled = mods switch
        {
            EditorModifiers.None when e.Key == Key.Escape => Escape(),
            EditorModifiers.None when e.Key is Key.Delete or Key.Back && selectTool.SelectedIds.Count > 0 => Run(viewModel.Selection.Delete),
            EditorModifiers.None when e.Key == Key.Space => Run(() => Canvas.SetTemporaryHand(true)),
            EditorModifiers.Control when e.Key == Key.Z => Run(Undo),
            EditorModifiers.Control when e.Key == Key.Y => Run(Redo),
            EditorModifiers.Control | EditorModifiers.Shift when e.Key == Key.Z => Run(Redo),
            EditorModifiers.Control when e.Key == Key.S => Run(() => _ = SaveAsync(saveAs: false)),
            EditorModifiers.Control | EditorModifiers.Shift when e.Key == Key.S => Run(() => _ = SaveAsync(saveAs: true)),
            EditorModifiers.Control when e.Key == Key.O => Run(() => _ = OpenWithDialogAsync()),
            EditorModifiers.Control when e.Key == Key.C => Edit(SelectionCommand.Copy),
            EditorModifiers.Control when e.Key == Key.X => Edit(SelectionCommand.Cut),
            EditorModifiers.Control when e.Key == Key.A => Edit(SelectionCommand.SelectAll),
            EditorModifiers.Control when e.Key == Key.V => Run(() => viewModel.Selection.Paste(Canvas.CurrentPageIndex, null)),
            _ => ShortcutKeyOf(e.Key) is { } key && Shortcuts.Find(key, mods) is { } shortcut && viewModel.Navigation.Run(shortcut.Command, repeat)
        };
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        repeatingKey = false;
        if (e.Key == Key.Space) Canvas.SetTemporaryHand(false);
    }

    private static bool Run(Action action)
    {
        action();
        return true;
    }

    private bool Edit(SelectionCommand command)
    {
        if (!viewModel.Selection.CanExecute(command)) return false;
        viewModel.Selection.Execute(command);
        return true;
    }

    /// <summary>Esc (spec 4.3): cancel what is in progress, else drop the selection and go back to the tool that made it.</summary>
    private bool Escape()
    {
        if (Canvas.IsToolActive) { Canvas.CancelTool(); return true; }
        if (selectTool.SelectedIds.Count > 0 || selectTool.HasTextSelection)
        {
            selectTool.Select(null);
            selectTool.ClearTextSelection();
            ReturnAfterAutoSelect();
            return true;
        }
        if (tools.Current != ToolKind.Select) { SetTool(ToolKind.Select); return true; }
        return false;
    }
}
