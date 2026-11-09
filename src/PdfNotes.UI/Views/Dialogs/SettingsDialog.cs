using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PdfNotes.Editor.Localization;
using PdfNotes.ViewModels;

namespace PdfNotes.UI.Views;

/// <summary>
/// The settings window (spec 18.1): <see cref="SettingsDialogViewModel"/> holds the values and commands; this view asks
/// the platform for a folder and shows one.
/// </summary>
public sealed partial class SettingsDialog : DialogView
{
    private readonly SettingsDialogViewModel model;

    public SettingsDialog(SettingsValues current, Func<Task<bool>> confirmOriginal, Action<DisplayValues>? preview = null, IReadOnlyList<LanguagePack>? languages = null,
        BackupActions? backups = null, AboutActions? about = null)
    {
        model = new(current, Strings.Current, confirmOriginal, preview, languages, backups, about, new(ChooseFolderAsync, OpenFolderAsync));
        DataContext = model;
        Title = Strings.Get("command.settings");
        PreferredSize = (600, 420, 600, 420);
        Resizable = false;
        AvaloniaXamlLoader.Load(this);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(false); };
    }

    public SettingsDialogViewModel Model => model;

    public SettingsValues? Result => model.Result;

    /// <summary>The categories (tests choose one by position).</summary>
    public ListBox Categories => this.FindControl<ListBox>("CategoryList")!;

    public override bool Accept() => model.Accept(() => Finish(true));

    private void OkClick(object? sender, RoutedEventArgs e) { if (Accept()) Finish(true); }

    private void CancelClick(object? sender, RoutedEventArgs e) => Finish(false);

    private async Task<string?> ChooseFolderAsync(string current)
    {
        if (StorageProvider is not { } storage) return null;
        var start = Directory.Exists(current) ? await storage.TryGetFolderFromPathAsync(current) : null;
        var chosen = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.Get("settings.backupFolder"), SuggestedStartLocation = start });
        return chosen.Count > 0 ? chosen[0].TryGetLocalPath() : null;
    }

    private async Task OpenFolderAsync(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            if (Launcher is { } launcher) await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
