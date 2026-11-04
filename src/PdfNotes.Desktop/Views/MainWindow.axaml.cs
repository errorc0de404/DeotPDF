using Avalonia.Controls;
using Avalonia.Threading;
using PdfNotes.Desktop.Canvas;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Services;
using SkiaSharp;

namespace PdfNotes.Desktop.Views;

/// <summary>The main window (roadmap P4). A0–A1: open a document and show it; tools and text come in A3–A4.</summary>
public sealed partial class MainWindow : Window
{
    private OpenDocument? document;
    private PageRenderQueue<SKImage>? queue;

    public MainWindow()
    {
        InitializeComponent();
        Canvas.ViewChanged += (_, _) => ZoomText.Text = $"{Math.Round(Canvas.Zoom * 100)}%";
        Closed += (_, _) => CloseDocument();
    }

    public DocumentCanvas DocumentCanvas => Canvas;
    public OpenDocument? Document => document;

    /// <summary>Opens a PDF (spec 4.2). Returns false and says why in the status bar when it cannot be opened.</summary>
    public async Task<bool> OpenFileAsync(string path)
    {
        StatusText.Text = Path.GetFileName(path);
        OpenDocument opened;
        try { opened = await OpenDocument.OpenPdfAsync(path); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            StatusText.Text = e.Message;
            return false;
        }
        Show(opened);
        return true;
    }

    /// <summary>Shows an opened document (tests pass one made in memory).</summary>
    public void Show(OpenDocument opened)
    {
        CloseDocument();
        document = opened;
        queue = new PageRenderQueue<SKImage>(opened.Renderer, PageImages.FromPdfium, action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));
        Canvas.SetDocument(opened.Session.Current.Pages, queue);
        opened.Session.StateChanged += OnSessionChanged;
        Title = opened.FilePath is { } path ? $"{Path.GetFileName(path)} - DeotPDF" : "DeotPDF";
    }

    private void OnSessionChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => { if (document is { } d) Canvas.UpdatePages(d.Session.Current.Pages); });

    private void CloseDocument()
    {
        Canvas.Detach();
        queue?.Dispose();
        queue = null;
        if (document is { } d) d.Session.StateChanged -= OnSessionChanged;
        document?.Dispose();
        document = null;
    }
}
