using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Tools;
using PdfNotes.ViewModels;

namespace PdfNotes.UI.Views;

/// <summary>
/// The canvas menu (spec 3.2, 5.6–5.9) and pasting (5.7, D14g): annotations from this app, else an image, else plain text
/// as a new text box. The entries and the commands are <see cref="SelectionViewModel"/>'s.
/// </summary>
public sealed partial class MainView
{
    private SelectionViewModel selection => viewModel.Selection;
    private bool suppressContext;
    private Point2? lastCanvasPointer;

    /// <summary>The canvas menu opened last (tests).</summary>
    public ContextMenu? LastCanvasMenu { get; private set; }

    private void InitializeSelectionCommands()
    {
        Canvas.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(Canvas).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed) suppressContext = Canvas.IsToolActive || PdfNotes.UI.Canvas.DocumentCanvas.FromScrollBar(e);
        }, RoutingStrategies.Tunnel);
        Canvas.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right) return;
            if (!suppressContext) { var p = e.GetPosition(Canvas); OpenAnnotationMenu(new(p.X, p.Y)); }
            suppressContext = false;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Canvas.PointerMoved += (_, e) => { var p = e.GetPosition(Canvas); lastCanvasPointer = new(p.X, p.Y); };
        Canvas.PointerExited += (_, _) => lastCanvasPointer = null;
    }

    /// <summary>
    /// The canvas menu at <paramref name="position"/> (viewport DIPs): on selected PDF text its copy and highlight; on an
    /// annotation lock, clipboard and order; elsewhere a new text box, the quick tools and paste.
    /// </summary>
    public ContextMenu? OpenAnnotationMenu(Point2 position)
    {
        FinishTextEditing();
        if (Document is null || Canvas.HitTestPage(position) is not { } anchor) return null;
        var hit = selectTool.Pick(position, includeLocked: true);
        bool onText = hit is null && selectTool.HasTextSelection && selectTool.TextSelectionPage == anchor.PageIndex &&
            selectTool.TextSelectionQuads(anchor.PageIndex).Any(q => TextSelection.DistanceToQuad(q, anchor.PagePoint) <= 2);
        if (selection.Menu(anchor.PageIndex, hit, onText) is not { } entries) return null;
        var menu = new ContextMenu { Placement = PlacementMode.Pointer };
        foreach (var entry in entries.Entries)
        {
            if (entry.SeparatorBefore) menu.Items.Add(new Separator());
            if (entry.Command == AnnotationCommand.SavedStyles) { menu.Items.Add(SavedTextStylesMenu(entry.Enabled)); continue; }
            var item = Menus.Item(entry.Text, () =>
            {
                Canvas.CancelTool();
                if (!selection.Run(entries, entry))
                    switch (entry.Command)
                    {
                        case AnnotationCommand.AddTextBox: BeginNewTextAtClick(anchor.PageIndex, anchor.PagePoint, 180); break;
                        case AnnotationCommand.Paste: PasteSelection(position); break;
                        case AnnotationCommand.ConvertForeign: _ = ConvertForeignAsync(); break;
                    }
                if (!IsEditingText) Canvas.Focus();
                styles.Refresh();
            }, isChecked: entry.Checked, enabled: entry.Enabled);
            if (entry.Command == AnnotationCommand.AddTextBox) item.Icon = new Themes.AppIcon { Glyph = "Text" };
            else if (entry.Tool is { } kind) item.Icon = ToolIcon(kind); // the ribbon icons (2026-10-15)
            menu.Items.Add(item);
        }
        menu.Open(Canvas);
        return LastCanvasMenu = menu;
    }

    /// <summary>
    /// Pastes annotations, else an image, else plain text as a new text box (spec 5.7, D14g). Where: a right-click paste at
    /// <paramref name="viewportPoint"/>; Ctrl+V at the pointer when it is over a page; otherwise the current page
    /// (annotations keeping their place, an image or text in the middle). Hidden notes are shown first (spec 24.10).
    /// </summary>
    public bool PasteSelection(Point2? viewportPoint = null)
    {
        if (Document is null) return false;
        if (NotesHidden) SetNotesHidden(false);
        Canvas.CancelTool();
        var at = viewportPoint ?? lastCanvasPointer;
        var anchor = at is { } p ? Canvas.HitTestPage(p) : null;
        int pageIndex = anchor?.PageIndex ?? Canvas.CurrentPageIndex;
        Point2? center = anchor?.PagePoint;
        var content = selection.Paste(pageIndex, center);
        switch (content.Kind)
        {
            case PasteKind.Annotations: styles.Refresh(); return true;
            case PasteKind.Image: return InsertImage(content.Png!, pageIndex, center);
            case PasteKind.Text: PastePlainTextBox(content.Text!, pageIndex, center); return true;
            default: return false;
        }
    }

    /// <summary>Plain text from the clipboard as a new text box whose first line starts at <paramref name="at"/> (null: the page middle).</summary>
    private void PastePlainTextBox(string plain, int pageIndex, Point2? at)
    {
        if (Document is not { } doc) return;
        var page = doc.Session.Current.Pages[pageIndex];
        if (at is { } point) BeginNewTextAtClick(pageIndex, point);
        else BeginNewText(pageIndex, new(page.Setup.WidthPt / 2 - 90, page.Setup.HeightPt / 2));
        editSession?.Model.Type(plain.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        Dispatcher.UIThread.Post(() => textEditor?.InvalidateVisual());
    }
}
