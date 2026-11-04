using PdfNotes.Core.Models;

namespace PdfNotes.TextEditor;

/// <summary>Editing keys as the model takes them (each platform maps its own key events onto these).</summary>
public enum EditorKey { Left, Right, Up, Down, Home, End, Backspace, Delete, Enter, Tab }

[Flags]
public enum EditorModifiers { None = 0, Shift = 1, Control = 2 }

/// <summary>What a change of the box was.</summary>
public enum TextEditKind { Typing, Deleting, Pasting, Cutting, Formatting, BoxChange, Undo, Redo }

/// <summary>
/// The edited box changed. <see cref="NewGroup"/>: the change began a new Undo step of the editor (spec 13.2 — the
/// document history splits at the same places). <see cref="Depth"/> is the editor's Undo depth after the change.
/// </summary>
public sealed class TextEditedEventArgs(TextBoxAnnotation box, TextEditKind kind, bool newGroup, int depth) : EventArgs
{
    public TextBoxAnnotation Box { get; } = box;
    public TextEditKind Kind { get; } = kind;
    public bool NewGroup { get; } = newGroup;
    public int Depth { get; } = depth;
}

/// <summary>
/// The clipboard as the editor uses it. The platform reads and writes the real clipboard (the WPF and Avalonia hosts;
/// roadmap P2 S4 keeps clipboard access out of the model). <see cref="Set"/> puts both forms on the clipboard at once.
/// </summary>
public interface ITextClipboard
{
    /// <summary>Puts the plain text (for other programs) and the formatted copy (<see cref="TextClipboardFormat.Name"/>) on the clipboard.</summary>
    void Set(string plain, string formatted);

    /// <summary>The formatted copy when the clipboard holds one from this app, else null.</summary>
    string? GetFormatted();

    /// <summary>The clipboard's plain text, or null.</summary>
    string? GetText();
}
