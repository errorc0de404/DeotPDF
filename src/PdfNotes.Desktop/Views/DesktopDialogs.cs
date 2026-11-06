using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PdfNotes.Desktop.Themes;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;

namespace PdfNotes.Desktop.Views;

/// <summary>A window of the app with an OK button: <see cref="Accept"/> checks the input and keeps the result (tests press OK through it).</summary>
public interface IAcceptDialog
{
    /// <summary>Reads the input; false (and the window stays open) when it is not valid.</summary>
    bool Accept();
}

/// <summary>
/// The main window's modal UI: the editor's questions and messages (<see cref="IEditorDialogs"/>), the file dialogs and
/// the app's own windows. Whoever makes the window gives it one (<see cref="DesktopDialogs"/> in the app; tests pass a
/// scripted one).
/// </summary>
public interface IDesktopDialogs : IEditorDialogs
{
    /// <summary>A dialog is open (backups wait, spec 18.2).</summary>
    bool IsOpen { get; }

    /// <summary>Shows one of the app's windows modally; true when it was closed with OK.</summary>
    bool ShowWindow(Window window);

    /// <summary>The same, waited for asynchronously (from async commands).</summary>
    Task<bool> ShowWindowAsync(Window window);

    /// <summary>A window opened from another app window (<paramref name="parent"/>), such as the range picker of the merge window.</summary>
    Task<bool> ShowChildAsync(Window child, Window parent);

    /// <summary>Asks for a PDF to open; null when cancelled.</summary>
    Task<string?> ChooseOpenPathAsync();

    /// <summary>PDFs to read (import, merge), or null.</summary>
    string[]? ChoosePdfFiles(string title);

    /// <summary>An image file to insert, or null.</summary>
    Task<string[]?> ChooseImageAsync();
}

/// <summary>
/// <see cref="IDesktopDialogs"/> on Avalonia. The editor asks synchronously; Avalonia's dialogs are asynchronous, so the
/// call waits in a nested dispatcher frame (the UI keeps running).
/// </summary>
public sealed class DesktopDialogs(Window owner) : IDesktopDialogs
{
    public bool IsOpen { get; private set; }

    public Answer Ask(string text, AskButtons buttons, NoticeKind kind)
    {
        var choices = buttons == AskButtons.YesNo
            ? new[] { (Strings.Get("dialog.yes"), Answer.Yes), (Strings.Get("dialog.no"), Answer.No) }
            : [(Strings.Get("dialog.save"), Answer.Yes), (Strings.Get("dialog.dontSave"), Answer.No), (Strings.Get("dialog.cancel"), Answer.Cancel)];
        return Wait(ShowBox(text, choices, buttons == AskButtons.YesNo ? Answer.No : Answer.Cancel));
    }

    public void Tell(string text, NoticeKind kind)
    {
        Wait(ShowBox(text, [(Strings.Get("dialog.ok"), Answer.Yes)], Answer.Yes));
    }

    public string? ChooseSavePath(SavePathRequest request) => Wait(SaveAsync(request));

    public bool ShowWindow(Window window) => Wait(window.ShowDialog<bool>(owner));

    public Task<bool> ShowChildAsync(Window child, Window parent) => child.ShowDialog<bool>(parent);

    public async Task<bool> ShowWindowAsync(Window window)
    {
        IsOpen = true;
        try { return await window.ShowDialog<bool>(owner); }
        finally { IsOpen = false; }
    }

    private async Task<string?> SaveAsync(SavePathRequest request)
    {
        var options = new FilePickerSaveOptions
        {
            Title = request.Title, SuggestedFileName = request.SuggestedName, DefaultExtension = "pdf", ShowOverwritePrompt = request.OverwritePrompt,
            FileTypeChoices = [Pdf]
        };
        if (request.Folder is { } folder) options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(folder);
        var file = await owner.StorageProvider.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }

    private static readonly FilePickerFileType Pdf = new("PDF") { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"], AppleUniformTypeIdentifiers = ["com.adobe.pdf"] }; // file type name, not UI text (D14e)

    /// <summary>Image types the app inserts (spec 10).</summary>
    public static readonly FilePickerFileType Images = new(Strings.Get("image.typeName"))
        { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"], MimeTypes = ["image/png", "image/jpeg", "image/bmp"], AppleUniformTypeIdentifiers = ["public.image"] }; // not UI text (D14e)

    public async Task<string?> ChooseOpenPathAsync() => (await ChooseFilesAsync(Strings.Get("file.openTitle"), [Pdf], multiple: false))?.FirstOrDefault();

    public string[]? ChoosePdfFiles(string title) => Wait(ChooseFilesAsync(title, [Pdf], multiple: true));

    public Task<string[]?> ChooseImageAsync() => ChooseFilesAsync(Strings.Get("image.insertTitle"), [Images], multiple: false);

    private async Task<string[]?> ChooseFilesAsync(string title, IReadOnlyList<FilePickerFileType> types, bool multiple)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = multiple, FileTypeFilter = types });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        return paths.Length > 0 ? paths : null;
    }

    private async Task<Answer> ShowBox(string text, (string Label, Answer Value)[] choices, Answer closed)
    {
        var dialog = new Window
        {
            Title = App.DisplayName, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, MinWidth = 320, MaxWidth = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        DialogLook.Apply(dialog);
        var result = closed;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new(0, 16, 0, 0) };
        foreach (var (label, value) in choices)
        {
            var button = new Button { Content = label, MinWidth = 80, IsDefault = ReferenceEquals(label, choices[0].Label) };
            button.Click += (_, _) => { result = value; dialog.Close(); };
            row.Children.Add(button);
        }
        dialog.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { result = closed; dialog.Close(); } };
        dialog.Content = new StackPanel { Margin = new(20), Children = { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, row } };
        IsOpen = true;
        try { await dialog.ShowDialog(owner); }
        finally { IsOpen = false; }
        return result;
    }

    private T Wait<T>(Task<T> task)
    {
        bool wasOpen = IsOpen;
        IsOpen = true;
        try
        {
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                Dispatcher.UIThread.PushFrame(frame);
            }
            return task.GetAwaiter().GetResult();
        }
        finally { IsOpen = wasOpen; }
    }
}
