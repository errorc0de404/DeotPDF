using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;

namespace PdfNotes.ViewModels;

/// <summary>
/// Checking for a new version (roadmap 1.1, <see cref="UpdateCheck"/>; P1 V2). A release build asks once at its first
/// start whether to check at start; then it checks at most once a day and, when there is a newer release, shows it in
/// the status bar. Settings › 정보 turns it on or off and checks at once. Nothing is downloaded or installed.
/// </summary>
public sealed class UpdateViewModel(AppSettings settings, Action save, IEditorDialogs dialogs, TimeProvider time, Func<Task<UpdateResult>> check) : ObservableObject
{
    private string? availableText;
    private string? url;

    /// <summary>The status bar text of a newer release, or null.</summary>
    public string? AvailableText { get => availableText; private set { if (SetProperty(ref availableText, value)) OnPropertyChanged(nameof(IsAvailable)); } }

    public bool IsAvailable => availableText is not null;

    /// <summary>The page the status bar button opens.</summary>
    public string Url => url ?? UpdateCheck.ReleasesPage;

    /// <summary>The start-up part (the app runs it after the window is shown; validation windows skip it). Development versions never check.</summary>
    public async Task CheckAtStartAsync(string? version = null)
    {
        version ??= UpdateCheck.CurrentVersion;
        if (UpdateCheck.IsDevelopment(version)) return;
        if (settings.CheckUpdates is null)
        {
            settings.CheckUpdates = dialogs.Ask(Strings.Get("update.ask"), AskButtons.YesNo, NoticeKind.Question) == Answer.Yes;
            save();
        }
        if (UpdateCheck.Due(settings.CheckUpdates, settings.UpdateCheckedAt, time.GetUtcNow().UtcDateTime, version)) await CheckAsync();
    }

    /// <summary>Asks GitHub now; a newer release shows in the status bar. Used at start and by Settings › 정보.</summary>
    public async Task<UpdateResult> CheckAsync()
    {
        var result = await check();
        if (result.State != UpdateState.Failed)
        {
            settings.UpdateCheckedAt = time.GetUtcNow().UtcDateTime;
            save();
        }
        if (result.State == UpdateState.Available)
        {
            url = result.Url;
            OnPropertyChanged(nameof(Url));
            AvailableText = Strings.Format("about.available", ("version", result.Version!));
        }
        return result;
    }
}
