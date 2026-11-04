using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PdfNotes.App.Services;
using PdfNotes.App.Tools;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

public partial class MainWindow
{
    private readonly IEditorClipboard clipboard = new WpfClipboard();
    private bool suppressContext;
    private void InitializeSelectionCommands()
    {
        foreach (var command in new[] { ApplicationCommands.Copy, ApplicationCommands.Cut, ApplicationCommands.Paste, ApplicationCommands.SelectAll })
            CommandBindings.Add(new CommandBinding(command, SelectionExecuted, SelectionCanExecute));
        View.PreviewMouseRightButtonDown += (_, _) => suppressContext = View.IsToolActive;
        View.MouseRightButtonUp += (_, e) => { if (!suppressContext) OpenAnnotationMenu(e.GetPosition(View)); e.Handled = true; };
    }
    private SelectionViewModel selection => viewModel.Selection;

    private static SelectionCommand ToSelectionCommand(ICommand command) =>
        command == ApplicationCommands.Copy ? SelectionCommand.Copy : command == ApplicationCommands.Cut ? SelectionCommand.Cut :
        command == ApplicationCommands.Paste ? SelectionCommand.Paste : SelectionCommand.SelectAll;

    private void SelectionCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (!View.IsKeyboardFocusWithin) return;
        e.CanExecute = selection.CanExecute(ToSelectionCommand(e.Command));
        e.Handled = true;
    }
    private void SelectionExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        var command = ToSelectionCommand(e.Command);
        if (command == SelectionCommand.Paste) PasteSelection();
        else selection.Execute(command);
        e.Handled = true;
    }
    public void SelectAllAnnotations() => selection.SelectAll();
    public bool CopySelection(IEnumerable<Guid>? annotationIds = null, Guid? pageId = null) => selection.Copy(annotationIds, pageId);

    /// <summary>Copies the selected PDF text as the engine reports it (spec 5.3, 17).</summary>
    public bool CopySelectedText() => selection.CopyText();

    /// <summary>Turns the selected PDF text into a glyph highlight with the highlight tool's style (one Undo unit).</summary>
    public bool HighlightSelectedText() => selection.HighlightText();

    // ----- PDF glyphs of source pages, cached per open document (spec 9.3) -----

    /// <summary>Glyph extraction of the open document (<see cref="MainViewModel.Glyphs"/>).</summary>
    private PageGlyphSource GlyphSource => viewModel.Glyphs;

    /// <summary>Selectable glyphs of a page in reading order; generated pages and pages without text have none (no OCR).</summary>
    public IReadOnlyList<TextGlyph> PageGlyphs(int pageIndex) => viewModel.PageGlyphs(pageIndex);

    /// <summary>
    /// Pastes annotations, else an image, else plain text as a new text box (spec 5.7, D14g). Where: a right-click paste
    /// at <paramref name="viewportPoint"/>; Ctrl+V at the mouse pointer when it is over a page; otherwise the current page,
    /// annotations keeping their place (10pt aside on their own page), an image or text in the middle. While the notes are
    /// hidden they are shown first (spec 24.10).
    /// </summary>
    public bool PasteSelection(Point? viewportPoint = null)
    {
        if (document is null) return false;
        if (NotesHidden) SetNotesHidden(false);
        View.CancelTool();
        var at = viewportPoint ?? (View.IsMouseOver ? Mouse.GetPosition(View) : null);
        var anchor = at is { } p ? View.HitTestPage(p) : null;
        int pageIndex = anchor?.PageIndex ?? View.CurrentPageIndex;
        Point2? center = anchor?.PagePoint;
        var content = selection.Paste(pageIndex, center);
        switch (content.Kind)
        {
            case PasteKind.Annotations: return true;
            case PasteKind.Image: return InsertImage(content.Png!, pageIndex, center);
            case PasteKind.Text: PastePlainTextBox(content.Text!, pageIndex, center); return true;
            default: return false;
        }
    }

    /// <summary>Inserts an image fitted into 80 % of the page, centered on <paramref name="center"/> (null: the page middle) of page <paramref name="pageIndex"/> (null: the current page).</summary>
    public bool InsertImage(byte[] bytes, int? pageIndex = null, Point2? center = null)
    {
        if (document is null) return false;
        if (NotesHidden) SetNotesHidden(false);
        try
        {
            var resource = ImageAssets.Read(bytes);
            var bitmap = ImageAssets.Decode(bytes);
            var page = document.Session.Current.Pages[pageIndex ?? View.CurrentPageIndex];
            var image = ImagePlacement.Place(resource, bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, page, center);
            View.CancelTool();
            document.Session.Execute(DocumentEdits.AddAnnotation(document.Session.Current, page.Id, image, resource, "undo.insertImage"));
            SetTool(ToolKind.Select); selectTool.Select(image.Id); View.Focus(); return true;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException or IOException or System.Runtime.InteropServices.COMException)
        { ShowMessage(Strings.Get("image.insertFailed"), MessageBoxImage.Warning); return false; }
    }
    private async void InsertImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = Strings.Get("image.filter"), Title = Strings.Get("image.insertTitle") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > ModelLimits.MaxResourceBytes) throw new InvalidDataException();
            InsertImage(await File.ReadAllBytesAsync(dialog.FileName));
        }
        catch (IOException) { ShowMessage(Strings.Get("image.readFailed"), MessageBoxImage.Warning); }
        catch (UnauthorizedAccessException) { ShowMessage(Strings.Get("image.readFailed"), MessageBoxImage.Warning); }
    }
    public ContextMenu? OpenAnnotationMenu(Point position)
    {
        FinishTextEditing();
        if (document is null || View.HitTestPage(position) is not { } anchor) return null;
        var hit = selectTool.Pick(new(position.X, position.Y), includeLocked: true);
        bool onText = hit is null && selectTool.HasTextSelection && selectTool.TextSelectionPage == anchor.PageIndex &&
            selectTool.TextSelectionQuads(anchor.PageIndex).Any(q => TextSelection.DistanceToQuad(q, anchor.PagePoint) <= 2);
        if (selection.Menu(anchor.PageIndex, hit, onText) is not { } entries) return null;
        var menu = new ContextMenu { PlacementTarget = View };
        foreach (var entry in entries.Entries)
        {
            if (entry.SeparatorBefore) menu.Items.Add(new Separator());
            if (entry.Command == AnnotationCommand.SavedStyles) { menu.Items.Add(SavedTextStylesMenu(entry.Enabled)); continue; }
            var item = new MenuItem { Header = entry.Text, IsEnabled = entry.Enabled, IsChecked = entry.Checked };
            if (entry.Command == AnnotationCommand.AddTextBox) item.Icon = Glyph("\uE8D2");
            else if (entry.Tool is { } kind) item.Icon = ToolIcon(kind); // the ribbon icons (2026-10-15)
            item.Click += (_, _) =>
            {
                View.CancelTool();
                if (!selection.Run(entries, entry))
                    switch (entry.Command)
                    {
                        case AnnotationCommand.AddTextBox: BeginNewTextAtClick(anchor.PageIndex, anchor.PagePoint, 180); break;
                        case AnnotationCommand.Paste: PasteSelection(position); break;
                        case AnnotationCommand.ConvertForeign: _ = ConvertForeignAsync(); break;
                    }
                if (textEditor is null) View.Focus();
            };
            menu.Items.Add(item);
        }
        if (hit is not null || !onText)
            menu.Closed += (_, _) => { if (textEditor is not null) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => textEditor?.Focus()); };
        menu.IsOpen = true; LastCanvasMenu = menu; return menu;
    }

    /// <summary>The ribbon icon of a tool, for the quick menu (2026-10-15).</summary>
    private FrameworkElement ToolIcon(ToolKind kind) => kind switch
    {
        ToolKind.Select => Glyph("\uE8B0"),
        ToolKind.Lasso => new ContentControl { Template = (ControlTemplate)FindResource("Part.Icon.Lasso"), Focusable = false },
        ToolKind.Pen => Glyph("\uEDFB"),
        ToolKind.FreeHighlight => Glyph("\uE7E6"),
        ToolKind.StrokeEraser or ToolKind.AreaEraser => Glyph("\uE75C"),
        ToolKind.Line or ToolKind.Curve => ShapeIcon(kind, settings.ShapeKind),
        _ => Glyph("\uE8B0")
    };

    private static TextBlock Glyph(string text)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        block.SetResourceReference(TextBlock.FontSizeProperty, "FontSize.Icon");
        return block;
    }

    /// <summary>True when the clipboard holds annotations, an image or text (an empty clipboard disables 붙여넣기, spec 3.2).</summary>
    private bool CanPaste => selection.CanPaste;

    /// <summary>The canvas menu opened last (validation harness: menus opened by a real right-click).</summary>
    public ContextMenu? LastCanvasMenu { get; private set; }
}
