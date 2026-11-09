using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Services;
using PdfNotes.ViewModels;

namespace PdfNotes.Tests.App;

/// <summary>Review 2026-11-08 D6: the settings window's values and commands without a window.</summary>
public sealed class SettingsDialogViewModelTests
{
    private static SettingsValues Current(BackupMode mode = BackupMode.Folder) =>
        new(QuickErase: true, PageDrag: false, new(true, 5, mode, "C:/backups", 10), DisplayValues.Default);

    [Fact]
    public void OkGivesTheValuesAndAPreviewFollowsEachDisplayChoice()
    {
        var previews = new List<DisplayValues>();
        var model = new SettingsDialogViewModel(Current(), Texts.Korean, () => Task.FromResult(true), previews.Add);
        Assert.Empty(previews); // opening shows nothing new
        model.ThemeDark = true;
        model.Accents.Single(a => a.Color == AccentColor.Green).IsChecked = true;
        Assert.Single(model.Accents, a => a.IsChecked); // one accent at a time
        Assert.Equal((ThemeChoice.Dark, AccentColor.Green), (previews[^1].Theme, previews[^1].Accent));
        model.PageDrag = true;
        model.Keep = "999";
        Assert.True(model.Accept(() => { }));
        Assert.True(model.Result!.PageDrag);
        Assert.Equal(AppSettings.MaxBackupKeep, model.Result.Backup.Keep);
        Assert.Equal(AccentColor.Green, model.Result.Display!.Accent);
    }

    /// <summary>On Android the question about the original file is answered later; OK goes on then (B12).</summary>
    [Fact]
    public async Task BackingUpIntoTheOriginalWaitsForItsAnswer()
    {
        var answer = new TaskCompletionSource<bool>();
        var model = new SettingsDialogViewModel(Current(), Texts.Korean, () => answer.Task);
        model.BackupToOriginal = true;
        Assert.False(model.BackupToFolder);
        int finished = 0;
        Assert.False(model.Accept(() => finished++)); // asked, not answered yet
        answer.SetResult(true);
        await Task.Yield();
        Assert.Equal(1, finished);
        Assert.Equal(BackupMode.Original, model.Result!.Backup.Mode);

        var declined = new SettingsDialogViewModel(Current(), Texts.Korean, () => Task.FromResult(false)) { BackupToOriginal = true };
        Assert.False(declined.Accept(() => throw new InvalidOperationException("not closed")));
        Assert.True(declined.BackupToFolder); // back to the folder, the window stays
    }

    [Fact]
    public async Task ClearingBackupsUpdatesTheUsage()
    {
        int count = 2;
        var model = new SettingsDialogViewModel(Current(), Texts.Korean, () => Task.FromResult(true),
            backups: new(() => (count, 2048), () => { count = 0; return Task.FromResult(true); }));
        Assert.True(model.ClearCommand.CanExecute());
        await model.ClearCommand.RunAsync();
        Assert.False(model.ClearCommand.CanExecute());
        Assert.Contains("0", model.Usage);
    }
}
