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
    private void SelectionCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (!View.IsKeyboardFocusWithin) return;
        e.CanExecute = document is not null && (e.Command == ApplicationCommands.Paste || e.Command == ApplicationCommands.SelectAll || selectTool.SelectedIds.Count > 0 ||
            e.Command == ApplicationCommands.Copy && selectTool.HasTextSelection);
        e.Handled = true;
    }
    private void SelectionExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Copy && selectTool.HasTextSelection) CopySelectedText(); // PDF text first (spec 17)
        else if (e.Command == ApplicationCommands.Copy) CopySelection();
        else if (e.Command == ApplicationCommands.Cut) { if (CopySelection()) DeleteSelection(); }
        else if (e.Command == ApplicationCommands.Paste) PasteSelection();
        else SelectAllAnnotations();
        e.Handled = true;
    }
    public void SelectAllAnnotations()
    {
        if (document is null) return;
        if (NotesHidden) SetNotesHidden(false); // spec 24.10, D14g
        SetTool(ToolKind.Select);
        selectTool.SetSelection(document.Session.Current.Pages[View.CurrentPageIndex].Annotations.Where(a => !a.Locked).Select(a => a.Id));
    }
    public bool CopySelection(IEnumerable<Guid>? annotationIds = null, Guid? pageId = null)
    {
        if (document is null || (pageId ?? selectTool.PageId) is not { } page) return false;
        try
        {
            if (AnnotationTransfer.Encode(document.Session.Current, page, annotationIds ?? selectTool.SelectedIds) is not { } packet) return false;
            clipboard.SetData(AnnotationTransfer.Format, packet);
            return true;
        }
        catch (Exception e) when (e is ClipboardUnavailableException or InvalidDataException)
        { ShowMessage(Strings.Get("clipboard.copyFailed"), MessageBoxImage.Warning); return false; }
    }
    /// <summary>Copies the selected PDF text as the engine reports it (spec 5.3, 17).</summary>
    public bool CopySelectedText()
    {
        string text = selectTool.SelectedText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (text.Length == 0) return false;
        try { clipboard.SetText(text.Replace("\n", Environment.NewLine, StringComparison.Ordinal)); return true; }
        catch (ClipboardUnavailableException) { ShowMessage(Strings.Get("clipboard.copyFailed"), MessageBoxImage.Warning); return false; }
    }

    /// <summary>Turns the selected PDF text into a glyph highlight with the highlight tool's style (one Undo unit).</summary>
    public bool HighlightSelectedText()
    {
        if (document is null || !selectTool.HasTextSelection) return false;
        int page = selectTool.TextSelectionPage;
        var (start, end) = selectTool.TextSelectionRange;
        var glyphs = PageGlyphs(page);
        var quads = TextSelection.LineQuads(glyphs, start, end);
        if (quads.IsEmpty) return false;
        var s = TextHighlightToolStyle;
        string hint = TextSelection.TextOf(glyphs, start, end);
        if (hint.Length > ModelLimits.MaxHintLength) hint = hint[..ModelLimits.MaxHintLength];
        var highlight = new PdfTextHighlight(Guid.NewGuid(), false, quads, hint, s.Mode, s.ManualHeight, s.Style.Color, s.Style.Opacity);
        selectTool.ClearTextSelection();
        return document.Session.Execute(DocumentEdits.AddAnnotation(document.Session.Current, document.Session.Current.Pages[page].Id, highlight, label: "undo.textHighlight"));
    }

    // ----- PDF glyphs of source pages, cached per open document (spec 9.3) -----

    private OpenDocument? glyphDocument;
    private PageGlyphSource glyphSource = new((PdfNotes.Pdf.Reading.DocumentSources?)null);

    /// <summary>Glyph extraction of the open document; replaced when another document is shown.</summary>
    private PageGlyphSource GlyphSource
    {
        get
        {
            if (!ReferenceEquals(glyphDocument, document)) { glyphDocument = document; glyphSource = new(document?.Sources); }
            return glyphSource;
        }
    }

    /// <summary>Selectable glyphs of a page in reading order; generated pages and pages without text have none (no OCR).</summary>
    public IReadOnlyList<TextGlyph> PageGlyphs(int pageIndex) =>
        document is not { } doc || pageIndex < 0 || pageIndex >= doc.Session.Current.Pages.Count ? [] : GlyphSource.Glyphs(doc.Session.Current.Pages[pageIndex].Setup);

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
        try
        {
            if (clipboard.ContainsData(AnnotationTransfer.Format))
            {
                var copied = AnnotationTransfer.Decode(clipboard.GetData(AnnotationTransfer.Format), bytes => ImageAssets.Decode(bytes));
                var target = document.Session.Current.Pages[pageIndex].Id;
                var pasted = copied.Paste(document.Session.Current, target, center, PdfNotes.Text.TextLayout.Bounds);
                document.Session.Execute(pasted.Command);
                SetTool(ToolKind.Select); selectTool.SetSelection(pasted.Ids); return true;
            }
            if (clipboard.ContainsImage() && clipboard.GetImagePng() is { } png) return InsertImage(png, pageIndex, center);
            if (clipboard.ContainsText()) { PastePlainTextBox(clipboard.GetText(), pageIndex, center); return true; }
            return false;
        }
        catch (Exception e) when (e is ClipboardUnavailableException or JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        { ShowMessage(Strings.Get("clipboard.pasteFailed"), MessageBoxImage.Warning); return false; }
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
        var model = document.Session.Current;
        Guid pageId = model.Pages[anchor.PageIndex].Id;
        var hit = selectTool.Pick(new(position.X, position.Y), includeLocked: true);
        var menu = new ContextMenu { PlacementTarget = View };
        void Add(string text, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled };
            item.Click += (_, _) => { View.CancelTool(); action(); if (textEditor is null) View.Focus(); };
            menu.Items.Add(item);
        }
        if (hit is null && selectTool.HasTextSelection && selectTool.TextSelectionPage == anchor.PageIndex &&
            selectTool.TextSelectionQuads(anchor.PageIndex).Any(q => TextSelection.DistanceToQuad(q, anchor.PagePoint) <= 2))
        {
            Add(Strings.Get("command.copy"), () => CopySelectedText());
            Add(Strings.Get("command.highlight"), () => HighlightSelectedText(), !NotesHidden);
            menu.IsOpen = true; LastCanvasMenu = menu; return menu;
        }
        if (hit is { } id)
        {
            var annotation = model.FindAnnotation(id)!.Annotation;
            if (!annotation.Locked && !selectTool.SelectedIds.Contains(id)) { SetTool(ToolKind.Select); selectTool.Select(id); }
            Guid[] targets = annotation.Locked ? [id] : selectTool.SelectedIds.ToArray();
            if (annotation is TextBoxAnnotation)
            {
                menu.Items.Add(SavedTextStylesMenu(!annotation.Locked));
                menu.Items.Add(new Separator());
            }
            Add(annotation.Locked ? Strings.Get("command.unlock") : Strings.Get("command.lock"), () => document.Session.Execute(DocumentEdits.SetLocked(document.Session.Current, targets, !annotation.Locked)));
            menu.Items.Add(new Separator());
            Add(Strings.Get("command.copy"), () => CopySelection(targets, pageId));
            Add(Strings.Get("command.cut"), () => { if (CopySelection(targets, pageId)) DeleteSelection(); }, !annotation.Locked);
            Add(Strings.Get("command.paste"), () => PasteSelection(position), CanPaste);
            Add(Strings.Get("command.delete"), DeleteSelection, !annotation.Locked);
            menu.Items.Add(new Separator());
            foreach (var (label, order) in new[] { (Strings.Get("order.forward"), ZOrderChange.Forward), (Strings.Get("order.backward"), ZOrderChange.Backward), (Strings.Get("order.toFront"), ZOrderChange.ToFront), (Strings.Get("order.toBack"), ZOrderChange.ToBack) })
                Add(label, () => document.Session.Execute(DocumentEdits.ChangeZOrder(document.Session.Current, pageId, targets, order)), !annotation.Locked);
        }
        else
        {
            Add(Strings.Get("menu.addTextBox"), () => BeginNewTextAtClick(anchor.PageIndex, anchor.PagePoint, 180));
            ((MenuItem)menu.Items[^1]).Icon = Glyph("\uE8D2");
            menu.Items.Add(new Separator());
            // The same switch as the toolbar (spec 5.6) with the ribbon icons (2026-10-15); the current tool is checked.
            var quickTools = new[] { (Strings.Get("tool.select"), ToolKind.Select), (Strings.Get("tool.lasso"), ToolKind.Lasso), (Strings.Get("tool.pen"), ToolKind.Pen), (Strings.Get("ribbon.highlighter"), ToolKind.FreeHighlight),
                (Strings.Get("tool.strokeEraser"), ToolKind.StrokeEraser), (Strings.Get("tool.areaEraser"), ToolKind.AreaEraser), (Strings.Get("tool.line"), ToolKind.Line), (Strings.Get("tool.curve"), ToolKind.Curve) };
            foreach (var (label, kind) in quickTools)
            {
                Add(label, () => SetTool(kind));
                ((MenuItem)menu.Items[^1]).IsChecked = tools.Current == kind;
                ((MenuItem)menu.Items[^1]).Icon = ToolIcon(kind);
            }
            menu.Items.Add(new Separator()); Add(Strings.Get("command.paste"), () => PasteSelection(position), CanPaste);
            if (ForeignAnnotationCount > 0) Add(Strings.Get("foreign.convertTip"), () => _ = ConvertForeignAsync(), CanConvertForeign); // spec 14.6, D14g
        }
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
    private bool CanPaste
    {
        get
        {
            try { return clipboard.ContainsData(AnnotationTransfer.Format) || clipboard.ContainsImage() || clipboard.ContainsText(); }
            catch (ClipboardUnavailableException) { return true; } // busy clipboard: let the command report it
        }
    }

    /// <summary>The canvas menu opened last (validation harness: menus opened by a real right-click).</summary>
    public ContextMenu? LastCanvasMenu { get; private set; }
}
