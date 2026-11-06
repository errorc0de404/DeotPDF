using PdfNotes.Core.Models;

namespace PdfNotes.ViewModels;

/// <summary>
/// What a command needs from the screen it runs in (roadmap P4.5 R4b–R4c): its file picker and own windows, its popup
/// menus, and the steps that still live in the view (undo with the text box editor and the canvas, hiding the notes,
/// the search bar). Each layout (the desktop window now, a mobile one later) implements it its own way. The later R4
/// steps move the view-side steps into view models, so this interface shrinks.
/// </summary>
public interface IAppShell
{
    /// <summary>A PDF to open, or null when cancelled.</summary>
    Task<string?> ChooseOpenPathAsync();

    /// <summary>The new note window (spec 4.1): the page size and background, or null when cancelled.</summary>
    NewPageSpec? ChooseNewNote();

    /// <summary>The settings window (spec 18.1).</summary>
    void ShowSettings();

    /// <summary>The recent documents list (spec 24.1).</summary>
    void ShowRecentMenu();

    /// <summary>The zoom presets and fits (spec 4.4).</summary>
    void ShowZoomMenu();

    /// <summary>Undo (or redo) one step: the edited text box's own steps while one is edited (spec 13.2–13.3).</summary>
    void UndoOrRedo(bool redo);

    /// <summary>Hides or shows the notes (spec 24.10).</summary>
    void SetNotesHidden(bool hidden);

    /// <summary>Opens the search bar (spec 12).</summary>
    void OpenSearch();

    /// <summary>A web page in the system's browser (the update notice).</summary>
    void OpenWebPage(string url);

    /// <summary>필기 › 이미지: an image file chosen and placed on the page in view (spec 10).</summary>
    Task InsertImageFromFileAsync();
}

/// <summary>A shell that does nothing and cancels every choice (view models made without a screen, tests).</summary>
public sealed class NoShell : IAppShell
{
    public static readonly NoShell Instance = new();
    public Task<string?> ChooseOpenPathAsync() => Task.FromResult<string?>(null);
    public NewPageSpec? ChooseNewNote() => null;
    public void ShowSettings() { }
    public void ShowRecentMenu() { }
    public void ShowZoomMenu() { }
    public void UndoOrRedo(bool redo) { }
    public void SetNotesHidden(bool hidden) { }
    public void OpenSearch() { }
    public void OpenWebPage(string url) { }
    public Task InsertImageFromFileAsync() => Task.CompletedTask;
}
