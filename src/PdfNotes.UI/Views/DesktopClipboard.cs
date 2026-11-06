using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PdfNotes.Editor.Platform;

namespace PdfNotes.UI.Views;

/// <summary>
/// The system clipboard for the editor (roadmap P4 A5): annotation and page packets as the app's own formats (between
/// this app's windows), plain text in and out (other programs), images in (spec 10, paste from a screenshot tool).
/// Avalonia's clipboard is asynchronous and the editor asks synchronously, so each call waits in a nested dispatcher frame
/// like the dialogs. Without a platform clipboard (some test hosts) the data stays in memory.
/// </summary>
public sealed class DesktopClipboard(Avalonia.Visual owner) : IEditorClipboard
{
    private static readonly Dictionary<string, DataFormat<string>> Formats = [];
    private string? memoryFormat, memoryData, memoryText;

    /// <summary>Keep the data inside the app (tests, and a host without a clipboard).</summary>
    public bool InMemory { get; set; }

    private IClipboard? System => InMemory ? null : TopLevel.GetTopLevel(owner)?.Clipboard;

    private static DataFormat<string> Format(string name)
    {
        lock (Formats)
        {
            if (!Formats.TryGetValue(name, out var format)) Formats[name] = format = DataFormat.CreateStringApplicationFormat(name);
            return format;
        }
    }

    public bool ContainsData(string format)
    {
        if (System is not { } clipboard) return memoryFormat == format;
        var wanted = Format(format);
        return Wait(clipboard.GetDataFormatsAsync()).Any(f => f.Equals(wanted));
    }

    public string? GetData(string format) => System is { } clipboard ? Wait(clipboard.TryGetValueAsync(Format(format))) : memoryFormat == format ? memoryData : null;

    public void SetData(string format, string data)
    {
        if (System is not { } clipboard) { memoryFormat = format; memoryData = data; memoryText = null; return; }
        var item = new DataTransferItem();
        item.Set(Format(format), data);
        var transfer = new DataTransfer();
        transfer.Add(item);
        Wait(Done(clipboard.SetDataAsync(transfer)));
    }

    public bool ContainsText() => System is { } clipboard ? Wait(clipboard.GetDataFormatsAsync()).Any(f => f.Equals(DataFormat.Text)) : memoryText is not null;

    public string GetText() => (System is { } clipboard ? Wait(clipboard.TryGetTextAsync()) : memoryText) ?? "";

    public void SetText(string text)
    {
        if (System is not { } clipboard) { memoryText = text; memoryFormat = memoryData = null; return; }
        Wait(Done(clipboard.SetTextAsync(text)));
    }

    public bool ContainsImage() => System is { } clipboard && Wait(clipboard.GetDataFormatsAsync()).Any(f => f.Equals(DataFormat.Bitmap));

    public byte[]? GetImagePng()
    {
        if (System is not { } clipboard) return null;
        using var bitmap = Wait(clipboard.TryGetBitmapAsync());
        if (bitmap is null) return null;
        using var png = new MemoryStream();
        bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); // PNG
        return png.ToArray();
    }

    private static async Task<bool> Done(Task task) { await task; return true; }

    private static T Wait<T>(Task<T> task)
    {
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
        catch (Exception e) when (e is not ClipboardUnavailableException) { throw new ClipboardUnavailableException(e); }
    }
}
