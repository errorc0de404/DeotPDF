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

public partial class MainWindow : Window, IDocumentHost, IEditorDialogs
{
    private readonly AppSettings settings;
    private readonly DocumentWorkflow workflow; // D14d: new/open/save/export flow of the editor
    private readonly ToolSwitch tools;          // D14d: current tool and the automatic selection visit
    private PageRenderScheduler? scheduler; // page bitmaps of the open document
    private bool closeConfirmed;

    private OpenDocument? document => workflow.Document;
    private bool busy => workflow.IsBusy;
    private bool confirming => workflow.IsConfirming;

    static MainWindow() => PdfNotes.Text.WpfText.Register(); // D14a: the WPF text engine behind TextLayout

    public MainWindow()
    {
        Theme.Attach(this);
        settings = AppSettings.Load();
        workflow = new DocumentWorkflow(this, this); // before the window: command bindings ask it while the XAML loads
        tools = new ToolSwitch(settings);
        InitializeComponent();
        RestoreWindowPlacement();
        Theme.Use(settings.Theme, settings.Accent);
        showPageNumbers = settings.ShowPageNumbers;
        pageNumberSize = settings.PageNumberSize;
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(SystemSettingHook);
        ShowDarkPages(settings.DarkPages);
        View.SetMode(settings.ViewMode);
        View.ViewChanged += (_, _) => UpdateViewStatus();
        View.ViewChanged += (_, _) => PositionNoteCard();
        InitializeTools();
        InitializeLinks();
        InitializePagePanel();
        InitializeOutline();
        ShowFavorites(settings.ShowFavorites);
        InitializeRibbon();
        UpdateRecentButton();
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

    void IDocumentHost.DocumentLeaving(OpenDocument? previous)
    {
        RememberPosition(previous);
        if (NotesHidden) SetNotesHidden(false); // a new or opened document shows its notes (spec 24.10)
        if (previous is not null) previous.Session.StateChanged -= SessionStateChanged;
    }

    void IDocumentHost.DocumentReplaced(OpenDocument? previous, OpenDocument next)
    {
        linkDocument = next; sourceLinks.Clear(); backPlaces.Clear(); forwardPlaces.Clear(); // links and jumps belong to one document
        next.Session.StateChanged += SessionStateChanged;
        var previousScheduler = scheduler;
        scheduler = new PageRenderScheduler(next.Renderer, Dispatcher);
        View.SetDocument(next.Session.Current.Pages, scheduler);
        selectTool.Select(null);
        activePageId = null; pageClipboard = null;
        pageThumbs.Clear();
        RefreshPagePanel();
        searchRun?.Cancel();
        ApplySearch([], null);
        ScheduleSearch(keep: false);
        previousScheduler?.Dispose();
        previous?.Dispose();
        ResetBackup();
        ShowingFile();
        LoadOutline();
        UpdateTitle();
        UpdateViewStatus();
        UpdateStyleBar();
        CountForeign();
        UpdateHistoryTips();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The quick undo/redo buttons name the step they would take (spec 13.3, D14g): `실행 취소: 필기 삭제 (Ctrl+Z)`.</summary>
    private void UpdateHistoryTips()
    {
        if (document?.Session is not { } session) return;
        UndoButton.ToolTip = session.UndoLabel is { } undo ? Strings.Format("ribbon.undoNamed", ("name", Strings.Get(undo))) : Strings.Get("ribbon.undoTip");
        RedoButton.ToolTip = session.RedoLabel is { } redo ? Strings.Format("ribbon.redoNamed", ("name", Strings.Get(redo))) : Strings.Get("ribbon.redoTip");
    }

    private void SessionStateChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, document?.Session)) return;
        View.UpdatePages(document!.Session.Current.Pages);
        selectTool.Revalidate();
        ScheduleSearch(keep: true); // text edits, deleted or moved pages: results follow the document
        // A page structure change makes glyph carets of the text selection refer to another page.
        if (!pageThumbs.Select(t => t.PageId).SequenceEqual(document.Session.Current.Pages.Select(p => p.Id)) ||
            pageThumbs.Any(t => document.Session.Current.GetPage(t.PageId).Setup != t.Rendered?.Setup)) selectTool.ClearTextSelection();
        RefreshPagePanel();
        RefreshOutline();
        UpdateStyleBar();
        UpdateTitle();
        CountForeign(); // imported pages may bring other apps' annotations
        UpdateHistoryTips();
        CommandManager.InvalidateRequerySuggested();
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

    // ----- the workflow's host and dialogs (D14d) -----

    void IDocumentHost.CancelTool() => View.CancelTool();
    void IDocumentHost.SetStatus(string text) => StatusText.Text = text;

    void IDocumentHost.StateChanged()
    {
        UpdateTitle();
        CommandManager.InvalidateRequerySuggested();
    }

    void IDocumentHost.Opened(string path)
    {
        AddRecent(path);
        RestorePosition(path);
    }

    void IDocumentHost.Saved(string path)
    {
        AddRecent(path);
        BackupSaved();
        ShowingFile();
    }

    bool IDocumentHost.ShowOpenElsewhere(string path)
    {
        if (BackupStore.WindowShowing(Path.GetDirectoryName(AppSettings.DefaultPath)!, path, backupStore) is not { } other) return false;
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

    private void UpdateTitle() =>
        Title = document is null ? App.DisplayName : $"{document.DisplayName}{(document.Session.IsDirty ? " *" : "")} - {App.DisplayName}";

    private void UpdateViewStatus()
    {
        if (!PageBox.IsKeyboardFocusWithin) PageBox.Text = (View.CurrentPageIndex + 1).ToString(CultureInfo.CurrentCulture);
        PageCountText.Text = $"/ {View.PageCount}";
        if (!ZoomBox.IsKeyboardFocusWithin) ZoomBox.Text = FormatZoom(View.Zoom);
        ZoomText.Text = FormatZoom(View.Zoom);
        ContinuousButton.IsChecked = View.Mode == ViewMode.Continuous;
        SinglePageButton.IsChecked = View.Mode == ViewMode.SinglePage;
    }

    private static string FormatZoom(double zoom) => ZoomSteps.Format(zoom, CultureInfo.CurrentCulture);

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

    private bool showPageNumbers;

    /// <summary>Page numbers drawn on the pages (spec 4.5; the settings window may be previewing a change).</summary>
    public bool PageNumbersShown => showPageNumbers;

    /// <summary>The stored 화면 settings (spec 18.1).</summary>
    public DisplayValues DisplaySettings => new(settings.Theme, settings.Accent, settings.ShowPageNumbers, settings.AutoSwitchTabs, settings.DarkPages, settings.ShowFavorites,
        settings.PageNumberSize);

    /// <summary>Shows theme, accent color and page numbers now without storing them (settings window preview).</summary>
    public void PreviewDisplay(DisplayValues values)
    {
        Theme.Use(values.Theme, values.Accent);
        ShowDarkPages(values.DarkPages);
        ShowFavorites(values.Favorites);
        if (pageNumberSize != values.NumberSize) { pageNumberSize = values.NumberSize; View.RefreshOverlays(); }
        if (showPageNumbers != values.PageNumbers)
        {
            showPageNumbers = values.PageNumbers;
            View.RefreshOverlays();
        }
    }

    /// <summary>Applies and stores the 화면 settings (an app setting: no Undo, the document stays unchanged).</summary>
    public void SetDisplay(DisplayValues values)
    {
        PreviewDisplay(values);
        settings.Theme = values.Theme;
        settings.Accent = values.Accent;
        settings.ShowPageNumbers = values.PageNumbers;
        settings.AutoSwitchTabs = values.AutoSwitchTabs;
        settings.DarkPages = values.DarkPages;
        settings.ShowFavorites = values.Favorites;
        settings.PageNumberSize = values.NumberSize;
        settings.Save();
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
            settings.Language),
            () => Prompt(Strings.Get("settings.backupInPlaceConfirm"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes,
            PreviewDisplay, Strings.Discover(AppSettings.LanguageFolders), new(backupStore.Usage, ClearBackups)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } values) { PreviewDisplay(display); return false; } // cancel undoes the preview
        SetDisplay(values.Display ?? display);
        settings.QuickErase = values.QuickErase;
        settings.PageDrag = values.PageDrag;
        settings.AutoBackup = values.Backup.Enabled;
        settings.BackupIntervalMinutes = values.Backup.IntervalMinutes;
        settings.BackupMode = values.Backup.Mode;
        settings.BackupFolder = string.Equals(values.Backup.Folder, AppSettings.DefaultBackupFolder, StringComparison.OrdinalIgnoreCase) ? null : values.Backup.Folder;
        settings.BackupKeep = values.Backup.Keep;
        bool languageChanged = !string.Equals(settings.Language, values.Language, StringComparison.OrdinalIgnoreCase);
        settings.Language = values.Language;
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
        bool pdfs = !busy && !importing && DroppedPdfs(e) is not null;
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
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) StatusText.Text = Strings.Get("drop.unsupported");
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
        if (Keyboard.Modifiers == ModifierKeys.Control && ZoomShortcut(e.Key)) { e.Handled = true; return; } // also while editing text (spec 24.2)
        // Ctrl+1 … Ctrl+4: the pen favorites (spec 17, 24.7, D14g), also while editing text, not in other input boxes.
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D4 or >= Key.NumPad1 and <= Key.NumPad4 && (textEditor is not null || !IsTextInputFocused))
        {
            int slot = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 : e.Key - Key.D1;
            if (settings.Favorites[slot] is not null) ApplyFavorite(slot);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.System && Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Left or Key.Right && !IsTextInputFocused)
        {
            if (e.SystemKey == Key.Left) GoBack(); else GoForward(); // jumps of links and the outline (spec 24.3)
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
        if (IsCanvasKeyboardFocus && PageKey(e)) { e.Handled = true; return; }
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

    /// <summary>Ctrl+= (and numpad +) next step, Ctrl+- previous step, Ctrl+0 fit width — around the view center (spec 24.2).</summary>
    private bool ZoomShortcut(Key key)
    {
        switch (key)
        {
            case Key.OemPlus or Key.Add:
                ZoomByStep(1);
                return true;
            case Key.OemMinus or Key.Subtract:
                ZoomByStep(-1);
                return true;
            case Key.D0 or Key.NumPad0:
                View.FitWidth();
                return true;
            default:
                return false;
        }
    }

    /// <summary>The next (<paramref name="direction"/> 1) or previous (-1) zoom step around the view center (spec 24.2).</summary>
    public void ZoomByStep(int direction)
    {
        View.SetZoom(ZoomSteps.Step(View.Zoom, direction));
    }

    /// <summary>
    /// Keyboard page moves on the canvas (spec 4.4, 2026-10-14): ←/→ scroll sideways and turn the page at the edge, PageUp/PageDown
    /// go to the previous/next page's top in both view modes, Ctrl+Home/Ctrl+End to the first/last page, ↑/↓ scroll a line;
    /// arrows with a selection move it (handled before). A view change only: no Undo, no unsaved mark.
    /// </summary>
    private bool PageKey(KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        // ↑/↓ scroll here, not in the ScrollViewer: it first predicts focus, and with no focusable element above the canvas
        // (select, text, lasso, note tools) it predicts the canvas itself and swallows ↑ without scrolling (2026-10-26).
        if (modifiers == ModifierKeys.None && e.Key is Key.Up or Key.Down)
        {
            if (e.Key == Key.Up) View.LineUp(); else View.LineDown();
            return true;
        }
        // While a stroke or another press is in progress the page stays (the 2026-10-15 crash: the page under the pen left the view).
        if (View.IsToolActive) return e.Key is Key.Left or Key.Right or Key.PageUp or Key.PageDown || modifiers == ModifierKeys.Control && e.Key is Key.Home or Key.End;
        if (modifiers == ModifierKeys.None && e.Key is Key.Left or Key.Right)
        {
            View.ArrowSideways(e.Key == Key.Left ? -1 : 1, e.IsRepeat);
            return true;
        }
        if (modifiers == ModifierKeys.None && e.Key is Key.PageUp or Key.PageDown)
        {
            View.GoToPage(View.CurrentPageIndex + (e.Key == Key.PageUp ? -1 : 1));
            return true;
        }
        if (modifiers == ModifierKeys.Control && e.Key is Key.Home or Key.End)
        {
            View.GoToPage(e.Key == Key.Home ? 0 : View.PageCount - 1);
            return true;
        }
        return false;
    }

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

    private void PreviousPageClick(object sender, RoutedEventArgs e) => View.PreviousPage();
    private void NextPageClick(object sender, RoutedEventArgs e) => View.NextPage();

    private void PageBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (int.TryParse(PageBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int number))
                View.GoToPage(number - 1);
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

    private void ZoomOutClick(object sender, RoutedEventArgs e) => View.ZoomBy(1 / 1.25);
    private void ZoomInClick(object sender, RoutedEventArgs e) => View.ZoomBy(1.25);
    private void FitWidthClick(object sender, RoutedEventArgs e) => View.FitWidth();
    private void FitPageClick(object sender, RoutedEventArgs e) => View.FitPage();

    /// <summary>Zoom presets of the status bar menu (spec 4.4).</summary>
    public static readonly int[] ZoomMenuPercents = ZoomSteps.MenuPercents;

    /// <summary>The zoom menu opened last (validation harness).</summary>
    public ContextMenu? LastZoomMenu { get; private set; }

    /// <summary>Clicking the zoom percentage opens the presets and the two fits, the current zoom checked (spec 4.4).</summary>
    private void ZoomMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ZoomButton, Placement = PlacementMode.Top };
        int current = (int)Math.Round(View.Zoom * 100);
        foreach (int percent in ZoomMenuPercents)
        {
            var item = new MenuItem { Header = percent.ToString(CultureInfo.CurrentCulture) + "%", IsChecked = percent == current };
            item.Click += (_, _) => View.SetZoom(percent / 100.0);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var width = new MenuItem { Header = Strings.Get("view.fitWidth") };
        width.Click += (_, _) => View.FitWidth();
        menu.Items.Add(width);
        var page = new MenuItem { Header = Strings.Get("view.fitPage") };
        page.Click += (_, _) => View.FitPage();
        menu.Items.Add(page);
        menu.IsOpen = true;
        LastZoomMenu = menu;
    }

    private void ZoomBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZoomBox.SelectedItem is ComboBoxItem { Content: string text } && TryParseZoom(text, out double zoom)) View.SetZoom(zoom);
        ZoomBox.SelectedIndex = -1;
        UpdateViewStatus();
    }

    private void ZoomBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Escape) return;
        if (e.Key == Key.Enter && TryParseZoom(ZoomBox.Text, out double zoom)) View.SetZoom(zoom);
        View.Focus();
        UpdateViewStatus();
        e.Handled = true;
    }

    private void ZoomBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateViewStatus();

    private static bool TryParseZoom(string text, out double zoom) => ZoomSteps.TryParse(text, CultureInfo.CurrentCulture, out zoom);

    private void ContinuousClick(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.Continuous);
    private void SinglePageClick(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.SinglePage);

    public void SetViewMode(ViewMode mode)
    {
        View.SetMode(mode);
        settings.ViewMode = mode;
        settings.Save();
        UpdateViewStatus();
    }

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
        backupTimer?.Stop();
        if (backupTimer is not null) backupStore.End(); // a normal end: nothing to recover next time
        RememberPosition(document);
        RememberWindowPlacement();
        settings.Save();
        searchRun?.Cancel();
        searchTimer?.Stop();
        View.Detach(); // a pending render tick must not reach the disposed scheduler
        scheduler?.Dispose();
        scheduler = null;
        workflow.Detach()?.Dispose();
    }
}
