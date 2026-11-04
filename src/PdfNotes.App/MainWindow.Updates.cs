using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace PdfNotes.App;

/// <summary>Checking for a new version (roadmap 1.1): the state and the check are <see cref="UpdateViewModel"/>; the window shows the status bar button.</summary>
public partial class MainWindow
{
    /// <summary>The start-up part, run by <see cref="App"/> after the window is shown (validation runs make windows themselves and skip it).</summary>
    public Task CheckForUpdatesAtStartAsync() => viewModel.Update.CheckAtStartAsync();

    private void InitializeUpdates() => viewModel.Update.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(UpdateViewModel.IsAvailable)) UpdateButton.Visibility = viewModel.Update.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
    };

    private void UpdateClick(object sender, RoutedEventArgs e) => OpenWebPage(viewModel.Update.Url);

    private static void OpenWebPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
    }
}
