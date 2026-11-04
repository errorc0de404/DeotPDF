using System.Collections.Immutable;
using PdfNotes.Editor.Input;

namespace PdfNotes.ViewModels;

/// <summary>Keys of the window shortcuts, independent of the platform's key codes (the view maps its keys).</summary>
public enum ShortcutKey { Left, Right, Up, Down, PageUp, PageDown, Home, End, Plus, Minus, Zero, D1, D2, D3, D4 }

/// <summary>Where a shortcut works.</summary>
public enum ShortcutScope
{
    /// <summary>Everywhere, also while typing in a text box or a field.</summary>
    Anywhere,
    /// <summary>On the canvas and while editing a text box on it, not in the app's other input fields.</summary>
    Writing,
    /// <summary>Anywhere except text input (a text box being edited or a field).</summary>
    NotTextInput,
    /// <summary>Only when the canvas has the keyboard (not a text box, the page list or a field).</summary>
    Canvas,
}

public enum ShortcutCommand
{
    ZoomIn, ZoomOut, FitWidth,
    Favorite1, Favorite2, Favorite3, Favorite4,
    Back, Forward,
    ScrollUp, ScrollDown, SidewaysLeft, SidewaysRight, PreviousPage, NextPage, FirstPage, LastPage,
}

public sealed record Shortcut(ShortcutKey Key, InputModifiers Modifiers, ShortcutScope Scope, ShortcutCommand Command);

/// <summary>
/// The window's view and favorite shortcuts (spec 4.4, 17, 24.2, 24.3, 24.7; P1 V4). Selection moves with the arrows
/// come before the page keys and stay with the view; tool and text shortcuts are elsewhere.
/// </summary>
public static class Shortcuts
{
    public static ImmutableArray<Shortcut> All { get; } =
    [
        new(ShortcutKey.Plus, InputModifiers.Control, ShortcutScope.Anywhere, ShortcutCommand.ZoomIn),
        new(ShortcutKey.Minus, InputModifiers.Control, ShortcutScope.Anywhere, ShortcutCommand.ZoomOut),
        new(ShortcutKey.Zero, InputModifiers.Control, ShortcutScope.Anywhere, ShortcutCommand.FitWidth),
        new(ShortcutKey.D1, InputModifiers.Control, ShortcutScope.Writing, ShortcutCommand.Favorite1),
        new(ShortcutKey.D2, InputModifiers.Control, ShortcutScope.Writing, ShortcutCommand.Favorite2),
        new(ShortcutKey.D3, InputModifiers.Control, ShortcutScope.Writing, ShortcutCommand.Favorite3),
        new(ShortcutKey.D4, InputModifiers.Control, ShortcutScope.Writing, ShortcutCommand.Favorite4),
        new(ShortcutKey.Left, InputModifiers.Alt, ShortcutScope.NotTextInput, ShortcutCommand.Back),
        new(ShortcutKey.Right, InputModifiers.Alt, ShortcutScope.NotTextInput, ShortcutCommand.Forward),
        new(ShortcutKey.Up, InputModifiers.None, ShortcutScope.Canvas, ShortcutCommand.ScrollUp),
        new(ShortcutKey.Down, InputModifiers.None, ShortcutScope.Canvas, ShortcutCommand.ScrollDown),
        new(ShortcutKey.Left, InputModifiers.None, ShortcutScope.Canvas, ShortcutCommand.SidewaysLeft),
        new(ShortcutKey.Right, InputModifiers.None, ShortcutScope.Canvas, ShortcutCommand.SidewaysRight),
        new(ShortcutKey.PageUp, InputModifiers.None, ShortcutScope.Canvas, ShortcutCommand.PreviousPage),
        new(ShortcutKey.PageDown, InputModifiers.None, ShortcutScope.Canvas, ShortcutCommand.NextPage),
        new(ShortcutKey.Home, InputModifiers.Control, ShortcutScope.Canvas, ShortcutCommand.FirstPage),
        new(ShortcutKey.End, InputModifiers.Control, ShortcutScope.Canvas, ShortcutCommand.LastPage),
    ];

    public static Shortcut? Find(ShortcutKey key, InputModifiers modifiers) =>
        All.FirstOrDefault(s => s.Key == key && s.Modifiers == modifiers);

    /// <summary>The favorite slot (0–3) of a favorite command, or null.</summary>
    public static int? FavoriteSlot(ShortcutCommand command) =>
        command is >= ShortcutCommand.Favorite1 and <= ShortcutCommand.Favorite4 ? command - ShortcutCommand.Favorite1 : null;
}
