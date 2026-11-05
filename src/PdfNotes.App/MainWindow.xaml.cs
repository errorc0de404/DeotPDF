using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using PdfNotes.App.Canvas;
using PdfNotes.App.Services;
using PdfNotes.App.Views;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Reading;
using PdfNotes.App.Themes;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Session;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

/// <summary>Commands without a built-in WPF equivalent.</summary>
public static class AppCommands
{
    public static readonly RoutedUICommand ExportFlattened = new(Strings.Get("file.exportFlattenedTitle"), nameof(ExportFlattened), typeof(AppCommands));
    public static readonly RoutedUICommand Settings = new(Strings.Get("command.settings"), nameof(Settings), typeof(AppCommands));
}

public partial class MainWindow : Window, IMainView, IEditorDialogs
{
    private readonly MainViewModel viewModel; // P1: title, status line, undo tips; owns the workflow and the tool switch
    private AppSettings settings => viewModel.Settings;
    private DocumentWorkflow workflow => viewModel.Workflow; // D14d: new/open/save/export flow of the editor
    private ToolSwitch tools => viewModel.Tools;             // D14d: current tool and the automatic selection visit
    private PageRenderScheduler? scheduler; // page bitmaps of the open document
    private bool closeConfirmed;

    private OpenDocument? document => workflow.Document;
    private bool busy => workflow.IsBusy;
    private bool confirming => workflow.IsConfirming;

    static MainWindow() => PdfNotes.Text.WpfText.Register(); // D14a: the WPF text engine behind TextLayout

    public MainWindow()
    {
        Theme.Attach(this);
        viewModel = new MainViewModel(this, this, AppSettings.Load(), App.DisplayName, clipboard: clipboard, pageDialogs: this); // before the window: command bindings ask it while the XAML loads
        InitializeComponent();
        DataContext = viewModel;
        RestoreWindowPlacement();
        Theme.Use(settings.Theme, settings.Accent);
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(SystemSettingHook);
        ShowDarkPages(settings.DarkPages);
        View.SetMode(settings.ViewMode);
        View.ViewChanged += (_, _) => { viewModel.Navigation.ViewChanged(); UpdateViewStatus(); };
        viewModel.Navigation.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NavigationViewModel.Mode)) UpdateViewStatus(); };
        viewModel.Favorites.PropertyChanged += (_, _) => BuildFavorites();
        InitializeDisplay();
        View.ViewChanged += (_, _) => PositionNoteCard();
        InitializeTools();
        InitializeLinks();
        InitializePagePanel();
        InitializeOutline();
        ShowFavorites(viewModel.Display.FavoritesShown);
        InitializeRibbon();
        InitializeUpdates();
        InitializeLoading();
        InitializeForeign();
        InitializeSearch();
        InitializeBackup();
        ShowDocument(OpenDocument.CreateNote(NewPageSpec.Default));
    }

    /// <summary>The open document (never null after construction).</summary>
    public OpenDocument Document => document!;

    /// <summary>True while a file is being opened or saved; document-switching commands are disabled meanwhile.</summary>
    public bool IsBusy => busy;

    // ----- validation harness hooks: replace modal UI when set -----

    /// <summary>Receives open failures instead of a message box.</summary>
    public Action<string>? OpenErrorSink { get; set; }

    /// <summary>Receives save failures and open notices instead of a message box.</summary>
    public Action<string>? MessageSink { get; set; }

    /// <summary>Answers questions (unsaved changes, external change) instead of a message box.</summary>
    public Func<string, MessageBoxButton, MessageBoxResult>? PromptOverride { get; set; }

    /// <summary>Returns the save path (or null = cancelled) instead of the save dialog. The argument is the suggested name.</summary>
    public Func<string, string?>? SaveDialogOverride { get; set; }

    /// <summary>A WPF position as an editor point (viewport DIPs).</summary>
    private static PdfNotes.Core.Geometry.Point2 At(Point p) => new(p.X, p.Y);

    // ----- document lifetime -----

    /// <summary>Replaces the current document. The old one is released only after the new one is shown.</summary>
    private void ShowDocument(OpenDocument next) => workflow.Replace(next);

    /// <summary>The view model behind this window (P1).</summary>
    public MainViewModel ViewModel => viewModel;

    void IMainView.DocumentLeaving(OpenDocument? previous)
    {
        RememberPosition(previous);
        if (NotesHidden) SetNotesHidden(false); // a new or opened document shows its notes (spec 24.10)
    }

    void IMainView.DocumentReplaced(OpenDocument? previous, OpenDocument next)
    {
        linkDocument = next; sourceLinks.Clear(); // links belong to one document (the jumps are cleared by the view model)
        var previousScheduler = scheduler;
        scheduler = new PageRenderScheduler(next.Renderer, Dispatcher);
        View.SetDocument(next.Session.Current.Pages, scheduler);
        selectTool.Select(null);
        pages.DocumentReplaced();
        previousScheduler?.Dispose();
        previous?.Dispose();
        ResetBackup();
        ShowingFile();
        UpdateViewStatus();
        UpdateStyleBar();
    }

    void IMainView.DocumentChanged()
    {
        View.UpdatePages(document!.Session.Current.Pages);
        selectTool.Revalidate();
        // A page structure change makes glyph carets of the text selection refer to another page.
        if (!pageThumbs.Select(t => t.PageId).SequenceEqual(document.Session.Current.Pages.Select(p => p.Id)) ||
            pageThumbs.Any(t => document.Session.Current.GetPage(t.PageId).Setup != t.Rendered?.Setup)) selectTool.ClearTextSelection();
        RefreshPagePanel();
        RefreshOutline();
        UpdateStyleBar();
    }

    void IMainView.CommandsChanged() => CommandManager.InvalidateRequerySuggested();

    void IMainView.OnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// Unsaved changes before closing, a new note or another file (spec 4.2, 18): Save / Don't save / Cancel.
    /// Returns false when the user cancels, the save dialog is cancelled or the save fails.
    /// </summary>
    public Task<bool> ConfirmDiscardChangesAsync() => workflow.ConfirmDiscardChangesAsync();

    /// <summary>Opens a PDF. On failure the current document stays open and unchanged.</summary>
    public Task<bool> OpenFileAsync(string path) => workflow.OpenFileAsync(path);

    public Task<bool> CreateNoteAsync(NewPageSpec spec) => workflow.CreateNoteAsync(spec);

    /// <summary>Ctrl+S / Save As (spec 14.1, 14.4, 18). Returns true only when the file was written.</summary>
    public Task<bool> SaveAsync(bool saveAs) => workflow.SaveAsync(saveAs);

    /// <summary>Saves to <paramref name="path"/> without the dialog (also used by the validation harness).</summary>
    public Task<bool> SaveToAsync(string path) => workflow.SaveToAsync(path);

    /// <summary>Flatten PDF export (spec 14.5). Returns true when the file was written.</summary>
    public Task<bool> ExportFlattenedAsync() => workflow.ExportFlattenedAsync();

    // ----- the view model's view and the workflow's dialogs (D14d, P1) -----

    void IMainView.CancelTool() => View.CancelTool();

    void IMainView.Opened(string path) => RestorePosition(path);

    void IMainView.Saved(string path)
    {
        BackupSaved();
        ShowingFile();
    }

    bool IMainView.ShowOpenElsewhere(string path)
    {
        if (BackupStore.WindowShowing(AppSettings.Folder, path, backupStore) is not { } other) return false;
        Services.WindowActivation.Bring(other);
        return true;
    }

    /// <summary>Tells the other windows which file this one shows (spec 4.6, D14g).</summary>
    private void ShowingFile()
    {
        if (backupStore is null) return; // the first note is shown before the backup store exists
        backupStore.SetOpenDocument(document?.FilePath, new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    Answer IEditorDialogs.Ask(string text, AskButtons buttons, NoticeKind kind) =>
        Prompt(text, buttons == AskButtons.YesNo ? MessageBoxButton.YesNo : MessageBoxButton.YesNoCancel, Image(kind)) switch
        {
            MessageBoxResult.Yes => Answer.Yes,
            MessageBoxResult.No => Answer.No,
            _ => Answer.Cancel
        };

    void IEditorDialogs.Tell(string text, NoticeKind kind)
    {
        if (kind != NoticeKind.OpenFailure) ShowMessage(text, Image(kind));
        else if (OpenErrorSink is { } sink) sink(text);
        else MessageBox.Show(this, text, Strings.Get("file.openTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    string? IEditorDialogs.ChooseSavePath(SavePathRequest request)
    {
        if (SaveDialogOverride is { } overrideDialog) return overrideDialog(request.SuggestedName);
        var dialog = new SaveFileDialog
        {
            Filter = Strings.Get("file.pdfFilter"), DefaultExt = ".pdf", AddExtension = true, OverwritePrompt = request.OverwritePrompt,
            FileName = request.SuggestedName, Title = request.Title
        };
        if (request.Folder is not null) dialog.InitialDirectory = request.Folder;
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private static MessageBoxImage Image(NoticeKind kind) => kind switch
    {
        NoticeKind.Information => MessageBoxImage.Information,
        NoticeKind.Question => MessageBoxImage.Question,
        _ => MessageBoxImage.Warning
    };

    private async void ExportFlattenedExecuted(object sender, ExecutedRoutedEventArgs e) => await ExportPagesAsync(flatten: true); // the export window, preset (spec 11.10)

    private MessageBoxResult Prompt(string text, MessageBoxButton buttons, MessageBoxImage image) =>
        PromptOverride is { } answer ? answer(text, buttons) : MessageBox.Show(this, text, App.DisplayName, buttons, image);

    private void ShowMessage(string text, MessageBoxImage image)
    {
        if (MessageSink is { } sink) sink(text);
        else MessageBox.Show(this, text, App.DisplayName, MessageBoxButton.OK, image);
    }

    /// <summary>The status bar's page and zoom (<see cref="NavigationViewModel"/>); a box being typed in keeps its text.</summary>
    private void UpdateViewStatus()
    {
        var navigation = viewModel.Navigation;
        if (!PageBox.IsKeyboardFocusWithin) PageBox.Text = navigation.PageNumberText;
        PageCountText.Text = navigation.PageCountText;
        if (!ZoomBox.IsKeyboardFocusWithin) ZoomBox.Text = navigation.ZoomText;
        ZoomText.Text = navigation.ZoomText;
        ContinuousButton.IsChecked = navigation.Mode == ViewMode.Continuous;
        SinglePageButton.IsChecked = navigation.Mode == ViewMode.SinglePage;
    }

    // ----- commands -----

    private void IdleCanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !busy;

    private async void NewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new NewNoteDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } spec) await CreateNoteAsync(spec);
    }

    private async void OpenExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = Strings.Get("file.pdfFilter"), Title = Strings.Get("file.openTitle") };
        if (dialog.ShowDialog(this) == true) await OpenFileAsync(dialog.FileName);
    }

    private async void SaveExecuted(object sender, ExecutedRoutedEventArgs e) => await SaveAsync(saveAs: false);
    private async void SaveAsExecuted(object sender, ExecutedRoutedEventArgs e) => await SaveAsync(saveAs: true);

    private void UndoCanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = document?.Session.CanUndo == true;
    private void RedoCanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = document?.Session.CanRedo == true;

    private void UndoExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        View.CancelTool();
        UndoTextAware(false);
    }

    private void RedoExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        View.CancelTool();
        UndoTextAware(true);
    }

    // ----- settings window (spec 18.1) -----

    /// <summary>Windows changed its app theme: follow it when the theme setting is `Windows 설정 따르기` (spec 3.2, D13d).</summary>
    private nint SystemSettingHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int SettingChange = 0x001A; // WM_SETTINGCHANGE
        if (message == SettingChange && lParam != 0 && System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            Dispatcher.BeginInvoke(Theme.SystemThemeChanged);
        return 0;
    }

    /// <summary>Page numbers drawn on the pages (spec 4.5; the settings window may be previewing a change).</summary>
    public bool PageNumbersShown => viewModel.Display.PageNumbers;

    /// <summary>The stored 화면 settings (spec 18.1).</summary>
    public DisplayValues DisplaySettings => viewModel.Display.Stored;

    /// <summary>The display values (<see cref="DisplayViewModel"/>) shown on the window: dark pages, favorites, page numbers.</summary>
    private void InitializeDisplay() => viewModel.Display.PropertyChanged += (_, e) =>
    {
        var display = viewModel.Display;
        switch (e.PropertyName)
        {
            case nameof(DisplayViewModel.DarkPages): ShowDarkPages(display.DarkPages); break;
            case nameof(DisplayViewModel.FavoritesShown): ShowFavorites(display.FavoritesShown); break;
            case nameof(DisplayViewModel.PageNumbers) or nameof(DisplayViewModel.NumberSize): View.RefreshOverlays(); break;
        }
    };

    /// <summary>Shows theme, accent color and page numbers now without storing them (settings window preview).</summary>
    public void PreviewDisplay(DisplayValues values)
    {
        Theme.Use(values.Theme, values.Accent);
        viewModel.Display.Preview(values);
    }

    /// <summary>Applies and stores the 화면 settings (an app setting: no Undo, the document stays unchanged).</summary>
    public void SetDisplay(DisplayValues values)
    {
        Theme.Use(values.Theme, values.Accent);
        viewModel.Display.Apply(values);
    }

    public bool QuickEraseEnabled => settings.QuickErase;

    /// <summary>필기 › 페이지 사이 끌어 옮기기 (spec 5.3, 18.1, D14g).</summary>
    public bool PageDragEnabled
    {
        get => settings.PageDrag;
        set { settings.PageDrag = value; settings.Save(); }
    }

    private void SettingsExecuted(object sender, ExecutedRoutedEventArgs e) => ShowSettings();

    /// <summary>
    /// Opens the settings window. OK stores the values in the app settings (kept after a restart); nothing here is
    /// document state, so the unsaved mark and Undo stay as they are. Returns true when OK was chosen.
    /// </summary>
    public bool ShowSettings()
    {
        var display = DisplaySettings;
        var dialog = new SettingsDialog(new(settings.QuickErase, settings.PageDrag,
            new(settings.AutoBackup, settings.BackupIntervalMinutes, settings.BackupMode, settings.EffectiveBackupFolder, settings.BackupKeep), display,
            settings.Language, settings.CheckUpdates == true, settings.FingerDrawing, settings.PenPressure),
            () => Prompt(Strings.Get("settings.backupInPlaceConfirm"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes,
            PreviewDisplay, Strings.Discover(AppSettings.LanguageFolders), new(backupStore.Usage, ClearBackups), new(viewModel.Update.CheckAsync, OpenWebPage)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } values) { PreviewDisplay(display); return false; } // cancel undoes the preview
        SetDisplay(values.Display ?? display);
        settings.QuickErase = values.QuickErase;
        settings.PageDrag = values.PageDrag;
        settings.FingerDrawing = values.FingerDrawing;
        settings.PenPressure = values.PenPressure;
        settings.AutoBackup = values.Backup.Enabled;
        settings.BackupIntervalMinutes = values.Backup.IntervalMinutes;
        settings.BackupMode = values.Backup.Mode;
        settings.BackupFolder = string.Equals(values.Backup.Folder, AppSettings.DefaultBackupFolder, StringComparison.OrdinalIgnoreCase) ? null : values.Backup.Folder;
        settings.BackupKeep = values.Backup.Keep;
        bool languageChanged = !string.Equals(settings.Language, values.Language, StringComparison.OrdinalIgnoreCase);
        settings.Language = values.Language;
        if (values.CheckUpdates != (settings.CheckUpdates == true)) settings.CheckUpdates = values.CheckUpdates; // untouched: still asked at the first start
        settings.Save();
        if (languageChanged) ShowMessage(Strings.Get("settings.languageNextStart"), MessageBoxImage.Information); // packs load at start (D14e)
        return true;
    }

    // ----- drag & drop (spec 4.2) -----

    /// <summary>The dropped files when every one is a PDF (D13j: several PDFs are inserted, not opened).</summary>
    private static string[]? DroppedPdfs(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files &&
        files.All(f => string.Equals(Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase)) ? files : null;

    /// <summary>PDFs over the page list go in at the drop line (D13j); elsewhere one PDF opens and several are inserted.</summary>
    private bool OverPageList(DragEventArgs e) => document is not null && PagePanel.IsVisible &&
        e.GetPosition(PageList) is var at && at.X >= 0 && at.Y >= 0 && at.X <= PageList.ActualWidth && at.Y <= PageList.ActualHeight;

    private void WindowDragOver(object sender, DragEventArgs e)
    {
        bool images = DroppedImages(e) is not null;
        bool pdfs = !busy && !viewModel.ImportExport.Importing && DroppedPdfs(e) is not null;
        e.Effects = pdfs || images ? DragDropEffects.Copy : DragDropEffects.None;
        ShowDropMark(images ? e.GetPosition(View) : null);
        if (pdfs && OverPageList(e)) ShowDropLine(DropIndex(e.GetPosition(PageList)));
        else if (!pageDragging) PageDropLine.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void WindowDragLeave(object sender, DragEventArgs e)
    {
        ShowDropMark(null);
        if (!pageDragging) PageDropLine.Visibility = Visibility.Collapsed;
    }

    /// <summary>One PDF opens it (spec 4.2); image files go onto the page where they are dropped (24.11); anything else is refused briefly.</summary>
    private async void WindowDrop(object sender, DragEventArgs e)
    {
        ShowDropMark(null);
        if (DroppedImages(e) is { } images)
        {
            e.Handled = true;
            Activate();
            try { InsertImagesAt(await Task.WhenAll(images.Select(f => new FileInfo(f).Length > ModelLimits.MaxResourceBytes ? throw new InvalidDataException() : File.ReadAllBytesAsync(f))), e.GetPosition(View)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { ShowMessage(Strings.Get("image.readFailed"), MessageBoxImage.Warning); }
            return;
        }
        if (DroppedPdfs(e) is not { } paths)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) viewModel.Status = Strings.Get("drop.unsupported");
            return;
        }
        e.Handled = true;
        bool onList = OverPageList(e);
        int at = onList ? DropIndex(e.GetPosition(PageList)) : -1;
        PageDropLine.Visibility = Visibility.Collapsed;
        Activate();
        if (onList) await ImportPdfPagesAsync(paths, at);
        else if (paths.Length > 1 && document is not null) await ImportPdfPagesAsync(paths);
        else if (paths.Length == 1) await OpenFileAsync(paths[0]);
        else await MergePdfsAsync(paths);
    }

    // ----- keyboard: Space = temporary hand tool outside text input (spec 4.4, 17); Esc, Delete -----

    private static bool IsTextInputFocused => Keyboard.FocusedElement is TextBoxBase or PasswordBox;

    private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (FilterHeldKey(e)) return;
        if (textEditor is not null && Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Z or Key.Y)
        { UndoTextAware(e.Key == Key.Y); e.Handled = true; return; }
        // The view shortcuts (spec 4.4, 17, 24.2, 24.3, 24.7): zoom also while editing text, favorites not in other input
        // boxes, back/forward outside text input. Page keys come after the selection's arrow moves.
        var shortcut = ShortcutOf(e);
        if (shortcut is { Scope: not ShortcutScope.Canvas } s && InScope(s.Scope))
        {
            if (Shortcuts.FavoriteSlot(s.Command) is { } slot) { if (settings.Favorites[slot] is not null) ApplyFavorite(slot); }
            else viewModel.Navigation.Run(s.Command, e.IsRepeat);
            e.Handled = true;
            return;
        }
        if (IsCanvasKeyboardFocus && (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) &&
            (Keyboard.Modifiers & ~ModifierKeys.Shift) == 0 && selectTool.SelectedIds.Count > 0)
        {
            selectTool.Nudge(e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0,
                e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
            return;
        }
        if (shortcut is { Scope: ShortcutScope.Canvas } page && IsCanvasKeyboardFocus && viewModel.Navigation.Run(page.Command, e.IsRepeat)) { e.Handled = true; return; }
        if (textEditor is null && tools.Current == ToolKind.Select && tools.Return == ToolKind.Text && View.IsKeyboardFocusWithin && IsTypingKey(e) &&
            selectTool.SelectedAnnotations is [TextBoxAnnotation { Locked: false } typed])
        {
            BeginTextEditWithKey(typed.Id, e); // the caret goes to the end
            return;
        }
        if (e.Key == Key.Space && !IsTextInputFocused)
        {
            if (!e.IsRepeat) View.SetTemporaryHand(true);
            e.Handled = true;
        }
    }

    /// <summary>The canvas (or nothing in particular) has the keyboard: not a text box, the page list or a field.</summary>
    private bool IsCanvasKeyboardFocus => View.IsKeyboardFocusWithin || Keyboard.FocusedElement is null || ReferenceEquals(Keyboard.FocusedElement, this);

    /// <summary>Zoom steps of Ctrl+= / Ctrl+- (spec 24.2): the zoom box presets, down to 10%.</summary>
    public static readonly int[] ZoomStepPercents = ZoomSteps.StepPercents;

    /// <summary>The next (<paramref name="direction"/> 1) or previous (-1) zoom step around the view center (spec 24.2).</summary>
    public void ZoomByStep(int direction) => viewModel.Navigation.ZoomByStep(direction);

    /// <summary>
    /// The key of the shortcut table (<see cref="Shortcuts"/>). ↑/↓ scroll here, not in the ScrollViewer: it first predicts
    /// focus, and with no focusable element above the canvas (select, text, lasso, note tools) it predicts the canvas
    /// itself and swallows ↑ without scrolling (2026-10-26).
    /// </summary>
    private static Shortcut? ShortcutOf(KeyEventArgs e)
    {
        ShortcutKey? key = (e.Key == Key.System ? e.SystemKey : e.Key) switch
        {
            Key.Left => ShortcutKey.Left, Key.Right => ShortcutKey.Right, Key.Up => ShortcutKey.Up, Key.Down => ShortcutKey.Down,
            Key.PageUp => ShortcutKey.PageUp, Key.PageDown => ShortcutKey.PageDown, Key.Home => ShortcutKey.Home, Key.End => ShortcutKey.End,
            Key.OemPlus or Key.Add => ShortcutKey.Plus, Key.OemMinus or Key.Subtract => ShortcutKey.Minus, Key.D0 or Key.NumPad0 => ShortcutKey.Zero,
            Key.D1 or Key.NumPad1 => ShortcutKey.D1, Key.D2 or Key.NumPad2 => ShortcutKey.D2, Key.D3 or Key.NumPad3 => ShortcutKey.D3, Key.D4 or Key.NumPad4 => ShortcutKey.D4,
            _ => null,
        };
        return key is { } k ? Shortcuts.Find(k, Modifiers(Keyboard.Modifiers)) : null;
    }

    private static InputModifiers Modifiers(ModifierKeys keys) =>
        (keys.HasFlag(ModifierKeys.Alt) ? InputModifiers.Alt : 0) | (keys.HasFlag(ModifierKeys.Control) ? InputModifiers.Control : 0) |
        (keys.HasFlag(ModifierKeys.Shift) ? InputModifiers.Shift : 0) | (keys.HasFlag(ModifierKeys.Windows) ? InputModifiers.Windows : 0);

    private bool InScope(ShortcutScope scope) => scope switch
    {
        ShortcutScope.Anywhere => true,
        ShortcutScope.Writing => textEditor is not null || !IsTextInputFocused,
        ShortcutScope.NotTextInput => !IsTextInputFocused,
        _ => IsCanvasKeyboardFocus,
    };

    private void WindowPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) View.SetTemporaryHand(false);
    }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F3 && IsSearchOpen && !e.Handled)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) SearchPrevious(); else SearchNext();
            e.Handled = true;
            return;
        }
        if (IsTextInputFocused || e.Handled) return;
        if (e.Key == Key.Escape)
        {
            // Esc steps back once per press (spec 5.3, 17; 2026-10-14): the operation in progress, then the selection (an
            // automatic selection visit returns to its tool), then the selection tool itself.
            switch (tools.Escape(View.IsToolActive, selectTool.SelectedIds.Count > 0 || selectTool.HasTextSelection))
            {
                case EscapeStep.CancelOperation: View.CancelTool(); break;
                case EscapeStep.ClearSelection: selectTool.Select(null); ReturnAfterAutoSelect(); break;
                case EscapeStep.ReturnToTool: ReturnAfterAutoSelect(); break;
                case EscapeStep.SelectTool: SetTool(ToolKind.Select); break;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            DeleteSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && View.IsToolActive && View.Tool is PdfNotes.Editor.Tools.IMultiClickTool multi)
        {
            multi.Finish(); // a free polygon in progress
            e.Handled = true;
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        View.SetTemporaryHand(false);
    }

    // ----- status bar: page and zoom -----

    private void PreviousPageClick(object sender, RoutedEventArgs e) => viewModel.Navigation.PreviousPage();
    private void NextPageClick(object sender, RoutedEventArgs e) => viewModel.Navigation.NextPage();

    private void PageBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            viewModel.Navigation.GoToPageNumber(PageBox.Text);
            View.Focus();
            UpdateViewStatus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            View.Focus();
            e.Handled = true;
        }
    }

    private void PageBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateViewStatus();

    private void ZoomOutClick(object sender, RoutedEventArgs e) => viewModel.Navigation.ZoomBy(1 / 1.25);
    private void ZoomInClick(object sender, RoutedEventArgs e) => viewModel.Navigation.ZoomBy(1.25);
    private void FitWidthClick(object sender, RoutedEventArgs e) => viewModel.Navigation.FitWidth();
    private void FitPageClick(object sender, RoutedEventArgs e) => viewModel.Navigation.FitPage();

    /// <summary>Zoom presets of the status bar menu (spec 4.4).</summary>
    public static readonly int[] ZoomMenuPercents = ZoomSteps.MenuPercents;

    /// <summary>The zoom menu opened last (validation harness).</summary>
    public ContextMenu? LastZoomMenu { get; private set; }

    /// <summary>Clicking the zoom percentage opens the presets and the two fits, the current zoom checked (spec 4.4).</summary>
    private void ZoomMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ZoomButton, Placement = PlacementMode.Top };
        foreach (var (percent, current) in viewModel.Navigation.ZoomMenu())
        {
            var item = new MenuItem { Header = percent.ToString(CultureInfo.CurrentCulture) + "%", IsChecked = current };
            item.Click += (_, _) => viewModel.Navigation.SetZoom(percent / 100.0);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var width = new MenuItem { Header = Strings.Get("view.fitWidth") };
        width.Click += (_, _) => viewModel.Navigation.FitWidth();
        menu.Items.Add(width);
        var page = new MenuItem { Header = Strings.Get("view.fitPage") };
        page.Click += (_, _) => viewModel.Navigation.FitPage();
        menu.Items.Add(page);
        menu.IsOpen = true;
        LastZoomMenu = menu;
    }

    private void ZoomBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZoomBox.SelectedItem is ComboBoxItem { Content: string text }) viewModel.Navigation.SetZoomText(text);
        ZoomBox.SelectedIndex = -1;
        UpdateViewStatus();
    }

    private void ZoomBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Escape) return;
        if (e.Key == Key.Enter) viewModel.Navigation.SetZoomText(ZoomBox.Text);
        View.Focus();
        UpdateViewStatus();
        e.Handled = true;
    }

    private void ZoomBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateViewStatus();

    private void ContinuousClick(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.Continuous);
    private void SinglePageClick(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.SinglePage);

    public void SetViewMode(ViewMode mode) => viewModel.Navigation.SetMode(mode);

    // ----- closing (spec 18) -----

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closeConfirmed) return;
        FinishTextEditing();
        switch (workflow.CloseStep(backupRunning))
        {
            case CloseStep.Close:
                return;
            case CloseStep.WaitForSave:
                e.Cancel = true; // a save is writing the file; close after it finishes
                return;
            case CloseStep.WaitForBackup:
                e.Cancel = true; // an automatic backup is writing: close when it is done
                Dispatcher.InvokeAsync(async () => { while (backupRunning) await Task.Delay(50); Close(); });
                return;
        }
        e.Cancel = true;
        Dispatcher.InvokeAsync(async () =>
        {
            if (!await ConfirmDiscardChangesAsync()) return;
            closeConfirmed = true;
            Close();
        });
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        if (backup.Started) { backup.Stop(); backupStore.End(); } // a normal end: nothing to recover next time
        RememberPosition(document);
        RememberWindowPlacement();
        settings.Save();
        viewModel.Search.Cancel();
        View.Detach(); // a pending render tick must not reach the disposed scheduler
        scheduler?.Dispose();
        scheduler = null;
        workflow.Detach()?.Dispose();
    }
}
