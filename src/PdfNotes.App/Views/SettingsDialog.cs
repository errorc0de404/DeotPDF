using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfNotes.App.Services;
using PdfNotes.App.Themes;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App.Views;

/// <summary>Automatic backup values of the settings window (spec 18.2).</summary>
public sealed record BackupValues(bool Enabled, int IntervalMinutes, BackupMode Mode, string Folder, int Keep);

/// <summary>
/// 화면 values of the settings window (spec 18.1, D13d): theme, accent color, page numbers (4.5), automatic 필기 ↔ 페이지
/// tab switch (3.2), reading dark page (24.12, off) and the pen favorites group (24.7, on).
/// </summary>
public sealed record DisplayValues(ThemeChoice Theme, AccentColor Accent, bool PageNumbers, bool AutoSwitchTabs = true, bool DarkPages = false, bool Favorites = true,
    PageNumberSize NumberSize = PageNumberSize.Normal)
{
    public static DisplayValues Default { get; } = new(ThemeChoice.System, AccentColor.Blue, true);
}

/// <summary>Values of the settings window (spec 18.1).</summary>
/// <param name="PageDrag">필기 › 페이지 사이 끌어 옮기기 (spec 5.3, D14g), off by default.</param>
/// <param name="Language">화면 › 언어 (D14e): the language pack, applied at the next start.</param>
public sealed record SettingsValues(bool QuickErase, bool PageDrag, BackupValues Backup, DisplayValues? Display = null, string Language = Strings.BuiltInLanguage);

/// <summary>What 저장 shows and does about the backups the app made (spec 18.2, D14g): their count and size, and 백업 비우기.</summary>
/// <param name="Clear">Asks for confirmation and deletes them; true when it deleted.</param>
public sealed record BackupActions(Func<(int Count, long Bytes)> Usage, Func<bool> Clear);

/// <summary>
/// Settings window (spec 18.1): categories on the left, their items on the right. OK applies, Cancel discards.
/// Choosing to back up into the original file asks once for confirmation (<paramref name="confirmOriginal"/>).
/// 화면 choices show at once through <paramref name="preview"/> (theme, accent, page numbers); the caller undoes the
/// preview when the window is cancelled.
/// </summary>
public sealed class SettingsDialog : Window
{
    private readonly CheckBox quickErase = new() { Name = "QuickEraseBox", Content = Strings.Get("settings.quickErase") };
    private readonly CheckBox pageDrag = new() { Name = "PageDragBox", Content = Strings.Get("settings.pageDrag") };
    private readonly ComboBox numberSize = new() { Name = "PageNumberSizeBox", Width = 80, Items = { Strings.Get("settings.small"), Strings.Get("settings.normal"), Strings.Get("settings.large") } };
    private readonly CheckBox darkPages = new() { Name = "DarkPagesBox", Content = Strings.Get("settings.darkPages") };
    private readonly CheckBox favorites = new() { Name = "FavoritesBox", Content = Strings.Get("settings.favorites") };
    private readonly CheckBox backup = new() { Name = "AutoBackupBox", Content = Strings.Get("settings.autoBackup") };
    private readonly ComboBox interval = new() { Name = "BackupIntervalBox", Width = 80 };
    private readonly RadioButton folderMode = new() { Name = "BackupFolderMode", Content = Strings.Get("settings.backupToFolder"), GroupName = "BackupMode" };
    private readonly RadioButton originalMode = new() { Name = "BackupOriginalMode", Content = Strings.Get("settings.backupInPlace"), GroupName = "BackupMode" };
    private readonly TextBox folder = new() { Name = "BackupFolderBox", Width = 230, IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox keep = new() { Name = "BackupKeepBox", Width = 44, VerticalContentAlignment = VerticalAlignment.Center };
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
    private readonly CheckBox autoTabs = new() { Name = "AutoTabsBox", Content = Strings.Get("settings.autoTabs"), Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox language = new() { Name = "LanguageBox", MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly BackupMode initialMode;
    private readonly Func<bool> confirmOriginal;
    private readonly Action<DisplayValues>? preview;

    public SettingsValues? Result { get; private set; }

    private static readonly (AccentColor Color, string Name)[] AccentNames =
        [(AccentColor.Blue, Strings.Get("color.blue")), (AccentColor.Green, Strings.Get("color.green")), (AccentColor.Purple, Strings.Get("color.purple")), (AccentColor.Orange, Strings.Get("color.orange")), (AccentColor.Red, Strings.Get("color.red")), (AccentColor.Gray, Strings.Get("color.gray"))];

    public SettingsDialog(SettingsValues current, Func<bool> confirmOriginal, Action<DisplayValues>? preview = null, IReadOnlyList<LanguagePack>? languages = null,
        BackupActions? backups = null)
    {
        Theme.Attach(this);
        this.confirmOriginal = confirmOriginal;
        this.preview = preview;
        initialMode = current.Backup.Mode;
        Title = Strings.Get("command.settings");
        Width = 560; Height = 400; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme.UseDialogTokens(this);
        categories.SetResourceReference(Control.BorderBrushProperty, "Divider");
        quickErase.IsChecked = current.QuickErase;
        pageDrag.IsChecked = current.PageDrag;
        backup.IsChecked = current.Backup.Enabled;
        foreach (int minutes in AppSettings.BackupIntervals) interval.Items.Add(new ComboBoxItem { Content = Strings.Format("settings.minutes", ("minutes", minutes)), Tag = minutes });
        interval.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.BackupIntervals, current.Backup.IntervalMinutes));
        (current.Backup.Mode == BackupMode.Original ? originalMode : folderMode).IsChecked = true;
        folder.Text = current.Backup.Folder;
        keep.Text = current.Backup.Keep.ToString(CultureInfo.CurrentCulture);

        var change = new Button { Name = "BackupFolderChange", Content = Strings.Get("settings.change"), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
        change.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Strings.Get("settings.backupFolder"), InitialDirectory = Directory.Exists(folder.Text) ? folder.Text : "" };
            if (dialog.ShowDialog(this) == true) folder.Text = dialog.FolderName;
        };
        var open = new Button { Name = "BackupFolderOpen", Content = Strings.Get("settings.openFolder"), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
        open.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(folder.Text);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder.Text}\"") { UseShellExecute = true });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        };
        var backupItems = new StackPanel { Margin = new Thickness(22, 0, 0, 0) };
        backupItems.Children.Add(Row(Strings.Get("settings.interval"), interval));
        backupItems.Children.Add(Row(Strings.Get("settings.mode"), new StackPanel { Children = { folderMode, originalMode } }));
        backupItems.Children.Add(Row(Strings.Get("settings.backupFolder"), folder, change, open));
        backupItems.Children.Add(Row(Strings.Get("settings.keep"), keep, new TextBlock { Text = Strings.Get("settings.keepUnit"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) }));
        backupItems.IsEnabled = backup.IsChecked == true;
        // The backups already written stay manageable when automatic backup is off (D14g).
        var clear = new Button { Name = "BackupClear", Content = Strings.Get("settings.clearBackups"), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(10, 0, 0, 0) };
        void ShowUsage()
        {
            var (count, bytes) = backups?.Usage() ?? (0, 0);
            usage.Text = Strings.Format("settings.backupUsage", ("count", count), ("size", Size(bytes)));
            clear.IsEnabled = count > 0;
        }
        clear.Click += (_, _) => { if (backups?.Clear() == true) ShowUsage(); };
        ShowUsage();
        var usageRow = Row(Strings.Get("settings.backupUsageLabel"), usage, clear);
        backup.Checked += (_, _) => backupItems.IsEnabled = true;
        backup.Unchecked += (_, _) => backupItems.IsEnabled = false;

        // 화면 (D13d): every choice shows at once.
        var display = current.Display ?? DisplayValues.Default;
        accents = AccentNames.Select(a => new RadioButton
        {
            Name = "Accent" + a.Color, GroupName = "Accent", Tag = a.Color, ToolTip = a.Name, Style = (Style)FindResource("ToolButton"), Padding = new Thickness(5),
            Content = new System.Windows.Shapes.Ellipse { Width = 16, Height = 16 }
        }).ToArray();
        foreach (var button in themes) button.IsChecked = (ThemeChoice)button.Tag == display.Theme;
        foreach (var button in accents) button.IsChecked = (AccentColor)button.Tag == display.Accent;
        pageNumbers.IsChecked = display.PageNumbers;
        autoTabs.IsChecked = display.AutoSwitchTabs;
        darkPages.IsChecked = display.DarkPages;
        numberSize.SelectedIndex = (int)display.NumberSize;
        numberSize.SelectionChanged += (_, _) => Preview();
        favorites.IsChecked = display.Favorites;
        foreach (var box in new[] { darkPages, favorites }) { box.Checked += (_, _) => Preview(); box.Unchecked += (_, _) => Preview(); }
        ShowAccents();
        foreach (var button in themes.Concat(accents)) button.Checked += (_, _) => Preview();
        pageNumbers.Checked += (_, _) => Preview();
        pageNumbers.Unchecked += (_, _) => Preview();
        var themeList = new StackPanel();
        foreach (var button in themes) { button.Margin = new Thickness(0, 2, 0, 2); themeList.Children.Add(button); }
        var accentList = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in accents) accentList.Children.Add(button);
        var displayItems = new StackPanel();
        var themeRow = Row(Strings.Get("settings.theme"), themeList);
        themeRow.Children[0].SetValue(VerticalAlignmentProperty, VerticalAlignment.Top); // beside the first choice
        ((TextBlock)themeRow.Children[0]).Margin = new Thickness(0, 2, 0, 0);
        displayItems.Children.Add(themeRow);
        displayItems.Children.Add(Row(Strings.Get("settings.accent"), accentList));
        foreach (var pack in languages ?? [Strings.Korean]) language.Items.Add(new ComboBoxItem { Content = pack.Name, Tag = pack.Language });
        language.SelectedItem = language.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, current.Language, StringComparison.OrdinalIgnoreCase))
            ?? language.Items[0];
        displayItems.Children.Add(Row(Strings.Get("settings.language"), language));

        var pages = new (string Name, UIElement Content)[]
        {
            (Strings.Get("settings.sectionNotes"), Section(quickErase, pageDrag)),
            (Strings.Get("settings.sectionSave"), Section(backup, backupItems, usageRow)),
            (Strings.Get("settings.sectionDisplay"), Section(displayItems, Row(Strings.Get("settings.pageNumberRow"), pageNumbers, Spaced(new TextBlock { Text = Strings.Get("settings.size"), VerticalAlignment = VerticalAlignment.Center }), numberSize),
                darkPages, favorites, autoTabs)),
        };
        foreach (var (name, _) in pages) categories.Items.Add(new ListBoxItem { Content = name, Padding = new Thickness(10, 6, 10, 6) });
        categories.SelectionChanged += (_, _) => page.Content = categories.SelectedIndex >= 0 ? pages[categories.SelectedIndex].Content : null;
        categories.SelectedIndex = 0;

        var ok = new Button { Name = "OkButton", Content = Strings.Get("common.ok"), IsDefault = true, MinWidth = 84, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Name = "CancelButton", Content = Strings.Get("common.cancel"), IsCancel = true, MinWidth = 84, Padding = new Thickness(10, 4, 10, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        DockPanel.SetDock(categories, Dock.Left);
        root.Children.Add(categories);
        root.Children.Add(page);
        Content = root;
    }

    /// <summary>OK: switching to the original file needs its confirmation; a declined switch keeps the window open.</summary>
    private void Accept()
    {
        var mode = originalMode.IsChecked == true ? BackupMode.Original : BackupMode.Folder;
        if (backup.IsChecked == true && mode == BackupMode.Original && initialMode != BackupMode.Original && !confirmOriginal())
        {
            folderMode.IsChecked = true;
            return;
        }
        int count = int.TryParse(keep.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) ? n : 10;
        Result = new(quickErase.IsChecked == true, pageDrag.IsChecked == true, new(backup.IsChecked == true,
            (int)((ComboBoxItem)interval.SelectedItem).Tag, mode, folder.Text, Math.Clamp(count, AppSettings.MinBackupKeep, AppSettings.MaxBackupKeep)), Display,
            (string)((ComboBoxItem)language.SelectedItem).Tag);
        DialogResult = true;
    }

    private DisplayValues Display => new((ThemeChoice)themes.Single(b => b.IsChecked == true).Tag, (AccentColor)accents.Single(b => b.IsChecked == true).Tag, pageNumbers.IsChecked == true,
        autoTabs.IsChecked == true, darkPages.IsChecked == true, favorites.IsChecked == true,
        (PageNumberSize)Math.Max(0, numberSize.SelectedIndex));

    private static FrameworkElement Spaced(FrameworkElement e) { e.Margin = new Thickness(14, 0, 6, 0); return e; }

    /// <summary>A file size for people: KB below one MB, MB with one decimal, GB with one decimal.</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0} KB", Math.Ceiling(bytes / 1024.0)), // unit symbols: not UI text (D14e)
        < 1024L * 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.0} MB", bytes / (1024.0 * 1024)), // unit symbols: not UI text (D14e)
        _ => string.Format(CultureInfo.CurrentCulture, "{0:0.0} GB", bytes / (1024.0 * 1024 * 1024)) // unit symbols: not UI text (D14e)
    };

    private void Preview()
    {
        if (!IsInitialized || themes.Count(b => b.IsChecked == true) != 1 || accents.Count(b => b.IsChecked == true) != 1) return;
        preview?.Invoke(Display);
        ShowAccents(); // the accents have a lighter variant in the dark theme
    }

    private void ShowAccents()
    {
        foreach (var button in accents)
        {
            var dot = (System.Windows.Shapes.Ellipse)button.Content;
            dot.Fill = new SolidColorBrush(Theme.AccentOf((AccentColor)button.Tag));
        }
    }

    private static StackPanel Row(string label, params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock { Text = label, Width = 72, VerticalAlignment = VerticalAlignment.Center });
        foreach (var item in items) row.Children.Add(item);
        return row;
    }

    private static StackPanel Section(params UIElement[] items)
    {
        var panel = new StackPanel();
        foreach (var item in items)
        {
            if (item is FrameworkElement e && e is not StackPanel) e.Margin = new Thickness(0, 4, 0, 8);
            panel.Children.Add(item);
        }
        return panel;
    }
}
