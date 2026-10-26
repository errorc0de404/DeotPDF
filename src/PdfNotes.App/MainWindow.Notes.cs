using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PdfNotes.App.Canvas;
using PdfNotes.App.Services;
using PdfNotes.App.Tools;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using Page = PdfNotes.Core.Models.Page;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

/// <summary>
/// Note-taking conveniences of D13i: reading dark page (spec 24.12), hiding the notes (24.10), pen favorites (24.7) and
/// image files dropped on a page (24.11). None of them changes how a document is saved.
/// </summary>
public partial class MainWindow
{
    private InvertEffect? darkPageEffect; // created the first time it is needed

    // ----- 24.12 reading dark page -----

    /// <summary>The page display is inverted (setting 화면 › 어두운 페이지, or its preview in the settings window).</summary>
    public bool DarkPagesShown { get; private set; }

    /// <summary>
    /// Sets the <c>Page.Effect</c> resource: page content in the document area and the thumbnails take it; marks, the
    /// app frame, saving, Flatten, backups and the clipboard never see it.
    /// </summary>
    private void ShowDarkPages(bool on)
    {
        DarkPagesShown = on;
        if (on && darkPageEffect is null) { darkPageEffect = new InvertEffect(); darkPageEffect.Freeze(); }
        Resources["Page.Effect"] = on ? darkPageEffect : null;
    }

    // ----- 24.10 hide the notes -----

    /// <summary>App annotations are hidden on the pages and thumbnails (보기 › 필기 숨기기). View state only.</summary>
    public bool NotesHidden { get; private set; }

    private void HideNotesClick(object sender, RoutedEventArgs e) => SetNotesHidden(HideNotesButton.IsChecked == true);

    /// <summary>
    /// Hides or shows the notes. While hidden the selection tool selects no annotation and PDF text can still be selected,
    /// copied and searched; choosing a drawing tool, pasting or inserting shows the notes again (spec 24.10, D14g).
    /// Never a document change: no Undo, no unsaved mark.
    /// </summary>
    public void SetNotesHidden(bool hidden)
    {
        if (NotesHidden == hidden) { HideNotesButton.IsChecked = hidden; return; }
        FinishTextEditing();
        View.CancelTool();
        NotesHidden = hidden;
        if (hidden) { SetTool(ToolKind.Select); selectTool.Select(null); }
        selectTool.IgnoreAnnotations = hidden;
        HideNotesButton.IsChecked = hidden;
        NotesHiddenText.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        UpdateStyleBar();
        View.RefreshOverlays();
        foreach (var thumb in pageThumbs) thumb.Rendered = null;
        RenderThumbnails();
    }

    // ----- 24.7 pen favorites -----

    public IReadOnlyList<AppSettings.FavoriteSettings?> Favorites => settings.Favorites;

    /// <summary>The favorites at the right end of the settings row under the ribbon (2026-10-15: every tab, not only 필기).</summary>
    private void ShowFavorites(bool shown) => FavoritesGroup.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The four favorite buttons at the right end of the settings row.</summary>
    private void BuildFavorites()
    {
        FavoritesPanel.Children.Clear();
        for (int i = 0; i < settings.Favorites.Length; i++)
        {
            int slot = i;
            var button = new Button { Name = $"Favorite{i}", Style = (Style)FindResource("ToolButton"), Focusable = false, Padding = new Thickness(2),
                Content = FavoriteIcon(settings.Favorites[i]), ToolTip = FavoriteTip(settings.Favorites[i], i) };
            button.Click += (_, _) => ApplyFavorite(slot);
            var replace = new MenuItem { Header = Strings.Get("favorites.replace") };
            replace.Click += (_, _) => StoreFavorite(slot);
            var clear = new MenuItem { Header = Strings.Get("favorites.clear"), IsEnabled = settings.Favorites[i] is not null };
            clear.Click += (_, _) => ClearFavorite(slot);
            button.ContextMenu = new ContextMenu { Items = { replace, clear } };
            FavoritesPanel.Children.Add(button);
        }
    }

    /// <summary>`펜 2pt (Ctrl+1)`, `밑줄 1.5pt (Ctrl+3)`; an empty slot says how to fill it (spec 24.7).</summary>
    private static string FavoriteTip(AppSettings.FavoriteSettings? slot, int index)
    {
        if (slot is null) return Strings.Get("favorites.emptyTip");
        string name = slot.Tool != ToolKind.FreeHighlight ? Strings.Get("ribbon.pen") : slot.MarkKind switch
        {
            TextMarkupKind.Underline => Strings.Get("markup.underline"),
            TextMarkupKind.StrikeOut => Strings.Get("text.strikeOut"),
            _ => Strings.Get("ribbon.highlighter")
        };
        return Strings.Format("favorites.tip", ("name", name), ("width", Math.Round(slot.Style.WidthPt, 1)), ("key", index + 1));
    }

    /// <summary>
    /// A colored dot sized by the width (pen), a wide translucent bar (highlighter), a thin line (underline/strikeout), or
    /// a dashed ring (empty).
    /// </summary>
    private FrameworkElement FavoriteIcon(AppSettings.FavoriteSettings? slot)
    {
        var grid = new Grid { Width = 16, Height = 16 };
        if (slot is null)
        {
            var ring = new Ellipse { Width = 12, Height = 12, StrokeThickness = 1, StrokeDashArray = [2, 2] };
            ring.SetResourceReference(Shape.StrokeProperty, "Text.Muted");
            grid.Children.Add(ring);
            return grid;
        }
        var style = slot.Style.ToStyle(StrokeStyle.DefaultPen, FavoriteKind(slot));
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * (1 - style.TransparencyPercent / 100)), style.Color.R, style.Color.G, style.Color.B));
        brush.Freeze();
        Shape mark = slot.Tool != ToolKind.FreeHighlight
            ? new Ellipse { Width = Math.Clamp(4 + style.WidthPt * 1.5, 5, 14), Height = Math.Clamp(4 + style.WidthPt * 1.5, 5, 14), Fill = brush, StrokeThickness = .5 }
            : slot.MarkKind is null ? new Rectangle { Width = 16, Height = 8, RadiusX = 2, RadiusY = 2, Fill = brush }
            : new Rectangle { Width = 16, Height = 3, Fill = brush, VerticalAlignment = slot.MarkKind == TextMarkupKind.Underline ? VerticalAlignment.Bottom : VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, slot.MarkKind == TextMarkupKind.Underline ? 2 : 0) };
        mark.SetResourceReference(Shape.StrokeProperty, "Divider"); // a white dot stays visible
        grid.Children.Add(mark);
        return grid;
    }

    private static AnnotationKind FavoriteKind(AppSettings.FavoriteSettings slot) =>
        slot.Tool != ToolKind.FreeHighlight ? AnnotationKind.Ink : slot.MarkKind is null ? AnnotationKind.FreeHighlight : AnnotationKind.TextMarkup;

    /// <summary>
    /// A favorite switches to its tool (the highlighter with its kind) with its color, width and transparency; an empty
    /// one stores the current settings. While the notes are hidden it shows them first (spec 24.10).
    /// </summary>
    public void ApplyFavorite(int slot)
    {
        if (slot < 0 || slot >= settings.Favorites.Length) return;
        if (settings.Favorites[slot] is not { } favorite) { StoreFavorite(slot); return; }
        if (NotesHidden) SetNotesHidden(false);
        if (favorite.Tool == ToolKind.FreeHighlight)
        {
            markKind = settings.MarkKind = favorite.MarkKind;
            switch (favorite.MarkKind)
            {
                case TextMarkupKind.Underline:
                    underlineStyle = favorite.Style.ToStyle(StrokeStyle.DefaultMarkup, AnnotationKind.TextMarkup);
                    settings.Underline = AppSettings.StyleSettings.From(underlineStyle);
                    break;
                case TextMarkupKind.StrikeOut:
                    strikeOutStyle = favorite.Style.ToStyle(StrokeStyle.DefaultMarkup, AnnotationKind.TextMarkup);
                    settings.StrikeOut = AppSettings.StyleSettings.From(strikeOutStyle);
                    break;
                default:
                    highlighterStyle = favorite.Style.ToStyle(StrokeStyle.DefaultHighlighter, AnnotationKind.FreeHighlight);
                    settings.Highlighter = AppSettings.StyleSettings.From(highlighterStyle);
                    break;
            }
        }
        else
        {
            penStyle = favorite.Style.ToStyle(StrokeStyle.DefaultPen, AnnotationKind.Ink);
            settings.Pen = AppSettings.StyleSettings.From(penStyle);
        }
        settings.Save();
        SetTool(favorite.Tool);
        UpdateStyleBar();
    }

    /// <summary>Stores the current highlighter settings (with its kind) when the highlighter is the tool, otherwise the pen's.</summary>
    public void StoreFavorite(int slot)
    {
        bool highlighter = tools.Current == ToolKind.FreeHighlight;
        settings.Favorites[slot] = new()
        {
            Tool = highlighter ? ToolKind.FreeHighlight : ToolKind.Pen,
            MarkKind = highlighter ? markKind : null,
            Style = AppSettings.StyleSettings.From(!highlighter ? penStyle : markKind is null ? highlighterStyle : MarkupToolStyle)
        };
        settings.Save();
        BuildFavorites();
    }

    public void ClearFavorite(int slot)
    {
        settings.Favorites[slot] = null;
        settings.Save();
        BuildFavorites();
    }

    // ----- 24.11 image files dropped on a page -----

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    private static bool IsImageFile(string path) => ImageExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Image files only (10절 formats) that can go onto the current document.</summary>
    private string[]? DroppedImages(DragEventArgs e) =>
        !busy && document is not null && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && files.All(IsImageFile) ? files : null;

    private Point? dropMark;

    /// <summary>While image files are dragged over a page, a small cross marks where they will land.</summary>
    private void ShowDropMark(Point? viewport)
    {
        dropMark = viewport;
        if (viewport is { } p && View.HitTestPage(p) is { } at) View.SetPreview(at.PageIndex, PaintDropMark, mark: true);
        else View.SetPreview(-1, null);
    }

    private void PaintDropMark(DrawingContext context, int pageIndex, Page page, ViewTransform transform)
    {
        if (dropMark is not { } p || View.HitTestPage(p) is not { } at || at.PageIndex != pageIndex) return;
        var c = transform.PageToDip.Transform(at.PagePoint);
        var pen = new Pen(Themes.Theme.Brush("Overlay.Selection"), 1.5);
        var halo = new Pen(Themes.Theme.Brush("Overlay.Halo"), 3.5);
        foreach (var line in new[] { (new Point(c.X - 10, c.Y), new Point(c.X + 10, c.Y)), (new Point(c.X, c.Y - 10), new Point(c.X, c.Y + 10)) })
        {
            context.DrawLine(halo, line.Item1, line.Item2);
            context.DrawLine(pen, line.Item1, line.Item2);
        }
    }

    /// <summary>
    /// Inserts dropped image files centered where they were dropped (outside every page: the current page's center), 10 pt
    /// apart, each fitted into 80 % of the page (spec 10). One drop is one Undo unit; the images end up selected.
    /// </summary>
    public bool InsertImagesAt(IReadOnlyList<byte[]> files, Point viewport)
    {
        if (document is null || files.Count == 0) return false;
        if (NotesHidden) SetNotesHidden(false); // dropping images shows the notes (spec 24.10, D14g)
        int pageIndex; Point2 center;
        if (View.HitTestPage(viewport) is { } at) { pageIndex = at.PageIndex; center = at.PagePoint; }
        else
        {
            pageIndex = View.CurrentPageIndex;
            var setup = document.Session.Current.Pages[pageIndex].Setup;
            center = new(setup.WidthPt / 2, setup.HeightPt / 2);
        }
        var page = document.Session.Current.Pages[pageIndex];
        var items = new List<(Annotation, Resource?)>();
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                var (image, resource) = PlacedImage(files[i], page, new(center.X + i * 10, center.Y + i * 10));
                items.Add((image, resource));
            }
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException or IOException or System.Runtime.InteropServices.COMException)
        {
            ShowMessage(Strings.Get("image.insertFailed"), MessageBoxImage.Warning);
            return false;
        }
        View.CancelTool();
        document.Session.Execute(DocumentEdits.AddAnnotations(document.Session.Current, page.Id, items, "undo.insertImage"));
        SetTool(ToolKind.Select);
        selectTool.SetSelection(items.Select(i => i.Item1.Id));
        View.Focus();
        return true;
    }

    /// <summary>An image annotation of natural size fitted into 80 % of the page, centered on <paramref name="center"/>.</summary>
    private static (ImageAnnotation Image, Resource Resource) PlacedImage(byte[] bytes, Page page, Point2 center)
    {
        var resource = ImageAssets.Read(bytes);
        var bitmap = ImageAssets.Decode(bytes);
        return (ImagePlacement.Place(resource, bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, page, center), resource);
    }
}
