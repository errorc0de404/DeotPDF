namespace PdfNotes.Editor.Platform;

/// <summary>The buttons of a question.</summary>
public enum AskButtons { YesNo, YesNoCancel }

/// <summary>The answer to a question (closing the box is <see cref="Cancel"/>, or <see cref="No"/> for a yes/no question).</summary>
public enum Answer { Yes, No, Cancel }

/// <summary>How a message is shown.</summary>
public enum NoticeKind { Information, Warning, Question, OpenFailure }

/// <summary>A PDF file the user picks to write; <paramref name="OverwritePrompt"/> asks again when it exists.</summary>
public sealed record SavePathRequest(string Title, string SuggestedName, string? Folder, bool OverwritePrompt = true);

/// <summary>
/// The modal UI of a platform shell (D14d): questions, messages and the save dialog. Only what really differs between
/// platforms is here; the shell's own windows (settings, page dialogs) stay in the shell.
/// </summary>
public interface IEditorDialogs
{
    Answer Ask(string text, AskButtons buttons, NoticeKind kind);

    void Tell(string text, NoticeKind kind);

    /// <summary>A PDF path to write, or null when cancelled.</summary>
    string? ChooseSavePath(SavePathRequest request);
}

/// <summary>The system clipboard (D14d). Methods may throw <see cref="ClipboardUnavailableException"/> when the clipboard is busy.</summary>
public interface IEditorClipboard
{
    bool ContainsData(string format);
    string? GetData(string format);
    void SetData(string format, string data);
    bool ContainsText();
    string GetText();
    void SetText(string text);
    bool ContainsImage();

    /// <summary>The clipboard image as PNG bytes, or null.</summary>
    byte[]? GetImagePng();
}

/// <summary>The clipboard could not be used (another program holds it).</summary>
public sealed class ClipboardUnavailableException(Exception inner) : Exception("The clipboard is unavailable.", inner);
