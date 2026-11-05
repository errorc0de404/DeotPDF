using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using PdfNotes.Editor.Session;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using PdfNotes.Editor.Tools;
using PdfNotes.ViewModels;
using EditorModifiers = PdfNotes.Editor.Input.InputModifiers;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// Keys of the window (spec 17, the WPF WindowPreviewKeyDown/WindowKeyDown): the shared shortcut table with its scopes
/// (<see cref="Shortcuts"/>: zoom anywhere, favorites while writing, back/forward outside text input, page keys on the
/// canvas), the arrows nudging a selection, the file and edit commands, Esc steps, Delete, Space to pan. A text box being
/// edited and the app's fields take their own keys first. On macOS ⌘ is the Ctrl of every shortcut.
/// </summary>
public sealed partial class MainWindow
{
    private void InitializeKeys()
    {
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
    }

    private static EditorModifiers Modifiers(KeyModifiers k) =>
        (k.HasFlag(KeyModifiers.Alt) ? EditorModifiers.Alt : 0) |
        (k.HasFlag(KeyModifiers.Control) || OperatingSystem.IsMacOS() && k.HasFlag(KeyModifiers.Meta) ? EditorModifiers.Control : 0) |
        (k.HasFlag(KeyModifiers.Shift) ? EditorModifiers.Shift : 0);

    private static ShortcutKey? ShortcutKeyOf(Key key) => key switch
    {
        Key.Left => ShortcutKey.Left, Key.Right => ShortcutKey.Right, Key.Up => ShortcutKey.Up, Key.Down => ShortcutKey.Down,
        Key.PageUp => ShortcutKey.PageUp, Key.PageDown => ShortcutKey.PageDown, Key.Home => ShortcutKey.Home, Key.End => ShortcutKey.End,
        Key.OemPlus or Key.Add => ShortcutKey.Plus, Key.OemMinus or Key.Subtract => ShortcutKey.Minus,
        Key.D0 or Key.NumPad0 => ShortcutKey.Zero, Key.D1 or Key.NumPad1 => ShortcutKey.D1, Key.D2 or Key.NumPad2 => ShortcutKey.D2,
        Key.D3 or Key.NumPad3 => ShortcutKey.D3, Key.D4 or Key.NumPad4 => ShortcutKey.D4,
        _ => null
    };

    private bool repeatingKey;
    private Key lastKey;

    /// <summary>A field of the app has the keyboard (search, the bars, the page box, a note card) — not the text box editor.</summary>
    private bool IsFieldFocused => FocusManager?.GetFocusedElement() is TextBox or ComboBox { IsEditable: true } || FocusManager?.GetFocusedElement() is Control c && c.FindAncestorOfType<TextBox>() is not null;

    /// <summary>The canvas (or nothing in particular) has the keyboard: not a text box, the page list or a field.</summary>
    private bool IsCanvasKeyboardFocus => FocusManager?.GetFocusedElement() is not { } focused || ReferenceEquals(focused, Canvas) || ReferenceEquals(focused, this);

    private bool InScope(ShortcutScope scope) => scope switch
    {
        ShortcutScope.Anywhere => true,
        ShortcutScope.Writing => IsEditingText || !IsFieldFocused,
        ShortcutScope.NotTextInput => !IsEditingText && !IsFieldFocused,
        _ => !IsEditingText && IsCanvasKeyboardFocus,
    };

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool repeat = repeatingKey && lastKey == e.Key;
        repeatingKey = true;
        lastKey = e.Key;
        if (e.Handled) return;
        var mods = Modifiers(e.KeyModifiers);
        // The view shortcuts: zoom also while editing text, favorites not in other fields, page keys after the arrow moves.
        var shortcut = ShortcutKeyOf(e.Key) is { } key ? Shortcuts.Find(key, mods) : null;
        if (shortcut is { Scope: not ShortcutScope.Canvas } s && InScope(s.Scope))
        {
            if (Shortcuts.FavoriteSlot(s.Command) is { } slot) { if (settings.Favorites[slot] is not null) { FinishTextEditing(); ApplyFavorite(slot); } }
            else viewModel.Navigation.Run(s.Command, repeat);
            e.Handled = true;
            return;
        }
        // Commands of the window everywhere (also from a field): files, settings, search.
        e.Handled = mods switch
        {
            EditorModifiers.Control when e.Key == Key.S => Run(() => { FinishTextEditing(); _ = SaveAsync(saveAs: false); }),
            EditorModifiers.Control | EditorModifiers.Shift when e.Key == Key.S => Run(() => { FinishTextEditing(); _ = SaveAsync(saveAs: true); }),
            EditorModifiers.Control when e.Key == Key.O => Run(() => _ = OpenWithDialogAsync()),
            EditorModifiers.Control when e.Key == Key.N => Run(() => _ = NewNoteAsync()),
            EditorModifiers.Control when e.Key == Key.F => Run(OpenSearch),
            EditorModifiers.Control when e.Key == Key.OemComma => Run(() => ShowSettings()),
            EditorModifiers.None or EditorModifiers.Shift when e.Key == Key.F3 && IsSearchOpen => Run(() => { if (mods == EditorModifiers.Shift) viewModel.Search.Previous(); else viewModel.Search.Next(); }),
            _ => false
        };
        if (e.Handled) return;
        if (IsEditingText && HandleTextKey(e)) return;
        if (IsEditingText || IsFieldFocused || noteBox?.IsFocused == true) return; // their own keys
        // The page list keeps its own keys (Delete, Ctrl+C/V/A, moving between rows); Undo, Esc and Space still work there.
        if (pageScroller.IsKeyboardFocusWithin && (e.Key is Key.Delete or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown ||
            mods == EditorModifiers.Control && e.Key is Key.C or Key.V or Key.A or Key.X)) return;
        // Arrows move a selection (Shift: 10 times), before the page keys (spec 5.3, 17) — with the canvas having the keyboard.
        if (IsCanvasKeyboardFocus && e.Key is Key.Left or Key.Right or Key.Up or Key.Down && (mods & ~EditorModifiers.Shift) == 0 && selectTool.SelectedIds.Count > 0)
        {
            selectTool.Nudge(e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0, e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0, mods.HasFlag(EditorModifiers.Shift));
            e.Handled = true;
            return;
        }
        if (shortcut is { Scope: ShortcutScope.Canvas } page && IsCanvasKeyboardFocus && viewModel.Navigation.Run(page.Command, repeat)) { e.Handled = true; return; }
        e.Handled = mods switch
        {
            EditorModifiers.None when e.Key == Key.Escape => Escape(),
            EditorModifiers.None when e.Key == Key.Delete && selectTool.SelectedIds.Count > 0 => Run(viewModel.Selection.Delete),
            EditorModifiers.None when e.Key == Key.Enter && Canvas.IsToolActive && Canvas.Tool is IMultiClickTool multi => Run(multi.Finish), // a free polygon in progress
            EditorModifiers.None when e.Key == Key.Space => Run(() => { if (!repeat) Canvas.SetTemporaryHand(true); }),
            EditorModifiers.Control when e.Key == Key.Z => Run(Undo),
            EditorModifiers.Control when e.Key == Key.Y => Run(Redo),
            EditorModifiers.Control | EditorModifiers.Shift when e.Key == Key.Z => Run(Redo),
            EditorModifiers.Control when e.Key == Key.C => IsCanvasKeyboardFocus && Edit(SelectionCommand.Copy),
            EditorModifiers.Control when e.Key == Key.X => IsCanvasKeyboardFocus && Edit(SelectionCommand.Cut),
            EditorModifiers.Control when e.Key == Key.A => IsCanvasKeyboardFocus && Edit(SelectionCommand.SelectAll),
            EditorModifiers.Control when e.Key == Key.V && IsCanvasKeyboardFocus => Run(() => PasteSelection()),
            _ => false
        };
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == lastKey) repeatingKey = false; // another key let go does not end the held one's repeat
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
        UpdateStyleBar();
        return true;
    }

    /// <summary>Esc steps back once per press (spec 5.3, 17): the operation in progress, the selection (an automatic visit returns to its tool), then the selection tool.</summary>
    private bool Escape()
    {
        switch (tools.Escape(Canvas.IsToolActive, selectTool.SelectedIds.Count > 0 || selectTool.HasTextSelection))
        {
            case EscapeStep.CancelOperation: Canvas.CancelTool(); break;
            case EscapeStep.ClearSelection: selectTool.Select(null); selectTool.ClearTextSelection(); ReturnAfterAutoSelect(); break;
            case EscapeStep.ReturnToTool: ReturnAfterAutoSelect(); break;
            case EscapeStep.SelectTool: SetTool(ToolKind.Select); break;
            default: return false;
        }
        return true;
    }
}
