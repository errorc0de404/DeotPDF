using PdfNotes.Editor.Platform;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// The editor's clipboard inside the app (roadmap P4 A3): annotation copies paste within and between this app's windows.
/// The system clipboard (other apps, images, PDF text out) comes with A5, through Avalonia's asynchronous clipboard.
/// </summary>
public sealed class MemoryClipboard : IEditorClipboard
{
    private static readonly Dictionary<string, string> Data = [];
    private static string? text;

    public bool ContainsData(string format) { lock (Data) return Data.ContainsKey(format); }
    public string? GetData(string format) { lock (Data) return Data.GetValueOrDefault(format); }
    public void SetData(string format, string data) { lock (Data) { Data.Clear(); Data[format] = data; text = null; } }
    public bool ContainsText() { lock (Data) return text is not null; }
    public string GetText() { lock (Data) return text ?? ""; }
    public void SetText(string value) { lock (Data) { Data.Clear(); text = value; } }
    public bool ContainsImage() => false;
    public byte[]? GetImagePng() => null;
}
