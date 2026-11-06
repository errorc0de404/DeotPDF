using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.UI.Views;
using AndroidUri = Android.Net.Uri;

namespace PdfNotes.Android;

/// <summary>
/// The modal UI on Android (roadmap P5a–P5b): questions in Android's own dialog, answered asynchronously; the system's
/// document picker to open and to save; messages as toasts. Questions asked synchronously (page commands) take their
/// safe answer until the app's windows come to Android (P5c).
/// </summary>
public sealed class AndroidDialogs(Func<TopLevel?> topLevel) : IDesktopDialogs
{
    private static global::Android.Content.Context Context => global::Android.App.Application.Context;

    public bool IsOpen { get; private set; }

    public Answer Ask(string text, AskButtons buttons, NoticeKind kind)
    {
        Tell(text, kind);
        return buttons == AskButtons.YesNo ? Answer.No : Answer.Cancel;
    }

    public Task<Answer> AskAsync(string text, AskButtons buttons, NoticeKind kind)
    {
        if (MainActivity.Current is not { } activity) return Task.FromResult(Ask(text, buttons, kind));
        var answer = new TaskCompletionSource<Answer>();
        Answer closed = buttons == AskButtons.YesNo ? Answer.No : Answer.Cancel;
        var builder = new global::Android.App.AlertDialog.Builder(activity).SetMessage(text)!.SetCancelable(true)!;
        if (buttons == AskButtons.YesNo)
        {
            builder.SetPositiveButton(Strings.Get("dialog.yes"), (_, _) => answer.TrySetResult(Answer.Yes));
            builder.SetNegativeButton(Strings.Get("dialog.no"), (_, _) => answer.TrySetResult(Answer.No));
        }
        else
        {
            builder.SetPositiveButton(Strings.Get("dialog.save"), (_, _) => answer.TrySetResult(Answer.Yes));
            builder.SetNegativeButton(Strings.Get("dialog.dontSave"), (_, _) => answer.TrySetResult(Answer.No));
            builder.SetNeutralButton(Strings.Get("dialog.cancel"), (_, _) => answer.TrySetResult(Answer.Cancel));
        }
        builder.SetOnCancelListener(new CancelListener(() => answer.TrySetResult(closed)));
        IsOpen = true;
        builder.Show();
        return Finish(answer.Task);
    }

    private async Task<T> Finish<T>(Task<T> task)
    {
        try { return await task; }
        finally { IsOpen = false; }
    }

    private sealed class CancelListener(Action cancelled) : Java.Lang.Object, global::Android.Content.IDialogInterfaceOnCancelListener
    {
        public void OnCancel(global::Android.Content.IDialogInterface? dialog) => cancelled();
    }

    public void Tell(string text, NoticeKind kind) =>
        global::Android.Widget.Toast.MakeText(Context, text, global::Android.Widget.ToastLength.Long)?.Show();

    private static readonly FilePickerFileType Pdf = new("PDF") { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] }; // file type name, not UI text (D14e)

    /// <summary>The system's document picker; the grant is kept so the recent list can open it again.</summary>
    public async Task<DocumentLocation?> ChooseDocumentAsync()
    {
        if (topLevel()?.StorageProvider is not { CanOpen: true } storage) return null;
        IsOpen = true;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Strings.Get("file.openTitle"), AllowMultiple = false, FileTypeFilter = [Pdf] });
            return files is [var file, ..] ? Location(file) : null;
        }
        finally { IsOpen = false; }
    }

    /// <summary>The system's "save as" (a new PDF document in the chosen folder).</summary>
    public async Task<DocumentLocation?> ChooseSaveLocationAsync(SavePathRequest request)
    {
        if (MainActivity.Current is not { } activity) return null;
        IsOpen = true;
        try
        {
            string name = Path.GetExtension(request.SuggestedName).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? request.SuggestedName : request.SuggestedName + ".pdf";
            if (await activity.CreateDocumentAsync(name) is not { } uri) return null;
            AndroidLocations.Keep(Context, uri);
            return new ContentLocation(Context, uri);
        }
        finally { IsOpen = false; }
    }

    private static DocumentLocation? Location(IStorageFile file)
    {
        if (file.TryGetLocalPath() is { } path) return DocumentLocation.File(path);
        var uri = AndroidUri.Parse(file.Path.OriginalString)!;
        AndroidLocations.Keep(Context, uri);
        return new ContentLocation(Context, uri);
    }

    public string? ChooseSavePath(SavePathRequest request) => null;
    public bool ShowWindow(Window window) => false;
    public Task<bool> ShowWindowAsync(Window window) => Task.FromResult(false);
    public Task<bool> ShowChildAsync(Window child, Window parent) => Task.FromResult(false);
    public Task<string?> ChooseOpenPathAsync() => Task.FromResult<string?>(null);
    public string[]? ChoosePdfFiles(string title) => null;
    public Task<string[]?> ChooseImageAsync() => Task.FromResult<string[]?>(null);
}
