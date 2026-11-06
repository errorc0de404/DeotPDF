using Avalonia.Controls;
using PdfNotes.Editor.Platform;
using PdfNotes.UI.Views;

namespace PdfNotes.Android;

/// <summary>
/// The modal UI on Android, for now (P5a): a message is a toast; a question takes its safe answer (no, cancel) and nothing
/// is chosen. The app's windows and real questions come with P5b–P5c.
/// </summary>
public sealed class AndroidDialogs : IDesktopDialogs
{
    public bool IsOpen => false;

    public Answer Ask(string text, AskButtons buttons, NoticeKind kind)
    {
        Tell(text, kind);
        return buttons == AskButtons.YesNo ? Answer.No : Answer.Cancel;
    }

    public void Tell(string text, NoticeKind kind) =>
        global::Android.Widget.Toast.MakeText(global::Android.App.Application.Context, text, global::Android.Widget.ToastLength.Long)?.Show();

    public string? ChooseSavePath(SavePathRequest request) => null;
    public bool ShowWindow(Window window) => false;
    public Task<bool> ShowWindowAsync(Window window) => Task.FromResult(false);
    public Task<bool> ShowChildAsync(Window child, Window parent) => Task.FromResult(false);
    public Task<string?> ChooseOpenPathAsync() => Task.FromResult<string?>(null);
    public string[]? ChoosePdfFiles(string title) => null;
    public Task<string[]?> ChooseImageAsync() => Task.FromResult<string[]?>(null);
}
