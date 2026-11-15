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

namespace PdfNotes.UI.Views;

/// <summary>
/// Sticky note cards (spec 24.9), hiding the notes (24.10), the pen favorites (24.7) and images (spec 10, 24.11): dropped,
/// inserted from a file or pasted. None of them changes how a document is saved.
/// </summary>
public sealed partial class MainView
{
    private NoteCardViewModel noteState => viewModel.NoteCard;

    /// <summary>The note being typed in (null: none).</summary>
    public StickyNote? OpenNoteAnnotation => noteState.Note;

    /// <summary>The text box of the card being typed in.</summary>
    public TextBox? NoteTextBox => noteState.Active is { } card ? NoteBox(card) : null;

    private void InitializeNotes()
    {
        Canvas.ViewChanged += (_, _) => PositionNoteCards();
        NoteLayer.SizeChanged += (_, _) => PositionNoteCards();
        // Cards opened, closed, moved with their notes: placed now, and again once a new card has its size.
        noteState.LayoutChanged += () =>
        {
            PositionNoteCards();
            Dispatcher.UIThread.Post(PositionNoteCards, DispatcherPriority.Loaded);
        };
        // A click on a sticky note with the selection tool opens it (spec 24.9); a drag only moved it.
        selectTool.Released += (_, changed) =>
        {
            if (!changed && selectTool.SelectedAnnotations is [StickyNote note] && selectTool.PageId is { } notePage)
                Dispatcher.UIThread.Post(() => OpenNote(note, notePage, isNew: false));
        };
        // The cards are a template over NoteCardViewModel.Cards; their keys, focus and drags are handled here for all of them.
        // Typing ends with Esc or when the keyboard leaves the card: a pinned card stays open, any other closes.
        NoteCards.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || CardOf(e.Source) is not { } card) return;
            noteState.Finish(card);
            Canvas.Focus();
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        NoteCards.AddHandler(GotFocusEvent, (_, e) => { if (CardOf(e.Source) is { } card) noteState.Activate(card); });
        NoteCards.AddHandler(LostFocusEvent, (_, e) =>
        {
            if (CardOf(e.Source) is not { } card) return;
            Dispatcher.UIThread.Post(() =>
            {
                // Not only the card being typed in: a save or a ribbon button ends the typing while the keyboard stays in a pinned card.
                if (noteState.Cards.Contains(card) && NoteCards.ContainerFromItem(card) is not { IsKeyboardFocusWithin: true }) noteState.Finish(card);
            });
        });
        // 보기 › 메모 펼치기 shows whether cards are pinned, also after a press that found no note to open.
        ExpandNotesButton.Click += (_, _) => Dispatcher.UIThread.Post(() => ExpandNotesButton.SetCurrentValue(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, noteState.HasPinned));
        NoteCards.AddHandler(PointerPressedEvent, NotePointerPressed);
        NoteCards.AddHandler(PointerMovedEvent, NotePointerMoved);
        NoteCards.AddHandler(PointerReleasedEvent, (_, e) => { if (noteDrag is not null) { noteDrag = null; e.Pointer.Capture(null); e.Handled = true; } });
        NoteCards.AddHandler(PointerCaptureLostEvent, (_, _) => noteDrag = null);
    }

    // ----- sticky note cards -----

    private static NoteCardItem? CardOf(object? source) => (source as StyledElement)?.DataContext as NoteCardItem;

    private TextBox? NoteBox(NoteCardItem card) =>
        NoteCards.ContainerFromItem(card) is { } container ? Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(container).OfType<TextBox>().FirstOrDefault() : null;

    /// <summary>The note tool's click: an icon centered on the point, opened for typing (not in the document yet).</summary>
    private void CreateNote(int pageIndex, Point2 point)
    {
        if (noteState.Create(pageIndex, point) is { } note && Document is { } doc) OpenNote(note, doc.Session.Current.Pages[pageIndex].Id, isNew: true);
    }

    /// <summary>Opens the note's card for typing; the card typed in before ends (a pinned one stays open).</summary>
    public void OpenNote(StickyNote note, Guid pageId, bool isNew)
    {
        if (!noteState.Open(note, pageId, isNew) || noteState.Active is not { } card) return;
        NoteCards.UpdateLayout();
        PositionNoteCards();
        Canvas.RefreshOverlays(); // a new note's icon is drawn while it is open
        if (NoteBox(card) is not { } box) return;
        box.Focus();
        box.CaretIndex = box.Text?.Length ?? 0;
    }

    /// <summary>The note icon on the screen: the box around it in the note layer's DIPs.</summary>
    private static Rect IconBox(StickyNote note, Affine pageToDip)
    {
        var corners = AnnotationGeometry.Corners(new(0, 0, StickyNote.IconPt, StickyNote.IconPt), note.Transform.Then(pageToDip));
        double left = corners.Min(p => p.X), top = corners.Min(p => p.Y);
        return new Rect(left, top, corners.Max(p => p.X) - left, corners.Max(p => p.Y) - top);
    }

    /// <summary>
    /// Keeps every card with its icon while the view scrolls or zooms: beside it (right, or left when the right has no
    /// room, inside the document area), or where the user dragged it — a distance from the icon in page points, joined to
    /// the icon by a thin line. A card whose icon is out of view is not shown, except the one being typed in.
    /// </summary>
    private void PositionNoteCards()
    {
        var links = new List<(Point From, Point To)>();
        if (Document is { } doc)
        {
            var model = doc.Session.Current;
            double width = NoteLayer.Bounds.Width, height = NoteLayer.Bounds.Height;
            foreach (var card in noteState.Cards)
            {
                if (NoteCards.ContainerFromItem(card) is not { } container) continue;
                int index = model.IndexOfPage(card.PageId);
                if (index < 0) { container.IsVisible = false; continue; }
                // The cards hear of a document change before the canvas has its pages (a page added in front of a pinned
                // card's page): this card waits for the call that follows the layout.
                if (index >= Canvas.PageCount) continue;
                var toDip = Canvas.PageTransform(index).PageToDip;
                var icon = IconBox(card.Note, toDip);
                double w = card.Width, h = container.Bounds.Height > 0 ? container.Bounds.Height : NoteCardItem.MinHeight;
                double x, y;
                if (card.Offset is { } offset)
                {
                    var at = toDip.Transform(new Point2(card.Note.Transform.OffsetX + offset.X, card.Note.Transform.OffsetY + offset.Y));
                    (x, y) = (at.X, at.Y);
                }
                else
                {
                    x = icon.Right + 6;
                    if (x + w > width && icon.X - 6 - w >= 0) x = icon.X - 6 - w;
                    x = Math.Clamp(x, 0, Math.Max(0, width - w));
                    y = Math.Clamp(icon.Y, 0, Math.Max(0, height - 100));
                }
                bool inView = icon.Right > 0 && icon.X < width && icon.Bottom > 0 && icon.Y < height;
                container.IsVisible = inView || ReferenceEquals(card, noteState.Active) || container.IsKeyboardFocusWithin;
                Avalonia.Controls.Canvas.SetLeft(container, x);
                Avalonia.Controls.Canvas.SetTop(container, y);
                if (!container.IsVisible || card.Offset is null) continue;
                var rect = new Rect(x, y, w, h);
                if (rect.Intersects(icon.Inflate(2))) continue; // still touching its icon
                // From the card's nearest point to the icon's edge (the line never crosses the icon).
                var near = new Point(Math.Clamp(icon.Center.X, rect.X, rect.Right), Math.Clamp(icon.Center.Y, rect.Y, rect.Bottom));
                links.Add((new Point(Math.Clamp(near.X, icon.X, icon.Right), Math.Clamp(near.Y, icon.Y, icon.Bottom)), near));
            }
        }
        NoteLinkLayer.Show(links);
    }

    // The band moves the card, the corner sizes it (spec 24.9). X and Y: the card's place, or its size, when the drag began.
    private (NoteCardItem Card, Point Start, double X, double Y, bool Sizing, bool Moved)? noteDrag;

    private void NotePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Control? part = null;
        for (var v = e.Source as Visual; v is not null && !ReferenceEquals(v, NoteCards); v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
            if (v is Control c && (c.Classes.Contains("noteBand") || c.Classes.Contains("noteGrip"))) { part = c; break; }
        if (part?.DataContext is not NoteCardItem card || NoteCards.ContainerFromItem(card) is not { } container) return;
        if (!e.GetCurrentPoint(NoteLayer).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        bool sizing = part.Classes.Contains("noteGrip");
        if (!sizing && e.ClickCount == 2) // back beside the icon
        {
            card.Offset = null;
            PositionNoteCards();
            return;
        }
        noteDrag = sizing
            ? (card, e.GetPosition(NoteLayer), card.Width, container.Bounds.Height, true, false)
            : (card, e.GetPosition(NoteLayer), Avalonia.Controls.Canvas.GetLeft(container), Avalonia.Controls.Canvas.GetTop(container), false, false);
        e.Pointer.Capture(part);
    }

    private void NotePointerMoved(object? sender, PointerEventArgs e)
    {
        if (noteDrag is not { } drag || Document is not { } doc) return;
        var p = e.GetPosition(NoteLayer);
        double dx = p.X - drag.Start.X, dy = p.Y - drag.Start.Y;
        if (!drag.Moved && Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return; // a press, not a drag
        noteDrag = drag with { Moved = true };
        e.Handled = true;
        var card = drag.Card;
        if (drag.Sizing) card.Resize(drag.X + dx, drag.Y + dy);
        else if (doc.Session.Current.IndexOfPage(card.PageId) is >= 0 and var index)
        {
            // The band stays within reach: the card never leaves the document area whole.
            double x = Math.Clamp(drag.X + dx, 40 - card.Width, Math.Max(0, NoteLayer.Bounds.Width - 40));
            double y = Math.Clamp(drag.Y + dy, 0, Math.Max(0, NoteLayer.Bounds.Height - 24));
            var at = Canvas.PageTransform(index).DipToPage.Transform(new Point2(x, y));
            card.Offset = new Point2(at.X - card.Note.Transform.OffsetX, at.Y - card.Note.Transform.OffsetY);
        }
        PositionNoteCards();
        if (drag.Sizing) Dispatcher.UIThread.Post(PositionNoteCards, DispatcherPriority.Loaded); // the line follows the new size
    }

    /// <summary>Ends the typing (a new note with text is added, an existing one updated or deleted when empty); pinned cards stay open.</summary>
    public void CloseNote() => noteState.Commit();

    /// <summary>
    /// A small AI mark at the lower right of an AI note's icon (spec 24.9): on the screen only, drawn as strokes so it
    /// needs no font. The saved note looks like any other.
    /// </summary>
    private static void PaintAiBadge(SKCanvas canvas, StickyNote note, ViewTransform t)
    {
        var icon = IconBox(note, t.PageToDip);
        float h = Math.Max(8, (float)icon.Height * 0.5f), w = h * 1.3f;
        var box = SKRect.Create((float)icon.Right - w * 0.6f, (float)icon.Bottom - h * 0.6f, w, h);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x33, 0x33, 0x33) };
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.White };
        using var ink = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, h * 0.11f), Color = SKColors.White, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        canvas.DrawRoundRect(box, h * 0.25f, h * 0.25f, fill);
        canvas.DrawRoundRect(box, h * 0.25f, h * 0.25f, edge);
        float top = box.Top + h * 0.24f, bottom = box.Bottom - h * 0.24f, left = box.Left + w * 0.2f, mid = box.Left + w * 0.42f, right = box.Left + w * 0.64f, bar = bottom - (bottom - top) * 0.33f;
        using var letters = new SKPath();
        letters.MoveTo(left, bottom); letters.LineTo(mid, top); letters.LineTo(right, bottom); // A
        letters.MoveTo(left + (mid - left) * 0.33f, bar); letters.LineTo(right - (right - mid) * 0.33f, bar);
        letters.MoveTo(box.Left + w * 0.8f, top); letters.LineTo(box.Left + w * 0.8f, bottom); // I
        canvas.DrawPath(letters, ink);
    }

    // ----- hide the notes (spec 24.10) -----

    public bool NotesHidden => viewModel.Display.NotesHidden;

    /// <summary>
    /// Hides or shows the notes. While hidden the selection tool selects no annotation and PDF text can still be selected,
    /// copied and searched; choosing a drawing tool, pasting or inserting shows them again (spec 24.10, D14g). A view
    /// change only: no Undo, no unsaved mark.
    /// </summary>
    public void SetNotesHidden(bool hidden) => viewModel.SetNotesHidden(hidden);

    // ----- pen favorites (spec 24.7) -----

    public IReadOnlyList<AppSettings.FavoriteSettings?> Favorites => viewModel.Favorites.Slots;

    /// <summary>A favorite button or Ctrl+1–4 (spec 24.7): see <see cref="ToolBarViewModel.ApplyFavorite"/>.</summary>
    public void ApplyFavorite(int slot) => viewModel.ToolBar.ApplyFavorite(slot);

    public void StoreFavorite(int slot) => viewModel.ToolBar.StoreFavorite(slot);

    // ----- images (spec 10) -----

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    private static bool IsImageFile(string path) => ImageExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static (ImageAnnotation Image, Resource Resource) PlacedImage(byte[] bytes, Page page, Point2? center)
    {
        var resource = ImageResources.Create(bytes);
        var (w, h, dpi) = ImageFiles.Check(bytes); // decodes whole, 64 M pixels at most, the file's DPI
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
            (topLevel as Window)?.Activate();
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
        (topLevel as Window)?.Activate();
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
