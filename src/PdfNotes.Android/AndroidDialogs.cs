using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.UI.Views;
using AndroidUri = Android.Net.Uri;

namespace PdfNotes.Android;

/// <summary>
/// The modal UI on Android (roadmap P5a–P5c): questions in Android's own dialog, answered asynchronously; the app's
/// windows on cards inside its view (<see cref="MainView.ShowInViewAsync"/>); the system's document picker to open, to
/// save and to choose PDFs and images; messages as toasts. A question asked synchronously takes its safe answer.
/// </summary>
public sealed class AndroidDialogs(Func<MainView?> view) : IDesktopDialogs
{
    private static global::Android.Content.Context Context => global::Android.App.Application.Context;

    public bool IsOpen { get; private set; }

    private TopLevel? TopLevel => view() is { } shown ? Avalonia.Controls.TopLevel.GetTopLevel(shown) : null;

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
        if (TopLevel?.StorageProvider is not { CanOpen: true } storage) return null;
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

    /// <summary>A window cannot be waited for here: the app asks asynchronously (<see cref="ShowWindowAsync"/>).</summary>
    public bool ShowWindow(DialogView dialog) => false;

    public Task<bool> ShowWindowAsync(DialogView dialog) => view()?.ShowInViewAsync(dialog) ?? Task.FromResult(false);

    /// <summary>A window opened from another goes on the card above it.</summary>
    public Task<bool> ShowChildAsync(DialogView child, DialogView parent) => ShowWindowAsync(child);

    public Task<string?> ChooseOpenPathAsync() => Task.FromResult<string?>(null);
    public string[]? ChoosePdfFiles(string title) => null;

    /// <summary>PDFs to import or merge: picked documents are copied into the app's cache, which the import reads.</summary>
    public Task<string[]?> ChoosePdfFilesAsync(string title) => ChooseCopiesAsync(title, [Pdf], multiple: true);

    private static readonly FilePickerFileType Images = new("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg"], MimeTypes = ["image/png", "image/jpeg"] }; // file type name, not UI text (D14e)

    public Task<string[]?> ChooseImageAsync() => ChooseCopiesAsync(Strings.Get("image.insertTitle"), [Images], multiple: false);

    private async Task<string[]?> ChooseCopiesAsync(string title, IReadOnlyList<FilePickerFileType> types, bool multiple)
    {
        if (TopLevel?.StorageProvider is not { CanOpen: true } storage) return null;
        IsOpen = true;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = multiple, FileTypeFilter = types });
            if (files.Count == 0) return null;
            string folder = Path.Combine(Context.CacheDir!.AbsolutePath, "picked", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var paths = new List<string>();
            foreach (var file in files)
            {
                string name = string.Concat(file.Name.Split(Path.GetInvalidFileNameChars()));
                string path = Path.Combine(folder, name.Length > 0 ? name : "document");
                await using (var input = await file.OpenReadAsync())
                await using (var output = File.Create(path))
                    await input.CopyToAsync(output);
                paths.Add(path);
            }
            return [.. paths];
        }
        finally { IsOpen = false; }
    }
}
