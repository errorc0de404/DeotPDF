using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// The editor's modal UI on Avalonia (D14d <see cref="IEditorDialogs"/>): questions, messages and the save dialog. The
/// editor asks synchronously; Avalonia's dialogs are asynchronous, so the call waits in a nested dispatcher frame (the UI
/// keeps running). Tests replace each with an override.
/// </summary>
public sealed class DesktopDialogs(Window owner) : IEditorDialogs
{
    public Func<string, AskButtons, Answer>? PromptOverride { get; set; }
    public Func<SavePathRequest, string?>? SaveDialogOverride { get; set; }
    public Action<string>? MessageSink { get; set; }

    /// <summary>A dialog is open (backups wait, spec 18.2).</summary>
    public bool IsOpen { get; private set; }

    public Answer Ask(string text, AskButtons buttons, NoticeKind kind)
    {
        if (PromptOverride is { } answer) return answer(text, buttons);
        var choices = buttons == AskButtons.YesNo
            ? new[] { (Strings.Get("dialog.yes"), Answer.Yes), (Strings.Get("dialog.no"), Answer.No) }
            : [(Strings.Get("dialog.save"), Answer.Yes), (Strings.Get("dialog.dontSave"), Answer.No), (Strings.Get("dialog.cancel"), Answer.Cancel)];
        return Wait(ShowBox(text, choices, buttons == AskButtons.YesNo ? Answer.No : Answer.Cancel));
    }

    public void Tell(string text, NoticeKind kind)
    {
        if (MessageSink is { } sink) { sink(text); return; }
        Wait(ShowBox(text, [(Strings.Get("dialog.ok"), Answer.Yes)], Answer.Yes));
    }

    public string? ChooseSavePath(SavePathRequest request)
    {
        if (SaveDialogOverride is { } choose) return choose(request);
        return Wait(SaveAsync(request));
    }

    private async Task<string?> SaveAsync(SavePathRequest request)
    {
        var options = new FilePickerSaveOptions
        {
            Title = request.Title, SuggestedFileName = request.SuggestedName, DefaultExtension = "pdf", ShowOverwritePrompt = request.OverwritePrompt,
            FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }] // file type name, not UI text (D14e)
        };
        if (request.Folder is { } folder) options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(folder);
        var file = await owner.StorageProvider.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }

    /// <summary>Asks for a PDF to open; null when cancelled.</summary>
    public async Task<string?> ChooseOpenPathAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }] // not UI text (D14e)
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<Answer> ShowBox(string text, (string Label, Answer Value)[] choices, Answer closed)
    {
        var dialog = new Window
        {
            Title = "DeotPDF", SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, MinWidth = 320, MaxWidth = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var result = closed;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new(0, 16, 0, 0) };
        foreach (var (label, value) in choices)
        {
            var button = new Button { Content = label, MinWidth = 80 };
            button.Click += (_, _) => { result = value; dialog.Close(); };
            row.Children.Add(button);
        }
        dialog.Content = new StackPanel { Margin = new(20), Children = { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, row } };
        await dialog.ShowDialog(owner);
        return result;
    }

    private T Wait<T>(Task<T> task)
    {
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
        finally { IsOpen = false; }
    }
}
