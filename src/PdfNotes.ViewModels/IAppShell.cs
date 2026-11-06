using PdfNotes.Core.Models;
using PdfNotes.Editor.Services;

namespace PdfNotes.ViewModels;

/// <summary>
/// What a command needs from the screen it runs in (roadmap P4.5 R4b–R4f): its file pickers, its own windows and its popup
/// menus. Each layout (the desktop window now, a mobile one later) implements it its own way.
/// </summary>
public interface IAppShell
{
    /// <summary>A PDF to open (a file, or a document of the system's picker), or null when cancelled.</summary>
    Task<DocumentLocation?> ChooseDocumentAsync();

    /// <summary>The new note window (spec 4.1): the page size and background, or null when cancelled.</summary>
    Task<NewPageSpec?> ChooseNewNoteAsync();

    /// <summary>The settings window (spec 18.1).</summary>
    Task ShowSettingsAsync();

    /// <summary>The recent documents list (spec 24.1).</summary>
    void ShowRecentMenu();

    /// <summary>The zoom presets and fits (spec 4.4).</summary>
    void ShowZoomMenu();

    /// <summary>A web page in the system's browser (the update notice).</summary>
    void OpenWebPage(string url);

    /// <summary>필기 › 이미지: an image file chosen and placed on the page in view (spec 10).</summary>
    Task InsertImageFromFileAsync();
}

/// <summary>A shell that does nothing and cancels every choice (view models made without a screen, tests).</summary>
public sealed class NoShell : IAppShell
{
    public static readonly NoShell Instance = new();
    public Task<DocumentLocation?> ChooseDocumentAsync() => Task.FromResult<DocumentLocation?>(null);
    public Task<NewPageSpec?> ChooseNewNoteAsync() => Task.FromResult<NewPageSpec?>(null);
    public Task ShowSettingsAsync() => Task.CompletedTask;
    public void ShowRecentMenu() { }
    public void ShowZoomMenu() { }
    public void OpenWebPage(string url) { }
    public Task InsertImageFromFileAsync() => Task.CompletedTask;
}
