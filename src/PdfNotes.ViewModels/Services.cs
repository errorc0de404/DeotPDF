using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;

namespace PdfNotes.ViewModels;

/// <summary>The canvas's input as the view models end it (R4f): before a command changes the document under it.</summary>
public interface IEditingSurface
{
    /// <summary>Commits a text box being edited into the document.</summary>
    void FinishTextEditing();

    /// <summary>Cancels a canvas operation in progress (a stroke, a drag).</summary>
    void CancelTool();
}

/// <summary>What a window's view models take from the app (R4g): its settings, name and folder, the clock, the language.</summary>
/// <param name="SaveSettings">Writes the app settings (tests pass one that writes nothing).</param>
/// <param name="SettingsFolder">The app's settings folder: backups, the backup store's records, the running windows' markers.</param>
public sealed record AppServices(AppSettings Settings, string AppName, Action<AppSettings> SaveSettings, string SettingsFolder)
{
    /// <summary>Clock and timers (tests pass a fake one).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Asks for the latest release (tests pass a fake one; default the release page).</summary>
    public Func<Task<UpdateResult>>? CheckUpdates { get; init; }

    /// <summary>The UI texts of the language in use.</summary>
    public Texts Texts { get; init; } = Texts.Korean;
}

/// <summary>
/// What a window's view models take from its screen (R4f): its message boxes, the document canvas and its input, and the
/// optional rest — thumbnails, the window's own state, the UI thread, its windows and menus, the clipboard, page windows.
/// Each layout (the desktop window now, a mobile one later) passes its own.
/// </summary>
public sealed record ScreenServices(IEditorDialogs Dialogs, IDocumentViewport Viewport, IEditingSurface Editing)
{
    public IPageThumbnailSource Thumbnails { get; init; } = NoThumbnails.Instance;
    public IWindowCoordinator Window { get; init; } = NoWindow.Instance;
    public IUiThread Ui { get; init; } = ImmediateUi.Instance;
    public IAppShell Shell { get; init; } = NoShell.Instance;
    public IEditorClipboard Clipboard { get; init; } = NoClipboard.Instance;
    public IPageDialogs PageDialogs { get; init; } = NoPageDialogs.Instance;

    private sealed class NoThumbnails : IPageThumbnailSource
    {
        public static readonly NoThumbnails Instance = new();
        public Task<PageThumbnail?> RenderAsync(OpenDocument document, PdfNotes.Core.Models.DocumentModel model, PdfNotes.Core.Models.Page page, double width, bool notesHidden, CancellationToken cancel) =>
            Task.FromResult<PageThumbnail?>(null);
    }

    private sealed class NoWindow : IWindowCoordinator
    {
        public static readonly NoWindow Instance = new();
        public bool InputPending => false;
        public bool ShowOpenElsewhere(string path) => false;
    }

    /// <summary>A clipboard that holds nothing.</summary>
    private sealed class NoClipboard : IEditorClipboard
    {
        public static readonly NoClipboard Instance = new();
        public bool ContainsData(string format) => false;
        public string? GetData(string format) => null;
        public void SetData(string format, string data) { }
        public bool ContainsText() => false;
        public string GetText() => "";
        public void SetText(string text) { }
        public bool ContainsImage() => false;
        public byte[]? GetImagePng() => null;
    }

    /// <summary>Page windows that always cancel.</summary>
    private sealed class NoPageDialogs : IPageDialogs
    {
        public static readonly NoPageDialogs Instance = new();
        public string[]? ChoosePdfFiles(string title) => null;
        public Task<int[]?> ChooseImportPagesAsync(MergeFile file) => Task.FromResult<int[]?>(null);
        public Task<MergeChoice?> ChooseMergeAsync(IReadOnlyList<MergeFile> files, bool insert, Func<Task<IReadOnlyList<MergeFile>>> addFiles) => Task.FromResult<MergeChoice?>(null);
        public PageExportChoice? ChooseExport(int pageCount, string range, bool flatten) => null;
        public PdfNotes.Core.Models.NewPageSpec? ChooseBlankPage(bool insert, PdfNotes.Core.Models.PageSetup reference) => null;
        public PageTransformRequest? ChooseTransform(bool resize, PdfNotes.Core.Models.DocumentModel model, PdfNotes.Core.Models.Page active, Func<PdfNotes.Core.Commands.PageApplyScope, PdfNotes.Core.Models.PageSetup[]> targets) => null;
    }
}

/// <summary>
/// Puts a window's view models together (roadmap P4.5 R4g): the app calls this with what it and the screen provide.
/// <see cref="MainViewModel"/> makes none of its parts itself, so a layout can replace one.
/// </summary>
public static class MainViewModels
{
    public static MainViewModel Create(AppServices app, ScreenServices screen)
    {
        var settings = app.Settings;
        var main = new MainViewModel(app, screen);
        main.Ribbon = new RibbonViewModel(settings);
        main.Recent = new RecentViewModel(settings, main.SaveSettings);
        main.Update = new UpdateViewModel(settings, main.SaveSettings, screen.Dialogs, app.Time, app.CheckUpdates ?? (() => UpdateCheck.CheckAsync()), app.Texts);
        main.Loading = new LoadingViewModel(main.After, app.Texts);
        main.Outline = new OutlineViewModel(main);
        main.Foreign = new ForeignViewModel(main);
        main.Search = new SearchViewModel(main);
        main.Navigation = new NavigationViewModel(main);
        main.Display = new DisplayViewModel(settings, main.SaveSettings);
        main.Favorites = new FavoritesViewModel(settings, main.SaveSettings, app.Texts, slot => main.ToolBar.ApplyFavorite(slot), slot => main.ToolBar.StoreFavorite(slot));
        main.Pages = new PagesViewModel(main, screen.Dialogs, screen.Clipboard);
        main.Selection = new SelectionViewModel(main, screen.Dialogs, screen.Clipboard);
        main.TextStyle = new TextStyleViewModel(main, screen.Dialogs);
        main.NoteCard = new NoteCardViewModel(main);
        main.ImportExport = new ImportExportViewModel(main, screen.Dialogs, screen.PageDialogs);
        main.ToolStyles = new ToolStylesViewModel(main);
        main.ToolBar = new ToolBarViewModel(main);
        main.Backup = new BackupViewModel(main, screen.Dialogs, app.Time, app.SettingsFolder);
        main.Assembled();
        return main;
    }
}
