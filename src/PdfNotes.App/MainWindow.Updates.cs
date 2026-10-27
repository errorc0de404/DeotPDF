using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

/// <summary>
/// Checking for a new version (roadmap 1.1, <see cref="UpdateCheck"/>). A release build asks once at its first start
/// whether to check at start; then it checks at most once a day in the background and, when there is a newer release,
/// shows it in the status bar (opens the release page). Settings › 정보 turns it on or off and checks at once. Nothing is
/// downloaded or installed.
/// </summary>
public partial class MainWindow
{
    private string? updateUrl;

    /// <summary>The start-up part, run by <see cref="App"/> after the window is shown (validation runs make windows themselves and skip it).</summary>
    public async Task CheckForUpdatesAtStartAsync()
    {
        string version = UpdateCheck.CurrentVersion;
        if (UpdateCheck.IsDevelopment(version)) return;
        if (settings.CheckUpdates is null)
        {
            settings.CheckUpdates = Prompt(Strings.Get("update.ask"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            settings.Save();
        }
        if (UpdateCheck.Due(settings.CheckUpdates, settings.UpdateCheckedAt, DateTime.UtcNow, version)) await CheckForUpdatesAsync();
    }

    /// <summary>Asks GitHub now; a newer release shows in the status bar. Used at start and by Settings › 정보.</summary>
    private async Task<UpdateResult> CheckForUpdatesAsync()
    {
        var result = await UpdateCheck.CheckAsync();
        if (result.State != UpdateState.Failed)
        {
            settings.UpdateCheckedAt = DateTime.UtcNow;
            settings.Save();
        }
        if (result.State == UpdateState.Available)
        {
            updateUrl = result.Url ?? UpdateCheck.ReleasesPage;
            UpdateText.Text = Strings.Format("about.available", ("version", result.Version!));
            UpdateButton.Visibility = Visibility.Visible;
        }
        return result;
    }

    private void UpdateClick(object sender, RoutedEventArgs e) => OpenWebPage(updateUrl ?? UpdateCheck.ReleasesPage);

    private static void OpenWebPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
    }
}
