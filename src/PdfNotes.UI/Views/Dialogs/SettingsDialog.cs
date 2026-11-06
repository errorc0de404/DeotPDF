using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PdfNotes.UI.Themes;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Services;
using PdfNotes.ViewModels;

namespace PdfNotes.UI.Views;

/// <summary>Automatic backup values of the settings window (spec 18.2).</summary>
public sealed record BackupValues(bool Enabled, int IntervalMinutes, BackupMode Mode, string Folder, int Keep);

/// <summary>Values of the settings window (spec 18.1), as in the WPF app.</summary>
public sealed record SettingsValues(bool QuickErase, bool PageDrag, BackupValues Backup, DisplayValues? Display = null, string Language = Strings.BuiltInLanguage,
    bool CheckUpdates = false, bool FingerDrawing = false, bool PenPressure = true);

/// <summary>What 정보 does (roadmap 1.1): check for a new version now, and open a web page.</summary>
public sealed record AboutActions(Func<Task<UpdateResult>> Check, Action<string> Open);

/// <summary>What 저장 shows and does about the backups the app made (spec 18.2, D14g).</summary>
public sealed record BackupActions(Func<(int Count, long Bytes)> Usage, Func<bool> Clear);

/// <summary>
/// The settings window (spec 18.1, the WPF SettingsDialog): categories 필기·저장·화면·정보 on the left, their items on the
/// right. OK applies, Cancel discards. 화면 choices show at once through the preview; the caller undoes it on cancel.
/// Choosing to back up into the original file asks once for confirmation.
/// </summary>
public sealed class SettingsDialog : DialogView
{
    private readonly CheckBox quickErase = new() { Name = "QuickEraseBox", Content = Strings.Get("settings.quickErase") };
    private readonly CheckBox pageDrag = new() { Name = "PageDragBox", Content = Strings.Get("settings.pageDrag") };
    private readonly CheckBox fingerDrawing = new() { Name = "FingerDrawingBox", Content = Strings.Get("settings.fingerDrawing") };
    private readonly CheckBox penPressure = new() { Name = "PenPressureBox", Content = Strings.Get("settings.penPressure") };
    private readonly ComboBox numberSize = new() { Name = "PageNumberSizeBox", Width = 90, ItemsSource = new[] { Strings.Get("settings.small"), Strings.Get("settings.normal"), Strings.Get("settings.large") } };
    private readonly CheckBox darkPages = new() { Name = "DarkPagesBox", Content = Strings.Get("settings.darkPages") };
    private readonly CheckBox favorites = new() { Name = "FavoritesBox", Content = Strings.Get("settings.favorites") };
    private readonly CheckBox backup = new() { Name = "AutoBackupBox", Content = Strings.Get("settings.autoBackup") };
    private readonly ComboBox interval = new() { Name = "BackupIntervalBox", Width = 90 };
    private readonly RadioButton folderMode = new() { Name = "BackupFolderMode", Content = Strings.Get("settings.backupToFolder"), GroupName = "BackupMode" };
    private readonly RadioButton originalMode = new() { Name = "BackupOriginalMode", Content = Strings.Get("settings.backupInPlace"), GroupName = "BackupMode" };
    private readonly TextBox folder = new() { Name = "BackupFolderBox", Width = 230, IsReadOnly = true };
    private readonly TextBox keep = new() { Name = "BackupKeepBox", Width = 50 };
    private readonly TextBlock usage = new() { Name = "BackupUsageText", VerticalAlignment = VerticalAlignment.Center };
    private readonly ListBox categories = new() { Name = "CategoryList", Width = 120, BorderThickness = new Thickness(0, 0, 1, 0), Background = Brushes.Transparent };
    private readonly ContentControl page = new() { Margin = new Thickness(20, 4, 4, 4) };
    private readonly RadioButton[] themes =
    [
        new() { Name = "ThemeSystem", Content = Strings.Get("settings.themeSystem"), GroupName = "Theme", Tag = ThemeChoice.System },
        new() { Name = "ThemeLight", Content = Strings.Get("settings.themeLight"), GroupName = "Theme", Tag = ThemeChoice.Light },
        new() { Name = "ThemeDark", Content = Strings.Get("settings.themeDark"), GroupName = "Theme", Tag = ThemeChoice.Dark },
    ];
    private readonly RadioButton[] accents;
    private readonly CheckBox pageNumbers = new() { Name = "PageNumbersBox", Content = Strings.Get("settings.showPageNumbers") };
    private readonly CheckBox autoTabs = new() { Name = "AutoTabsBox", Content = Strings.Get("settings.autoTabs") };
    private readonly ComboBox language = new() { Name = "LanguageBox", MinWidth = 140 };
    private readonly CheckBox checkUpdates = new() { Name = "CheckUpdatesBox", Content = Strings.Get("about.checkAtStart"), Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBlock updateStatus = new() { Name = "UpdateStatusText", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly BackupMode initialMode;
    private readonly Func<bool> confirmOriginal;
    private readonly Action<DisplayValues>? preview;
    private readonly IReadOnlyList<LanguagePack> packs;
    private bool built;

    public SettingsValues? Result { get; private set; }

    private static readonly (AccentColor Color, string Name)[] AccentNames =
        [(AccentColor.Blue, Strings.Get("color.blue")), (AccentColor.Green, Strings.Get("color.green")), (AccentColor.Purple, Strings.Get("color.purple")), (AccentColor.Orange, Strings.Get("color.orange")), (AccentColor.Red, Strings.Get("color.red")), (AccentColor.Gray, Strings.Get("color.gray"))];

    public SettingsDialog(SettingsValues current, Func<bool> confirmOriginal, Action<DisplayValues>? preview = null, IReadOnlyList<LanguagePack>? languages = null,
        BackupActions? backups = null, AboutActions? about = null)
    {
        this.confirmOriginal = confirmOriginal;
        this.preview = preview;
        initialMode = current.Backup.Mode;
        Title = Strings.Get("command.settings");
        PreferredSize = (600, 420, 600, 420);
        Resizable = false;
        categories.Bind(BorderBrushProperty, categories.GetResourceObservable("Divider"));
        quickErase.IsChecked = current.QuickErase;
        pageDrag.IsChecked = current.PageDrag;
        fingerDrawing.IsChecked = current.FingerDrawing;
        penPressure.IsChecked = current.PenPressure;
        backup.IsChecked = current.Backup.Enabled;
        interval.ItemsSource = AppSettings.BackupIntervals.Select(m => Strings.Format("settings.minutes", ("minutes", m))).ToArray();
        interval.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.BackupIntervals, current.Backup.IntervalMinutes));
        (current.Backup.Mode == BackupMode.Original ? originalMode : folderMode).IsChecked = true;
        folder.Text = current.Backup.Folder;
        keep.Text = current.Backup.Keep.ToString(CultureInfo.CurrentCulture);
        var change = new Button { Name = "BackupFolderChange", Content = Strings.Get("settings.change"), Padding = new Thickness(8, 1), Margin = new Thickness(6, 0, 0, 0) };
        change.Click += async (_, _) =>
        {
            var start = Directory.Exists(folder.Text) ? await StorageProvider!.TryGetFolderFromPathAsync(folder.Text!) : null;
            var chosen = await StorageProvider!.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.Get("settings.backupFolder"), SuggestedStartLocation = start });
            if (chosen.Count > 0 && chosen[0].TryGetLocalPath() is { } path) folder.Text = path;
        };
        var open = new Button { Name = "BackupFolderOpen", Content = Strings.Get("settings.openFolder"), Padding = new Thickness(8, 1), Margin = new Thickness(6, 0, 0, 0) };
        open.Click += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(folder.Text ?? "");
                if (Launcher is { } launcher) await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder.Text!));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        };
        var backupItems = new StackPanel { Margin = new Thickness(22, 0, 0, 0) };
        backupItems.Children.Add(Row(Strings.Get("settings.interval"), interval));
        backupItems.Children.Add(Row(Strings.Get("settings.mode"), new StackPanel { Children = { folderMode, originalMode } }));
        backupItems.Children.Add(Row(Strings.Get("settings.backupFolder"), folder, change, open));
        backupItems.Children.Add(Row(Strings.Get("settings.keep"), keep, new TextBlock { Text = Strings.Get("settings.keepUnit"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) }));
        backupItems.IsEnabled = backup.IsChecked == true;
        var clear = new Button { Name = "BackupClear", Content = Strings.Get("settings.clearBackups"), Padding = new Thickness(8, 1), Margin = new Thickness(10, 0, 0, 0) };
        void ShowUsage()
        {
            var (count, bytes) = backups?.Usage() ?? (0, 0);
            usage.Text = Strings.Format("settings.backupUsage", ("count", count), ("size", FileSizes.Format(bytes)));
            clear.IsEnabled = count > 0;
        }
        clear.Click += (_, _) => { if (backups?.Clear() == true) ShowUsage(); };
        ShowUsage();
        var usageRow = Row(Strings.Get("settings.backupUsageLabel"), usage, clear);
        backup.IsCheckedChanged += (_, _) => backupItems.IsEnabled = backup.IsChecked == true;

        // 화면 (D13d): every choice shows at once.
        var display = current.Display ?? DisplayValues.Default;
        var toolButton = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource("ToolButton")!;
        accents = [.. AccentNames.Select(a =>
        {
            var button = new RadioButton { Name = "Accent" + a.Color, GroupName = "Accent", Tag = a.Color, Theme = toolButton, Padding = new Thickness(5), Content = new Ellipse { Width = 16, Height = 16 } };
            ToolTip.SetTip(button, a.Name);
            return button;
        })];
        foreach (var button in themes) button.IsChecked = (ThemeChoice)button.Tag! == display.Theme;
        foreach (var button in accents) button.IsChecked = (AccentColor)button.Tag! == display.Accent;
        pageNumbers.IsChecked = display.PageNumbers;
        autoTabs.IsChecked = display.AutoSwitchTabs;
        darkPages.IsChecked = display.DarkPages;
        numberSize.SelectedIndex = (int)display.NumberSize;
        favorites.IsChecked = display.Favorites;
        ShowAccents();
        numberSize.SelectionChanged += (_, _) => Preview();
        foreach (var box in new[] { darkPages, favorites, pageNumbers }) box.IsCheckedChanged += (_, _) => Preview();
        foreach (var button in themes.Concat(accents)) button.IsCheckedChanged += (_, _) => { if (button.IsChecked == true) Preview(); };
        var themeList = new StackPanel();
        foreach (var button in themes) { button.Margin = new Thickness(0, 2); themeList.Children.Add(button); }
        var accentList = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in accents) accentList.Children.Add(button);
        var themeRow = Row(Strings.Get("settings.theme"), themeList);
        ((TextBlock)themeRow.Children[0]).VerticalAlignment = VerticalAlignment.Top;
        ((TextBlock)themeRow.Children[0]).Margin = new Thickness(0, 6, 0, 0);
        packs = languages ?? [Strings.Korean];
        language.ItemsSource = packs.Select(p => p.Name).ToArray();
        language.SelectedIndex = Math.Max(0, packs.ToList().FindIndex(p => string.Equals(p.Language, current.Language, StringComparison.OrdinalIgnoreCase)));
        checkUpdates.IsChecked = current.CheckUpdates;
        var sizeLabel = new TextBlock { Text = Strings.Get("settings.size"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) };
        var pages = new (string Name, Control Content)[]
        {
            (Strings.Get("settings.sectionNotes"), Section(quickErase, pageDrag, penPressure, fingerDrawing)),
            (Strings.Get("settings.sectionSave"), Section(backup, backupItems, usageRow)),
            (Strings.Get("settings.sectionDisplay"), Section(new StackPanel { Children = { themeRow, Row(Strings.Get("settings.accent"), accentList), Row(Strings.Get("settings.language"), language) } },
                Row(Strings.Get("settings.pageNumberRow"), pageNumbers, sizeLabel, numberSize), darkPages, favorites, autoTabs)),
            (Strings.Get("settings.sectionAbout"), About(about)), // last: new categories go after (Probes find them by position)
        };
        categories.ItemsSource = pages.Select(p => p.Name).ToArray();
        categories.SelectionChanged += (_, _) => page.Content = categories.SelectedIndex >= 0 ? pages[categories.SelectedIndex].Content : null;
        categories.SelectedIndex = 0;
        var ok = new Button { Name = "OkButton", Content = Strings.Get("common.ok"), IsDefault = true, MinWidth = 84, Padding = new Thickness(10, 4) };
        ok.Click += (_, _) => { if (Accept()) Finish(true); };
        var cancel = new Button { Name = "CancelButton", Content = Strings.Get("common.cancel"), MinWidth = 84, Padding = new Thickness(10, 4) };
        cancel.Click += (_, _) => Finish(false);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(false); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } };
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        DockPanel.SetDock(categories, Dock.Left);
        root.Children.Add(categories);
        root.Children.Add(new ScrollViewer { Content = page });
        Content = root;
        built = true;
    }

    /// <summary>The categories (tests choose one by position).</summary>
    public ListBox Categories => categories;

    /// <summary>OK: switching to the original file needs its confirmation; a declined switch keeps the window open.</summary>

    public override bool Accept()
    {
        var mode = originalMode.IsChecked == true ? BackupMode.Original : BackupMode.Folder;
        if (backup.IsChecked == true && mode == BackupMode.Original && initialMode != BackupMode.Original && !confirmOriginal())
        {
            folderMode.IsChecked = true;
            return false;
        }
        int count = int.TryParse((keep.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) ? n : 10;
        Result = new(quickErase.IsChecked == true, pageDrag.IsChecked == true, new(backup.IsChecked == true,
            AppSettings.BackupIntervals[Math.Max(0, interval.SelectedIndex)], mode, folder.Text ?? "", Math.Clamp(count, AppSettings.MinBackupKeep, AppSettings.MaxBackupKeep)), Display,
            packs[Math.Max(0, language.SelectedIndex)].Language, checkUpdates.IsChecked == true, fingerDrawing.IsChecked == true, penPressure.IsChecked == true);
        return true;
    }

    private DisplayValues Display => new((ThemeChoice)themes.Single(b => b.IsChecked == true).Tag!, (AccentColor)accents.Single(b => b.IsChecked == true).Tag!, pageNumbers.IsChecked == true,
        autoTabs.IsChecked == true, darkPages.IsChecked == true, favorites.IsChecked == true, (PageNumberSize)Math.Max(0, numberSize.SelectedIndex));

    /// <summary>정보 (roadmap 1.1): name, version, license and links, the start-up check and a check now.</summary>
    private StackPanel About(AboutActions? about)
    {
        var name = new TextBlock { Text = AppInfo.DisplayName, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        name.Bind(TextBlock.FontSizeProperty, name.GetResourceObservable("FontSize.Title"));
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 6, 0, 10),
            Children = { Link("ProjectLink", Strings.Get("about.project"), UpdateCheck.ProjectPage, about), Link("NoticesLink", Strings.Get("about.notices"), UpdateCheck.NoticesPage, about) } };
        var check = new Button { Name = "UpdateCheckButton", Content = Strings.Get("about.checkNow"), Padding = new Thickness(8, 1), IsEnabled = about is not null };
        var status = new StackPanel { Orientation = Orientation.Horizontal, Children = { check, updateStatus } };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            updateStatus.Text = Strings.Get("about.checking");
            var result = await about!.Check();
            while (status.Children.Count > 2) status.Children.RemoveAt(2);
            switch (result.State)
            {
                case UpdateState.Available:
                    updateStatus.Text = Strings.Format("about.available", ("version", result.Version!));
                    status.Children.Add(Link("DownloadLink", Strings.Get("about.download"), result.Url ?? UpdateCheck.ReleasesPage, about));
                    break;
                case UpdateState.UpToDate: updateStatus.Text = Strings.Get("about.upToDate"); break;
                default: updateStatus.Text = Strings.Get("about.failed"); break;
            }
            check.IsEnabled = true;
        };
        return new StackPanel
        {
            Children =
            {
                name,
                Row(Strings.Get("about.version"), new TextBlock { Name = "VersionText", Text = UpdateCheck.CurrentVersion, VerticalAlignment = VerticalAlignment.Center }),
                Row(Strings.Get("about.license"), new TextBlock { Text = "GPL-3.0", VerticalAlignment = VerticalAlignment.Center }), // license id: not UI text (D14e)
                links, checkUpdates, status
            }
        };
    }

    private static Button Link(string name, string text, string url, AboutActions? about)
    {
        var link = new Button { Name = name, Content = text, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(6, 0, 0, 0) };
        link.Bind(ForegroundProperty, link.GetResourceObservable("Accent"));
        ToolTip.SetTip(link, url);
        link.Click += (_, _) => about?.Open(url);
        return link;
    }

    private void Preview()
    {
        if (!built || themes.Count(b => b.IsChecked == true) != 1 || accents.Count(b => b.IsChecked == true) != 1) return;
        preview?.Invoke(Display);
        ShowAccents(); // the accents have a lighter variant in the dark theme
    }

    private void ShowAccents()
    {
        if (Application.Current is not { } app) return;
        foreach (var button in accents) ((Ellipse)button.Content!).Fill = new SolidColorBrush(DesktopTheme.AccentOf(app, (AccentColor)button.Tag!));
    }

    private static StackPanel Row(string label, params Control[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4) };
        row.Children.Add(new TextBlock { Text = label, Width = 80, VerticalAlignment = VerticalAlignment.Center });
        foreach (var item in items) row.Children.Add(item);
        return row;
    }

    private static StackPanel Section(params Control[] items)
    {
        var panel = new StackPanel();
        foreach (var item in items)
        {
            if (item is not StackPanel) item.Margin = new Thickness(0, 4, 0, 8);
            panel.Children.Add(item);
        }
        return panel;
    }
}
