using System.Text.Json;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;

namespace PdfNotes.ViewModels;

/// <summary>The edit commands of the canvas (Ctrl+C/X/V/A, spec 5.7).</summary>
public enum SelectionCommand { Copy, Cut, Paste, SelectAll }

/// <summary>What a paste found on the clipboard: annotations (already pasted), an image or text for the view to place, or nothing.</summary>
public enum PasteKind { None, Annotations, Image, Text }

public sealed record PasteContent(PasteKind Kind, byte[]? Png = null, string? Text = null)
{
    public static PasteContent Nothing { get; } = new(PasteKind.None);
}

/// <summary>An entry of the canvas menu (spec 3.2, 5.7): the view adds icons and runs the commands it owns.</summary>
public enum AnnotationCommand
{
    CopyText, HighlightText,
    SavedStyles, Lock, Unlock, Copy, Cut, Paste, Delete, Forward, Backward, ToFront, ToBack,
    AddTextBox, Tool, ConvertForeign,
    /// <summary>내 필기로: the AI writing among the targets becomes the user's own (MCP_LIVE_PLAN 5).</summary>
    Adopt,
    /// <summary>A table's rows and columns at the cell the menu was opened on (1.4 plan 1.3 구조 편집).</summary>
    TableRowAbove, TableRowBelow, TableColumnLeft, TableColumnRight, TableDeleteRow, TableDeleteColumn,
    /// <summary>자동 너비 (1.4 plan 1.1): a check entry for one text box; on fits it to its text, off keeps the width it has.</summary>
    AutoWidth
}

/// <param name="SeparatorBefore">A line goes above this entry.</param>
/// <param name="Tool">The tool of a <see cref="AnnotationCommand.Tool"/> entry (quick menu, spec 5.6).</param>
public sealed record AnnotationMenuEntry(AnnotationCommand Command, string Text, bool Enabled = true, bool SeparatorBefore = false, ToolKind? Tool = null, bool Checked = false);

/// <summary>
/// The canvas menu for one right-click: its entries, and the annotations and page its commands act on (a locked
/// annotation alone; otherwise the selection, which the click made when it was outside it).
/// </summary>
/// <param name="Cell">The table cell the menu was opened on: the row and column commands act there.</param>
public sealed record AnnotationMenu(IReadOnlyList<AnnotationMenuEntry> Entries, Guid PageId, Guid[] Targets, (Guid Table, int Row, int Column)? Cell = null);

/// <summary>
/// Selection commands (spec 5.3, 5.7, 17; P1 V8): copy, cut, paste, delete and select all on the canvas, copying and
/// highlighting selected PDF text, lock and stacking order, and the canvas menu's entries. The selection tool, hit
/// testing and the menu controls stay in the view.
/// </summary>
public sealed class SelectionViewModel(MainViewModel owner, IEditorDialogs dialogs, IEditorClipboard clipboard) : ObservableObject
{
    private Texts texts => owner.Texts;

    private SelectTool? selection;
    private Action<byte[]> checkImage = _ => { };

    /// <summary>The selection tool (made by the view with its canvas) and the platform's image check for pasted annotations.</summary>
    public void Attach(SelectTool tool, Action<byte[]> imageCheck)
    {
        selection = tool;
        checkImage = imageCheck;
    }

    public SelectTool Tool => selection ?? throw new InvalidOperationException("No selection tool attached.");

    /// <summary>The selection tool, or null before the view made it.</summary>
    public SelectTool? Attached => selection;

    private OpenDocument? Document => owner.Document;

    private void Warn(string key) => dialogs.Tell(texts.Get(key), NoticeKind.Warning);

    /// <summary>Whether an edit command can run (with the canvas focused): paste and select all need a document only.</summary>
    public bool CanExecute(SelectionCommand command) => Document is not null &&
        (command is SelectionCommand.Paste or SelectionCommand.SelectAll || Tool.SelectedIds.Count > 0 || command == SelectionCommand.Copy && Tool.HasTextSelection);

    /// <summary>Runs an edit command but paste, which the view places (Ctrl+C copies selected PDF text first, spec 17).</summary>
    public void Execute(SelectionCommand command)
    {
        switch (command)
        {
            case SelectionCommand.Copy when Tool.HasTextSelection: CopyText(); break;
            case SelectionCommand.Copy: Copy(); break;
            case SelectionCommand.Cut: Cut(); break;
            case SelectionCommand.SelectAll: SelectAll(); break;
        }
    }

    /// <summary>Nothing selected (a command removed what was).</summary>
    public void ClearSelection() => Tool.SetSelection([]);

    /// <summary>Selects every unlocked annotation of the current page with the selection tool (hidden notes come back, spec 24.10).</summary>
    public void SelectAll()
    {
        if (Document is not { } doc) return;
        if (owner.Display.NotesHidden) owner.SetNotesHidden(false);
        owner.ToolBar.SetTool(ToolKind.Select);
        Tool.SetSelection(doc.Session.Current.Pages[owner.Viewport.CurrentPageIndex].Annotations.Where(a => !a.Locked).Select(a => a.Id));
    }

    /// <summary>Copies annotations (the selection by default) as the app's clipboard packet.</summary>
    public bool Copy(IEnumerable<Guid>? annotationIds = null, Guid? pageId = null)
    {
        if (Document is not { } doc || (pageId ?? Tool.PageId) is not { } page) return false;
        try
        {
            if (AnnotationTransfer.Encode(doc.Session.Current, page, annotationIds ?? Tool.SelectedIds) is not { } packet) return false;
            clipboard.SetData(AnnotationTransfer.Format, packet);
            return true;
        }
        catch (Exception e) when (e is ClipboardUnavailableException or InvalidDataException)
        { Warn("clipboard.copyFailed"); return false; }
    }

    /// <summary>Copies, then deletes what was copied.</summary>
    public void Cut(IEnumerable<Guid>? annotationIds = null, Guid? pageId = null)
    {
        if (Copy(annotationIds, pageId)) Delete();
    }

    /// <summary>Copies the selected PDF text as the engine reports it (spec 5.3, 17).</summary>
    public bool CopyText()
    {
        string text = Tool.SelectedText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (text.Length == 0) return false;
        try { clipboard.SetText(text.Replace("\n", Environment.NewLine, StringComparison.Ordinal)); return true; }
        catch (ClipboardUnavailableException) { Warn("clipboard.copyFailed"); return false; }
    }

    /// <summary>Deletes the selected annotations; an automatic selection visit then returns to its tool (spec 5.4).</summary>
    public void Delete()
    {
        if (Document is not { } doc || Tool.SelectedIds.Count == 0) return;
        owner.Editing.CancelTool();
        doc.Session.Execute(DocumentEdits.DeleteAnnotations(doc.Session.Current, Tool.SelectedIds));
        owner.ToolBar.ReturnAfterAutoSelect();
    }

    /// <summary>Turns the selected PDF text into a glyph highlight with the highlighter's style (one Undo unit).</summary>
    public bool HighlightText()
    {
        if (Document is not { } doc || !Tool.HasTextSelection) return false;
        int page = Tool.TextSelectionPage;
        var (start, end) = Tool.TextSelectionRange;
        var glyphs = owner.PageGlyphs(page);
        var quads = TextSelection.LineQuads(glyphs, start, end);
        if (quads.IsEmpty) return false;
        var s = owner.ToolStyles.TextHighlightStyle;
        string hint = TextSelection.TextOf(glyphs, start, end);
        if (hint.Length > ModelLimits.MaxHintLength) hint = hint[..ModelLimits.MaxHintLength];
        var highlight = new PdfTextHighlight(Guid.NewGuid(), false, quads, hint, s.Mode, s.ManualHeight, s.Style.Color, s.Style.Opacity);
        Tool.ClearTextSelection();
        return doc.Session.Execute(DocumentEdits.AddAnnotation(doc.Session.Current, doc.Session.Current.Pages[page].Id, highlight, label: "undo.textHighlight"));
    }

    /// <summary>True when the clipboard holds annotations, an image or text (an empty clipboard disables 붙여넣기, spec 3.2).</summary>
    public bool CanPaste
    {
        get
        {
            try { return clipboard.ContainsData(AnnotationTransfer.Format) || clipboard.ContainsImage() || clipboard.ContainsText(); }
            catch (ClipboardUnavailableException) { return true; } // busy clipboard: let the command report it
        }
    }

    /// <summary>
    /// Pastes annotations onto page <paramref name="pageIndex"/> (centered on <paramref name="center"/>, or keeping their
    /// place, spec 5.7, D14g) and selects them. An image or text is returned for the view to place; a failure is reported.
    /// </summary>
    public PasteContent Paste(int pageIndex, Point2? center)
    {
        if (Document is not { } doc) return PasteContent.Nothing;
        try
        {
            if (clipboard.ContainsData(AnnotationTransfer.Format))
            {
                var copied = AnnotationTransfer.Decode(clipboard.GetData(AnnotationTransfer.Format), checkImage);
                var target = doc.Session.Current.Pages[pageIndex].Id;
                var pasted = copied.Paste(doc.Session.Current, target, center, PdfNotes.Text.TextLayout.Bounds);
                doc.Session.Execute(pasted.Command);
                owner.ToolBar.SetTool(ToolKind.Select);
                Tool.SetSelection(pasted.Ids);
                return new(PasteKind.Annotations);
            }
            if (clipboard.ContainsImage() && clipboard.GetImagePng() is { } png) return new(PasteKind.Image, Png: png);
            if (clipboard.ContainsText()) return new(PasteKind.Text, Text: clipboard.GetText());
            return PasteContent.Nothing;
        }
        catch (Exception e) when (e is ClipboardUnavailableException or JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        { Warn("clipboard.pasteFailed"); return PasteContent.Nothing; }
    }

    /// <summary>Locks or unlocks annotations (one Undo unit, spec 5.8).</summary>
    public void SetLocked(IReadOnlyCollection<Guid> targets, bool locked)
    {
        if (Document is { } doc) doc.Session.Execute(DocumentEdits.SetLocked(doc.Session.Current, targets, locked));
    }

    /// <summary>Moves annotations in the page's stacking order (spec 5.9).</summary>
    public void ChangeOrder(Guid pageId, IReadOnlyCollection<Guid> targets, ZOrderChange order)
    {
        if (Document is { } doc) doc.Session.Execute(DocumentEdits.ChangeZOrder(doc.Session.Current, pageId, targets, order));
    }

    /// <summary>내 필기로 (MCP_LIVE_PLAN 5): the AI writing among <paramref name="targets"/> becomes the user's own; one Undo unit.</summary>
    public void Adopt(IReadOnlyCollection<Guid> targets)
    {
        if (Document is not { } doc || Authorship.Adopt(doc.Session.Current, targets) is not { } command) return;
        doc.Session.Execute(command);
        owner.Status = texts.Format("ai.adopted", ("count", command.Operations.Length));
    }

    private static readonly (AnnotationCommand Command, string Key, ToolKind Tool)[] QuickTools =
    [
        (AnnotationCommand.Tool, "tool.select", ToolKind.Select), (AnnotationCommand.Tool, "tool.lasso", ToolKind.Lasso),
        (AnnotationCommand.Tool, "tool.pen", ToolKind.Pen), (AnnotationCommand.Tool, "ribbon.highlighter", ToolKind.FreeHighlight),
        (AnnotationCommand.Tool, "tool.markup", ToolKind.Markup),
        (AnnotationCommand.Tool, "tool.strokeEraser", ToolKind.StrokeEraser), (AnnotationCommand.Tool, "tool.areaEraser", ToolKind.AreaEraser),
        (AnnotationCommand.Tool, "tool.line", ToolKind.Line), (AnnotationCommand.Tool, "tool.curve", ToolKind.Curve)
    ];

    /// <summary>
    /// The canvas menu for a right-click on page <paramref name="pageIndex"/> (spec 3.2, 5.6–5.9): on selected PDF text its
    /// copy and highlight; on an annotation (selecting it when it is unlocked and outside the selection) lock, clipboard
    /// and order commands; elsewhere a new text box, the quick tool switch, paste and converting other apps' annotations.
    /// On an unlocked table, <paramref name="pagePoint"/> (where the menu was opened) picks the cell its row and column
    /// commands act on (1.4 plan 1.3).
    /// </summary>
    public AnnotationMenu? Menu(int pageIndex, Guid? hit, bool onTextSelection, Point2? pagePoint = null)
    {
        if (Document is not { } doc) return null;
        var model = doc.Session.Current;
        Guid pageId = model.Pages[pageIndex].Id;
        var entries = new List<AnnotationMenuEntry>();
        if (hit is null && onTextSelection)
        {
            entries.Add(new(AnnotationCommand.CopyText, texts.Get("command.copy")));
            entries.Add(new(AnnotationCommand.HighlightText, texts.Get("command.highlight"), !owner.Display.NotesHidden));
            return new(entries, pageId, []);
        }
        if (hit is { } id && model.FindAnnotation(id)?.Annotation is { } annotation)
        {
            if (!annotation.Locked && !Tool.SelectedIds.Contains(id)) { owner.ToolBar.SetTool(ToolKind.Select); Tool.Select(id); }
            Guid[] targets = annotation.Locked ? [id] : [.. Tool.SelectedIds];
            bool unlocked = !annotation.Locked;
            if (annotation is TextBoxAnnotation) entries.Add(new(AnnotationCommand.SavedStyles, texts.Get("text.savedStyles"), unlocked));
            if (annotation is TextBoxAnnotation box && targets.Length == 1)
                entries.Add(new(AnnotationCommand.AutoWidth, texts.Get("textBox.autoWidth"), unlocked, Checked: box.AutoWidth));
            var cell = annotation is TableAnnotation { Locked: false } table && pagePoint is { } point ? CellNear(table, point) : null;
            if (cell is { } at)
            {
                var t = (TableAnnotation)annotation;
                entries.Add(new(AnnotationCommand.TableRowAbove, texts.Get("table.rowAbove")));
                entries.Add(new(AnnotationCommand.TableRowBelow, texts.Get("table.rowBelow")));
                entries.Add(new(AnnotationCommand.TableColumnLeft, texts.Get("table.columnLeft")));
                entries.Add(new(AnnotationCommand.TableColumnRight, texts.Get("table.columnRight")));
                entries.Add(new(AnnotationCommand.TableDeleteRow, texts.Get("table.deleteRow"), t.Rows > 1, SeparatorBefore: true));
                entries.Add(new(AnnotationCommand.TableDeleteColumn, texts.Get("table.deleteColumn"), t.Columns > 1));
            }
            entries.Add(annotation.Locked ? new(AnnotationCommand.Unlock, texts.Get("command.unlock"), SeparatorBefore: entries.Count > 0)
                : new(AnnotationCommand.Lock, texts.Get("command.lock"), SeparatorBefore: entries.Count > 0));
            entries.Add(new(AnnotationCommand.Copy, texts.Get("command.copy"), SeparatorBefore: true));
            entries.Add(new(AnnotationCommand.Cut, texts.Get("command.cut"), unlocked));
            entries.Add(new(AnnotationCommand.Paste, texts.Get("command.paste"), CanPaste));
            entries.Add(new(AnnotationCommand.Delete, texts.Get("command.delete"), unlocked));
            entries.Add(new(AnnotationCommand.Forward, texts.Get("order.forward"), unlocked, SeparatorBefore: true));
            entries.Add(new(AnnotationCommand.Backward, texts.Get("order.backward"), unlocked));
            entries.Add(new(AnnotationCommand.ToFront, texts.Get("order.toFront"), unlocked));
            entries.Add(new(AnnotationCommand.ToBack, texts.Get("order.toBack"), unlocked));
            if (targets.Any(t => Authorship.IsAi(model, t))) entries.Add(new(AnnotationCommand.Adopt, texts.Get("ai.adopt"), SeparatorBefore: true));
            return new(entries, pageId, targets, cell is { } c ? (annotation.Id, c.Row, c.Column) : null);
        }
        entries.Add(new(AnnotationCommand.AddTextBox, texts.Get("menu.addTextBox")));
        bool first = true;
        foreach (var (command, key, tool) in QuickTools)
        {
            entries.Add(new(command, texts.Get(key), SeparatorBefore: first, Tool: tool, Checked: owner.Tools.Current == tool));
            first = false;
        }
        entries.Add(new(AnnotationCommand.Paste, texts.Get("command.paste"), CanPaste, SeparatorBefore: true));
        if (owner.Foreign.Count > 0) entries.Add(new(AnnotationCommand.ConvertForeign, texts.Get("foreign.convertTip"), owner.Foreign.CanConvert)); // spec 14.6, D14g
        return new(entries, pageId, []);
    }

    /// <summary>Runs a menu command this view model owns; false for the ones the view runs (text box, paste, styles, conversion).</summary>
    public bool Run(AnnotationMenu menu, AnnotationMenuEntry entry)
    {
        switch (entry.Command)
        {
            case AnnotationCommand.CopyText: CopyText(); return true;
            case AnnotationCommand.HighlightText: HighlightText(); return true;
            case AnnotationCommand.Lock or AnnotationCommand.Unlock: SetLocked(menu.Targets, entry.Command == AnnotationCommand.Lock); return true;
            case AnnotationCommand.Copy: Copy(menu.Targets, menu.PageId); return true;
            case AnnotationCommand.Cut: Cut(menu.Targets, menu.PageId); return true;
            case AnnotationCommand.Delete: Delete(); return true;
            case AnnotationCommand.Forward: ChangeOrder(menu.PageId, menu.Targets, ZOrderChange.Forward); return true;
            case AnnotationCommand.Backward: ChangeOrder(menu.PageId, menu.Targets, ZOrderChange.Backward); return true;
            case AnnotationCommand.ToFront: ChangeOrder(menu.PageId, menu.Targets, ZOrderChange.ToFront); return true;
            case AnnotationCommand.ToBack: ChangeOrder(menu.PageId, menu.Targets, ZOrderChange.ToBack); return true;
            case AnnotationCommand.Adopt: Adopt(menu.Targets); return true;
            case AnnotationCommand.AutoWidth: SetAutoWidth(menu.Targets, !entry.Checked); return true;
            case AnnotationCommand.Tool when entry.Tool is { } tool: owner.ToolBar.SetTool(tool); return true;
            case AnnotationCommand.TableRowAbove or AnnotationCommand.TableRowBelow or AnnotationCommand.TableColumnLeft or AnnotationCommand.TableColumnRight
                or AnnotationCommand.TableDeleteRow or AnnotationCommand.TableDeleteColumn: EditTable(menu, entry.Command); return true;
            default: return false;
        }
    }

    /// <summary>
    /// 자동 너비 on (fit to the text at once) or off (fixed at the width it has) for one unlocked text box: one Undo unit
    /// (1.4 plan 1.1).
    /// </summary>
    public void SetAutoWidth(IReadOnlyCollection<Guid> targets, bool on)
    {
        if (Document is not { } doc || targets.Count != 1 ||
            doc.Session.Current.FindAnnotation(targets.First())?.Annotation is not TextBoxAnnotation { Locked: false } box || box.AutoWidth == on) return;
        var after = on ? TextStyleViewModel.FitOnPage(doc.Session.Current, box with { AutoWidth = true }) : box with { AutoWidth = false };
        doc.Session.SealCoalescing();
        doc.Session.Execute(DocumentEdits.UpdateAnnotations(doc.Session.Current, [after], "undo.autoWidth"));
        doc.Session.SealCoalescing();
    }

    /// <summary>The cell under a page point of a table; on the outer frame, the nearest cell.</summary>
    private static (int Row, int Column)? CellNear(TableAnnotation table, Point2 pagePoint)
    {
        var local = table.Transform.Invert().Transform(pagePoint);
        var grid = PdfNotes.Text.TableLayout.Measure(table);
        var inside = new Point2(Math.Clamp(local.X, 0, grid.ColumnX[^1]), Math.Clamp(local.Y, 0, grid.RowY[^1]));
        return PdfNotes.Text.TableLayout.CellAt(table, inside);
    }

    /// <summary>A row or column inserted next to the menu's cell, or its row or column removed: one Undo unit (1.4 plan 1.3).</summary>
    private void EditTable(AnnotationMenu menu, AnnotationCommand command)
    {
        if (Document is not { } doc || menu.Cell is not { } cell ||
            doc.Session.Current.FindAnnotation(cell.Table)?.Annotation is not TableAnnotation { Locked: false } table ||
            cell.Row >= table.Rows || cell.Column >= table.Columns) return;
        var (after, label) = command switch
        {
            AnnotationCommand.TableRowAbove => (TableEdits.InsertRow(table, cell.Row), "undo.tableRow"),
            AnnotationCommand.TableRowBelow => (TableEdits.InsertRow(table, cell.Row + 1), "undo.tableRow"),
            AnnotationCommand.TableColumnLeft => (TableEdits.InsertColumn(table, cell.Column), "undo.tableColumn"),
            AnnotationCommand.TableColumnRight => (TableEdits.InsertColumn(table, cell.Column + 1), "undo.tableColumn"),
            AnnotationCommand.TableDeleteRow => (TableEdits.DeleteRow(table, cell.Row), "undo.tableRow"),
            _ => (TableEdits.DeleteColumn(table, cell.Column), "undo.tableColumn")
        };
        if (after is null) return; // the last row or column: its entry is off
        doc.Session.SealCoalescing();
        doc.Session.Execute(DocumentEdits.UpdateAnnotations(doc.Session.Current, [after], label));
        doc.Session.SealCoalescing();
    }
}
