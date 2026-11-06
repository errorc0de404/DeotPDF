using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;
using PdfNotes.Render.Skia;
using PdfNotes.ViewModels;
using SkiaSharp;
using Page = PdfNotes.Core.Models.Page;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// Sticky note cards (spec 24.9), hiding the notes (24.10), the pen favorites (24.7) and images (spec 10, 24.11): dropped,
/// inserted from a file or pasted. None of them changes how a document is saved.
/// </summary>
public sealed partial class MainWindow
{
    private NoteCardViewModel noteState => viewModel.NoteCard;

    public StickyNote? OpenNoteAnnotation => noteState.Note;
    public TextBox? NoteTextBox => noteState.IsOpen ? NoteText : null;

    private void InitializeNotes()
    {
        Canvas.ViewChanged += (_, _) => PositionNoteCard();
        // A click on a sticky note with the selection tool opens it (spec 24.9); a drag only moved it.
        selectTool.Released += (_, changed) =>
        {
            if (!changed && selectTool.SelectedAnnotations is [StickyNote note] && selectTool.PageId is { } notePage)
                Dispatcher.UIThread.Post(() => OpenNote(note, notePage, isNew: false));
        };
        // The card (bound to NoteCardViewModel, R4e) closes with Esc or when the keyboard leaves it.
        NoteText.KeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseNote(); Canvas.Focus(); e.Handled = true; } };
        NoteText.LostFocus += (_, _) => Dispatcher.UIThread.Post(() => { if (noteState.IsOpen && !NoteCard.IsKeyboardFocusWithin) CloseNote(); });
    }

    // ----- sticky note cards -----

    /// <summary>The note tool's click: an icon centered on the point, opened for typing (not in the document yet).</summary>
    private void CreateNote(int pageIndex, Point2 point)
    {
        if (noteState.Create(pageIndex, point) is { } note && Document is { } doc) OpenNote(note, doc.Session.Current.Pages[pageIndex].Id, isNew: true);
    }

    public void OpenNote(StickyNote note, Guid pageId, bool isNew)
    {
        CloseNote();
        if (!noteState.Open(note, pageId, isNew)) return;
        NoteCard.UpdateLayout();
        PositionNoteCard();
        Canvas.RefreshOverlays(); // a new note's icon is drawn while it is open
        NoteText.Focus();
        NoteText.CaretIndex = NoteText.Text?.Length ?? 0;
    }

    /// <summary>Keeps the card beside its icon (right of it, inside the document area) while the view scrolls or zooms.</summary>
    private void PositionNoteCard()
    {
        if (noteState.Note is not { } open || noteState.PageIndex is not (>= 0 and var index)) return;
        var corner = Canvas.PageTransform(index).PageToDip.Transform(new Point2(open.Transform.OffsetX + StickyNote.IconPt, open.Transform.OffsetY));
        double x = Math.Clamp(corner.X + 6, 0, Math.Max(0, NoteLayer.Bounds.Width - NoteCard.Width));
        double y = Math.Clamp(corner.Y, 0, Math.Max(0, NoteLayer.Bounds.Height - 100));
        Avalonia.Controls.Canvas.SetLeft(NoteCard, x);
        Avalonia.Controls.Canvas.SetTop(NoteCard, y);
    }

    /// <summary>Commits the open note: a new one with text is added, an existing one updated or deleted when empty.</summary>
    public void CloseNote() => noteState.Commit();

    // ----- hide the notes (spec 24.10) -----

    public bool NotesHidden => viewModel.Display.NotesHidden;

    /// <summary>
    /// Hides or shows the notes. While hidden the selection tool selects no annotation and PDF text can still be selected,
    /// copied and searched; choosing a drawing tool, pasting or inserting shows them again (spec 24.10, D14g). A view
    /// change only: no Undo, no unsaved mark.
    /// </summary>
    public void SetNotesHidden(bool hidden)
    {
        if (NotesHidden == hidden) return;
        FinishTextEditing();
        CloseNote();
        Canvas.CancelTool();
        viewModel.Display.NotesHidden = hidden;
        if (hidden) { SetTool(ToolKind.Select); selectTool.Select(null); }
        selectTool.IgnoreAnnotations = hidden;
        styles.Refresh();
        Canvas.RefreshOverlays();
        _ = pages.InvalidateThumbnailsAsync();
    }

    // ----- pen favorites (spec 24.7) -----

    public IReadOnlyList<AppSettings.FavoriteSettings?> Favorites => viewModel.Favorites.Slots;

    /// <summary>A favorite button or Ctrl+1–4 (spec 24.7): see <see cref="ToolBarViewModel.ApplyFavorite"/>.</summary>
    public void ApplyFavorite(int slot) => viewModel.ToolBar.ApplyFavorite(slot);

    public void StoreFavorite(int slot) => viewModel.ToolBar.StoreFavorite(slot);

    // ----- images (spec 10) -----

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    private static bool IsImageFile(string path) => ImageExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private (ImageAnnotation Image, Resource Resource) PlacedImage(byte[] bytes, Page page, Point2? center)
    {
        var resource = ImageResources.Create(bytes);
        var (w, h, dpi) = ImageFiles.Check(bytes); // decodes whole, 64 M pixels at most, the file's DPI (the WPF ImageAssets)
        return (ImagePlacement.Place(resource, w, h, dpi, page, center), resource);
    }

    /// <summary>Inserts an image fitted into 80 % of the page, centered on <paramref name="center"/> (null: the page middle).</summary>
    public bool InsertImage(byte[] bytes, int? pageIndex = null, Point2? center = null) => InsertImages([bytes], pageIndex, center);

    /// <summary>
    /// Inserts images (files dropped together: 10 pt apart), each fitted into 80 % of the page, as one Undo unit; they end
    /// up selected. Outside every page they go to the current page's middle.
    /// </summary>
    public bool InsertImages(IReadOnlyList<byte[]> files, int? pageIndex = null, Point2? center = null)
    {
        if (Document is not { } doc || files.Count == 0) return false;
        if (NotesHidden) SetNotesHidden(false); // inserting images shows the notes (spec 24.10, D14g)
        var page = doc.Session.Current.Pages[pageIndex ?? Canvas.CurrentPageIndex];
        var items = new List<(Annotation, Resource?)>();
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                Point2? at = center is { } c ? new Point2(c.X + i * 10, c.Y + i * 10) : files.Count > 1 ? new Point2(page.Setup.WidthPt / 2 + i * 10, page.Setup.HeightPt / 2 + i * 10) : null;
                var (image, resource) = PlacedImage(files[i], page, at);
                items.Add((image, resource));
            }
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException or IOException)
        {
            Tell(Strings.Get("image.insertFailed"), NoticeKind.Warning);
            return false;
        }
        Canvas.CancelTool();
        doc.Session.Execute(DocumentEdits.AddAnnotations(doc.Session.Current, page.Id, items, "undo.insertImage"));
        SetTool(ToolKind.Select);
        selectTool.SetSelection(items.Select(i => i.Item1.Id));
        Canvas.Focus();
        return true;
    }

    /// <summary>필기 › 이미지: a file from the open dialog.</summary>
    public async Task InsertImageFromFileAsync()
    {
        if (await dialogs.ChooseImageAsync() is not [{ } path, ..]) return;
        try
        {
            if (new FileInfo(path).Length > ModelLimits.MaxResourceBytes) throw new InvalidDataException();
            InsertImage(await File.ReadAllBytesAsync(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { Tell(Strings.Get("image.readFailed"), NoticeKind.Warning); }
    }

    // ----- files dropped on the window (spec 4.2, 24.11, D13j) -----

    private Point2? dropMark;

    private void InitializeDrop()
    {
        AddHandler(DragDrop.DragOverEvent, WindowDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => { ShowDropMark(null); PageDropLine.IsVisible = pageDragging; });
        AddHandler(DragDrop.DropEvent, WindowDrop);
    }

    private static string[]? DroppedFiles(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles() is { Length: > 0 } items ? items.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray() : null;

    private string[]? DroppedImages(DragEventArgs e) => !busy && Document is not null && DroppedFiles(e) is { Length: > 0 } f && f.All(IsImageFile) ? f : null;

    private static string[]? DroppedPdfs(DragEventArgs e) =>
        DroppedFiles(e) is { Length: > 0 } f && f.All(p => string.Equals(System.IO.Path.GetExtension(p), ".pdf", StringComparison.OrdinalIgnoreCase)) ? f : null;

    /// <summary>Over the visible part of the page list (the scroller's view, not the scrolled list's own coordinates).</summary>
    private bool OverPageList(DragEventArgs e) => Document is not null && PagePanel.IsVisible && e.GetPosition(PageScroller) is var at &&
        at.X >= 0 && at.Y >= 0 && at.X <= PageScroller.Bounds.Width && at.Y <= PageScroller.Bounds.Height;

    private void WindowDragOver(object? sender, DragEventArgs e)
    {
        bool images = DroppedImages(e) is not null;
        bool pdfs = !busy && !viewModel.ImportExport.Importing && DroppedPdfs(e) is not null;
        e.DragEffects = pdfs || images ? DragDropEffects.Copy : DragDropEffects.None;
        var p = e.GetPosition(Canvas);
        ShowDropMark(images ? new Point2(p.X, p.Y) : null);
        if (pdfs && OverPageList(e)) ShowDropLine(DropIndex(e.GetPosition(PageList)));
        else if (!pageDragging) PageDropLine.IsVisible = false;
        e.Handled = true;
    }

    /// <summary>One PDF opens it (spec 4.2); several are inserted; on the page list they go in at the line; images go where they are dropped.</summary>
    private async void WindowDrop(object? sender, DragEventArgs e)
    {
        ShowDropMark(null);
        if (DroppedImages(e) is { } images)
        {
            e.Handled = true;
            Activate();
            var p = e.GetPosition(Canvas);
            var anchor = Canvas.HitTestPage(new(p.X, p.Y));
            try
            {
                var bytes = await Task.WhenAll(images.Select(f => new FileInfo(f).Length > ModelLimits.MaxResourceBytes ? throw new InvalidDataException() : File.ReadAllBytesAsync(f)));
                InsertImages(bytes, anchor?.PageIndex, anchor?.PagePoint);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Tell(Strings.Get("image.readFailed"), NoticeKind.Warning); }
            return;
        }
        if (DroppedPdfs(e) is not { } paths)
        {
            if (DroppedFiles(e) is not null) viewModel.Status = Strings.Get("drop.unsupported");
            return;
        }
        e.Handled = true;
        bool onList = OverPageList(e);
        int at = onList ? DropIndex(e.GetPosition(PageList)) : -1;
        PageDropLine.IsVisible = false;
        Activate();
        if (onList) await ImportPdfPagesAsync(paths, at);
        else if (paths.Length > 1 && Document is not null) await ImportPdfPagesAsync(paths);
        else if (paths.Length == 1) await OpenFileAsync(paths[0]);
    }

    /// <summary>While image files are dragged over a page, a small cross marks where they will land.</summary>
    private void ShowDropMark(Point2? viewport)
    {
        if (dropMark == viewport) return;
        dropMark = viewport;
        Canvas.RefreshOverlays();
    }

    private Point2? DropMarkOn(int pageIndex) =>
        dropMark is { } p && Canvas.HitTestPage(p) is { } at && at.PageIndex == pageIndex ? Canvas.PageTransform(pageIndex).PageToDip.Transform(at.PagePoint) : null;

    private static void PaintDropMark(SKCanvas canvas, Point2 c)
    {
        using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, Color = SKColors.White };
        using var pen = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = SKAnnotationColors.Selection };
        foreach (var paint in new[] { halo, pen })
        {
            canvas.DrawLine((float)c.X - 10, (float)c.Y, (float)c.X + 10, (float)c.Y, paint);
            canvas.DrawLine((float)c.X, (float)c.Y - 10, (float)c.X, (float)c.Y + 10, paint);
        }
    }

    private static class SKAnnotationColors
    {
        public static readonly SKColor Selection = new(0x1E, 0x6F, 0xD9); // Overlay.Selection
    }
}
