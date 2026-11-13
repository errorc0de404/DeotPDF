using System.Globalization;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Services;

namespace PdfNotes.ViewModels;

/// <summary>Automatic backup values of the settings window (spec 18.2).</summary>
public sealed record BackupValues(bool Enabled, int IntervalMinutes, BackupMode Mode, string Folder, int Keep);

/// <summary>Values of the settings window (spec 18.1).</summary>
public sealed record SettingsValues(bool QuickErase, bool PageDrag, BackupValues Backup, DisplayValues? Display = null, string Language = Strings.BuiltInLanguage,
    bool CheckUpdates = false, bool FingerDrawing = false, bool PenPressure = true, bool LiveLink = false);

/// <summary>What 정보 does (roadmap 1.1): check for a new version now, and open a web page.</summary>
public sealed record AboutActions(Func<Task<UpdateResult>> Check, Action<string> Open);

/// <summary>What 저장 shows and does about the backups the app made (spec 18.2, D14g).</summary>
public sealed record BackupActions(Func<(int Count, long Bytes)> Usage, Func<Task<bool>> Clear);

/// <summary>What the settings window asks of the view: a folder to back up into, and showing a folder.</summary>
public sealed record SettingsFolders(Func<string, Task<string?>> Choose, Func<string, Task> Open);

/// <summary>One accent color of 화면 (D13d).</summary>
public sealed class AccentOption(AccentColor color, string name, Action<AccentOption> chosen) : ObservableObject
{
    private bool isChecked;
    private int look;

    public AccentColor Color => color;
    public string Name => name;

    public bool IsChecked
    {
        get => isChecked;
        set { if (SetProperty(ref isChecked, value) && value) chosen(this); }
    }

    /// <summary>Changes when the theme changes: the accents have a lighter variant in the dark theme.</summary>
    public int Look { get => look; private set => SetProperty(ref look, value); }

    internal void Restyle() => Look++;
}

/// <summary>
/// The settings window (spec 18.1): categories 필기·저장·화면·정보, OK applies, Cancel discards. 화면 choices show at once
/// through the preview; the caller undoes it on cancel. Choosing to back up into the original file asks once (on Android
/// the answer comes later, and OK goes on from there).
/// </summary>
public sealed class SettingsDialogViewModel : ObservableObject
{
    private readonly Texts texts;
    private readonly BackupMode initialMode;
    private readonly Func<Task<bool>> confirmOriginal;
    private readonly Action<DisplayValues>? preview;
    private readonly IReadOnlyList<LanguagePack> packs;
    private readonly BackupActions? backups;
    private readonly AboutActions? about;
    private readonly SettingsFolders? folders;
    private readonly bool ready;
    private bool originalConfirmed;

    public SettingsDialogViewModel(SettingsValues current, Texts texts, Func<Task<bool>> confirmOriginal, Action<DisplayValues>? preview = null,
        IReadOnlyList<LanguagePack>? languages = null, BackupActions? backups = null, AboutActions? about = null, SettingsFolders? folders = null)
    {
        this.texts = texts;
        this.confirmOriginal = confirmOriginal;
        this.preview = preview;
        this.backups = backups;
        this.about = about;
        this.folders = folders;
        initialMode = current.Backup.Mode;
        packs = languages ?? [Strings.Korean];
        Categories = [texts.Get("settings.sectionNotes"), texts.Get("settings.sectionSave"), texts.Get("settings.sectionDisplay"), texts.Get("settings.sectionAbout")];
        Intervals = [.. AppSettings.BackupIntervals.Select(m => texts.Format("settings.minutes", ("minutes", m)))];
        NumberSizes = [texts.Get("settings.small"), texts.Get("settings.normal"), texts.Get("settings.large")];
        Languages = [.. packs.Select(p => p.Name)];
        Accents = [.. new (AccentColor Color, string Key)[] { (AccentColor.Blue, "color.blue"), (AccentColor.Green, "color.green"), (AccentColor.Purple, "color.purple"),
            (AccentColor.Orange, "color.orange"), (AccentColor.Red, "color.red"), (AccentColor.Gray, "color.gray") }
            .Select(a => new AccentOption(a.Color, texts.Get(a.Key), Choose))];

        quickErase = current.QuickErase;
        pageDrag = current.PageDrag;
        fingerDrawing = current.FingerDrawing;
        liveLink = current.LiveLink;
        penPressure = current.PenPressure;
        autoBackup = current.Backup.Enabled;
        intervalIndex = Math.Max(0, Array.IndexOf(AppSettings.BackupIntervals, current.Backup.IntervalMinutes));
        toOriginal = current.Backup.Mode == BackupMode.Original;
        backupFolder = current.Backup.Folder;
        keep = current.Backup.Keep.ToString(CultureInfo.CurrentCulture);
        var display = current.Display ?? DisplayValues.Default;
        theme = display.Theme;
        foreach (var accent in Accents) accent.IsChecked = accent.Color == display.Accent;
        pageNumbers = display.PageNumbers;
        autoTabs = display.AutoSwitchTabs;
        darkPages = display.DarkPages;
        favorites = display.Favorites;
        numberSizeIndex = (int)display.NumberSize;
        languageIndex = Math.Max(0, packs.ToList().FindIndex(p => string.Equals(p.Language, current.Language, StringComparison.OrdinalIgnoreCase)));
        checkUpdates = current.CheckUpdates;

        ClearCommand = new AsyncCommand(ClearAsync, () => usageCount > 0);
        ChangeFolderCommand = new AsyncCommand(ChangeFolderAsync, () => folders is not null);
        OpenFolderCommand = new AsyncCommand(() => folders?.Open(BackupFolder) ?? Task.CompletedTask, () => folders is not null);
        CheckNowCommand = new AsyncCommand(CheckNowAsync, () => about is not null);
        OpenLinkCommand = new Command<string>(url => about?.Open(url));
        ShowUsage();
        ready = true;
    }

    // ----- categories -----

    public string[] Categories { get; }

    private int category;
    public int Category
    {
        get => category;
        set
        {
            if (!SetProperty(ref category, value)) return;
            OnPropertyChanged(nameof(IsNotes)); OnPropertyChanged(nameof(IsSave)); OnPropertyChanged(nameof(IsDisplay)); OnPropertyChanged(nameof(IsAbout));
        }
    }

    public bool IsNotes => Category == 0;
    public bool IsSave => Category == 1;
    public bool IsDisplay => Category == 2;
    public bool IsAbout => Category == 3; // last: new categories go after (Probes find them by position)

    // ----- 필기 -----

    private bool quickErase, pageDrag, fingerDrawing, penPressure, liveLink;
    public bool QuickErase { get => quickErase; set => SetProperty(ref quickErase, value); }
    public bool PageDrag { get => pageDrag; set => SetProperty(ref pageDrag, value); }
    public bool FingerDrawing { get => fingerDrawing; set => SetProperty(ref fingerDrawing, value); }

    /// <summary>AI 도구 연결 (MCP_LIVE_PLAN 2): AI clients on this computer may ask for the open document.</summary>
    public bool LiveLink { get => liveLink; set => SetProperty(ref liveLink, value); }
    public bool PenPressure { get => penPressure; set => SetProperty(ref penPressure, value); }

    // ----- 저장 -----

    private bool autoBackup, toOriginal;
    private int intervalIndex, usageCount;
    private string backupFolder, keep, usage = "";

    public bool AutoBackup { get => autoBackup; set => SetProperty(ref autoBackup, value); }
    public string[] Intervals { get; }
    public int IntervalIndex { get => intervalIndex; set => SetProperty(ref intervalIndex, value); }

    public bool BackupToFolder
    {
        get => !toOriginal;
        set { if (value) SetMode(original: false); }
    }

    public bool BackupToOriginal
    {
        get => toOriginal;
        set { if (value) SetMode(original: true); }
    }

    private void SetMode(bool original)
    {
        if (!SetProperty(ref toOriginal, original, nameof(BackupToOriginal))) return;
        OnPropertyChanged(nameof(BackupToFolder));
    }

    public string BackupFolder { get => backupFolder; set => SetProperty(ref backupFolder, value); }
    public string Keep { get => keep; set => SetProperty(ref keep, value); }
    public string Usage { get => usage; private set => SetProperty(ref usage, value); }

    public AsyncCommand ClearCommand { get; }
    public AsyncCommand ChangeFolderCommand { get; }
    public AsyncCommand OpenFolderCommand { get; }

    private void ShowUsage()
    {
        var (count, bytes) = backups?.Usage() ?? (0, 0);
        usageCount = count;
        Usage = texts.Format("settings.backupUsage", ("count", count), ("size", FileSizes.Format(bytes)));
        ClearCommand?.Refresh();
    }

    private async Task ClearAsync()
    {
        if (backups is not null && await backups.Clear()) ShowUsage();
    }

    private async Task ChangeFolderAsync()
    {
        if (folders is not null && await folders.Choose(BackupFolder) is { } path) BackupFolder = path;
    }

    // ----- 화면 (D13d): every choice shows at once -----

    private ThemeChoice theme;
    private bool pageNumbers, autoTabs, darkPages, favorites;
    private int numberSizeIndex, languageIndex;

    public bool ThemeSystem { get => theme == ThemeChoice.System; set { if (value) SetTheme(ThemeChoice.System); } }
    public bool ThemeLight { get => theme == ThemeChoice.Light; set { if (value) SetTheme(ThemeChoice.Light); } }
    public bool ThemeDark { get => theme == ThemeChoice.Dark; set { if (value) SetTheme(ThemeChoice.Dark); } }

    private void SetTheme(ThemeChoice value)
    {
        if (theme == value) return;
        theme = value;
        OnPropertyChanged(nameof(ThemeSystem)); OnPropertyChanged(nameof(ThemeLight)); OnPropertyChanged(nameof(ThemeDark));
        Preview();
    }

    public IReadOnlyList<AccentOption> Accents { get; }
    public bool PageNumbers { get => pageNumbers; set { if (SetProperty(ref pageNumbers, value)) Preview(); } }
    public string[] NumberSizes { get; }
    public int NumberSizeIndex { get => numberSizeIndex; set { if (SetProperty(ref numberSizeIndex, value)) Preview(); } }
    public bool DarkPages { get => darkPages; set { if (SetProperty(ref darkPages, value)) Preview(); } }
    public bool Favorites { get => favorites; set { if (SetProperty(ref favorites, value)) Preview(); } }
    public bool AutoTabs { get => autoTabs; set => SetProperty(ref autoTabs, value); }
    public string[] Languages { get; }
    public int LanguageIndex { get => languageIndex; set => SetProperty(ref languageIndex, value); }

    public DisplayValues Display => new(theme, Accents.FirstOrDefault(a => a.IsChecked)?.Color ?? AccentColor.Blue, PageNumbers, AutoTabs, DarkPages, Favorites,
        (PageNumberSize)Math.Max(0, NumberSizeIndex));

    /// <summary>One accent at a time.</summary>
    private void Choose(AccentOption chosen)
    {
        foreach (var accent in Accents ?? []) if (!ReferenceEquals(accent, chosen)) accent.IsChecked = false;
        Preview();
    }

    private void Preview()
    {
        if (!ready) return;
        preview?.Invoke(Display);
        foreach (var accent in Accents) accent.Restyle();
    }

    // ----- 정보 (roadmap 1.1) -----

    private bool checkUpdates;
    private string updateStatus = "";
    private string? downloadUrl;

    public string Version => UpdateCheck.CurrentVersion;
    public string License => "GPL-3.0"; // license id: not UI text (D14e)
    public string ProjectPage => UpdateCheck.ProjectPage;
    public string NoticesPage => UpdateCheck.NoticesPage;
    public bool CheckUpdates { get => checkUpdates; set => SetProperty(ref checkUpdates, value); }
    public string UpdateStatus { get => updateStatus; private set => SetProperty(ref updateStatus, value); }

    /// <summary>Where the newer release is, while a check found one.</summary>
    public string? DownloadUrl
    {
        get => downloadUrl;
        private set { if (SetProperty(ref downloadUrl, value)) OnPropertyChanged(nameof(HasDownload)); }
    }

    public bool HasDownload => DownloadUrl is not null;
    public AsyncCommand CheckNowCommand { get; }
    public Command<string> OpenLinkCommand { get; }

    private async Task CheckNowAsync()
    {
        UpdateStatus = texts.Get("about.checking");
        DownloadUrl = null;
        var result = await about!.Check();
        switch (result.State)
        {
            case UpdateState.Available:
                UpdateStatus = texts.Format("about.available", ("version", result.Version!));
                DownloadUrl = result.Url ?? UpdateCheck.ReleasesPage;
                break;
            case UpdateState.UpToDate: UpdateStatus = texts.Get("about.upToDate"); break;
            default: UpdateStatus = texts.Get("about.failed"); break;
        }
    }

    // ----- OK -----

    public SettingsValues? Result { get; private set; }

    /// <summary>
    /// OK: the values, or false while switching to the original file waits for its confirmation; <paramref name="finish"/>
    /// closes the window when a later yes completes it. A no goes back to the backup folder and keeps the window open.
    /// </summary>
    public bool Accept(Action finish)
    {
        var mode = BackupToOriginal ? BackupMode.Original : BackupMode.Folder;
        if (AutoBackup && mode == BackupMode.Original && initialMode != BackupMode.Original && !originalConfirmed)
        {
            Fire.AndReport(ConfirmOriginalAsync(finish));
            return false;
        }
        int count = int.TryParse((Keep ?? "").Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) ? n : 10;
        Result = new(QuickErase, PageDrag, new(AutoBackup, AppSettings.BackupIntervals[Math.Max(0, IntervalIndex)], mode, BackupFolder ?? "",
            Math.Clamp(count, AppSettings.MinBackupKeep, AppSettings.MaxBackupKeep)), Display,
            packs[Math.Max(0, LanguageIndex)].Language, CheckUpdates, FingerDrawing, PenPressure, LiveLink);
        return true;
    }

    private async Task ConfirmOriginalAsync(Action finish)
    {
        if (await confirmOriginal())
        {
            originalConfirmed = true;
            if (Accept(finish)) finish();
        }
        else BackupToFolder = true;
    }
}
