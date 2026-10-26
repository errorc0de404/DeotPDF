using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PdfNotes.App;
using PdfNotes.App.Canvas;
using PdfNotes.App.Themes;
using PdfNotes.App.Views;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Prototype;
using static PdfNotes.Probe.ProbeInput;

namespace PdfNotes.Probe;

/// <summary>
/// D14g <c>d14g-logic</c> (spec 2.0 〔D14g〕 items) without OS input: windows are not activated and stay off screen, tools
/// get their pointer events directly, dialogs are replaced. Checks the paste position (a point, another page, stepping
/// aside), dragging a selection to another page (setting, continuous view only, one Undo), where Undo/Redo shows its
/// change and its named tooltip, text-edit Undo staying in the box, the return-tool mark and the empty click that goes
/// back, Shift-drag rectangle selection, the lock cursor, hidden notes coming back with an editing action, favorites with a
/// highlighter kind, the fixed user colors, the export window (merged PDF preset, the open file refused), the other-app
/// annotation command, pages pasted from another window, the other window found by its marker and clearing backups.
/// </summary>
internal static class D14gCheck
{
    private static MainWindow window = null!;
    private static string folder = "";
    private static readonly List<object> checks = [];
    private static int failed;
    private static DocumentSession Session => window.Document.Session;
    private static DocumentView View => (DocumentView)window.FindName("View");

    public static int Run(string output)
    {
        folder = output;
        Directory.CreateDirectory(folder);
        Environment.SetEnvironmentVariable("PDFNOTES_SETTINGS_DIR", Path.Combine(folder, "settings"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                Theme.SystemThemeOverride = () => AppTheme.Light;
                await Steps();
                result = failed == 0 ? 0 : 1;
            }
            catch (Exception e) { Console.WriteLine(e); File.WriteAllText(Path.Combine(folder, "d14g-error.txt"), e.ToString()); }
            finally
            {
                File.WriteAllText(Path.Combine(folder, "d14g-logic-result.json"), JsonSerializer.Serialize(new
                { Passed = result == 0, Input = "none (windows not activated, off screen)", Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
                foreach (var w in app.Windows.OfType<MainWindow>().ToArray()) { w.PromptOverride = (_, _) => MessageBoxResult.No; w.Close(); }
                app.Shutdown();
            }
        };
        app.Run();
        return result;
    }

    private static T OffScreen<T>(T w) where T : Window
    {
        w.ShowActivated = false; w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = SystemParameters.VirtualScreenLeft - 6000; w.Top = SystemParameters.VirtualScreenTop;
        return w;
    }

    private static Point Dip(int page, Point2 p) { var d = View.PageTransform(page).PageToDip.Transform(p); return new(d.X, d.Y); }

    private static InkStroke Ink(double x, double y) => new(Guid.NewGuid(), false, Affine.Identity, [new(x, y), new(x + 40, y + 10)], 3, new(0, 0, 0xCC), 1);

    private static InkStroke Add(int page, InkStroke ink)
    {
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[page].Id, ink));
        return ink;
    }

    private static int PageOf(Guid id) => Session.Current.FindAnnotation(id) is { } at ? at.PageIndex : -1;

    private static Rect2 BoundsOf(Guid id) => AnnotationGeometry.Bounds([Session.Current.FindAnnotation(id)!.Annotation]);

    private static async Task Drag(params Point[] points)
    {
        var tool = View.Tool!;
        tool.PointerDown(Ptr(points[0]));
        foreach (var p in points.Skip(1)) tool.PointerMove(Ptr(p));
        tool.PointerUp(Ptr(points[^1]));
        await Settle();
    }

    private static async Task Steps()
    {
        var messages = new List<string>();
        MessageBoxResult answer = MessageBoxResult.Yes;
        window = OffScreen(new MainWindow());
        window.Width = 1200; window.Height = 860;
        window.MessageSink = messages.Add;
        window.PromptOverride = (_, _) => answer;
        window.Show();
        await Settle(300);
        Check("new note of two pages", await window.CreateNoteAsync(NewPageSpec.Default) && window.AddBlankPage(insertAfterActive: false, NewPageSpec.Default));
        await Settle(400);
        await Paste();
        await PageDrag();
        await UndoFeedback();
        await TextUndo();
        await Selection();
        await HiddenAndFavorites();
        await Export();
        await ForeignCommand();
        await Windows(messages);
    }

    // ----- spec 5.7: where a paste goes -----

    private static async Task Paste()
    {
        View.SetZoom(.4); View.GoToPage(0); await Settle(); // page 2 on screen too: a paste can point at it
        var ink = Add(0, Ink(100, 100));
        window.SetTool(ToolKind.Select); window.Selection.Select(ink.Id);
        Check("copy", window.CopySelection());
        Check("a paste at a point of another page centers the copy there", window.PasteSelection(Dip(1, new(300, 400))) && window.Selection.SelectedIds.Count == 1 &&
            PageOf(window.Selection.SelectedIds.First()) == 1 && BoundsOf(window.Selection.SelectedIds.First()) is var b && Math.Abs(b.X + b.Width / 2 - 300) < .01 &&
            Math.Abs(b.Y + b.Height / 2 - 400) < .01, $"{PageOf(window.Selection.SelectedIds.First())}");
        View.SetZoom(1); View.GoToPage(0); await Settle();
        var first = window.PasteSelection() ? window.Selection.SelectedIds.First() : Guid.Empty;
        var second = window.PasteSelection() ? window.Selection.SelectedIds.First() : Guid.Empty;
        var original = BoundsOf(ink.Id);
        Check("Ctrl+V away from the pages: the current page, 10pt aside, then 20pt past the first copy", PageOf(first) == 0 && PageOf(second) == 0 &&
            Math.Abs(BoundsOf(first).X - original.X - 10) < .01 && Math.Abs(BoundsOf(second).X - original.X - 20) < .01);
        Check("a paste is one Undo unit", Session.UndoLabel == "undo.paste");
    }

    // ----- spec 5.3: dragging to another page -----

    private static async Task PageDrag()
    {
        window.SetViewMode(ViewMode.Continuous);
        View.GoToPage(0); View.SetZoom(.5); await Settle(400);
        var ink = Add(0, Ink(200, 600));
        var on = Dip(0, new(220, 605));
        var target = Dip(1, new(220, 300));
        window.SetTool(ToolKind.Select); window.Selection.Select(ink.Id); await Settle();
        window.PageDragEnabled = false;
        await Drag(on, new(on.X, on.Y + 40), target);
        Check("setting off: a drag stays on its page", PageOf(ink.Id) == 0 && Session.UndoLabel == "undo.move");
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        window.PageDragEnabled = true;
        window.Selection.Select(ink.Id); await Settle();
        View.Tool!.PointerDown(Ptr(on));
        View.Tool.PointerMove(Ptr(target));
        Check("over another page the selection shows there", window.Selection.CrossPage is { PageIndex: 1 });
        View.Tool.PointerUp(Ptr(target)); await Settle();
        var moved = Session.Current.FindAnnotation(ink.Id);
        Check("setting on, continuous view: dropped on page 2 with its ID, selected, one Undo unit", moved is { PageIndex: 1 } && Session.UndoLabel == "undo.moveToPage" &&
            window.Selection.SelectedIds.SequenceEqual([ink.Id]) && Math.Abs(BoundsOf(ink.Id).X - AnnotationGeometry.Bounds([ink]).X) < 1, $"{moved?.PageIndex}");
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("Undo puts it back on page 1", PageOf(ink.Id) == 0);
        window.SetViewMode(ViewMode.SinglePage); await Settle(300);
        Check("one page at a time: no other page to drop on", View.HitTestPage(P2(target)) is not { PageIndex: 1 });
        window.SetViewMode(ViewMode.Continuous); View.SetZoom(1); await Settle(300);
        window.PageDragEnabled = false;
    }

    // ----- spec 13.3: where Undo/Redo happened -----

    private static async Task UndoFeedback()
    {
        View.GoToPage(1); await Settle(300);
        var ink = Add(1, Ink(150, 700));
        Session.Execute(DocumentEdits.DeleteAnnotations(Session.Current, [ink.Id]));
        window.SetTool(ToolKind.Pen);
        View.GoToPage(0); await Settle(300);
        var undoButton = (Button)window.FindName("UndoButton");
        Check("the undo button names the step", undoButton.ToolTip as string is { } tip && tip.Contains("실행 취소: ") && tip.Contains("Ctrl+Z"), undoButton.ToolTip?.ToString() ?? "");
        ApplicationCommands.Undo.Execute(null, window); await Settle(300);
        Check("Undo off screen goes there and selects what came back", View.CurrentPageIndex == 1 && window.Selection.SelectedIds.SequenceEqual([ink.Id]) &&
            window.CurrentTool == ToolKind.Select, $"page {View.CurrentPageIndex}");
        Check("the pen is marked as the tool to go back to", window.ReturnTool == ToolKind.Pen && ToolMarks.GetIsReturnTarget((ToggleButton)window.FindName("PenToolButton")));
        int count = Session.Current.AllAnnotations.Count();
        await Drag(Dip(1, new(400, 100)), Dip(1, new(400.5, 100)));
        Check("a click on an empty place clears the selection and goes back to the pen without drawing", window.CurrentTool == ToolKind.Pen &&
            Session.Current.AllAnnotations.Count() == count && !ToolMarks.GetIsReturnTarget((ToggleButton)window.FindName("PenToolButton")));
    }

    // ----- spec 13.2: Undo while editing text stays in the box -----

    private static async Task TextUndo()
    {
        View.GoToPage(0); await Settle();
        var box = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Translation(80, 300), "abc", [TextEditing.Default with { Length = 3 }], PdfNotes.Core.Models.TextAlignment.Left, 200, TextBoxStyle.Default, []);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, box));
        var ink = Add(0, Ink(300, 500));
        window.BeginTextEdit(box.Id); await Settle();
        var editor = window.ActiveTextEditor!;
        editor.CaretPosition = editor.Document.ContentEnd.GetInsertionPosition(System.Windows.Documents.LogicalDirection.Backward);
        editor.CaretPosition.InsertTextInRun("X"); await Settle();
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("Undo while editing takes back the typing", window.ActiveTextEditor is not null && ((TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation).Text == "abc");
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("…and then nothing: the stroke drawn before stays (spec 13.2)", Session.Current.FindAnnotation(ink.Id) is not null && window.ActiveTextEditor is not null);
        window.FinishTextEditing(); await Settle();
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("after editing, Undo is the document's again", Session.Current.FindAnnotation(ink.Id) is null);
    }

    // ----- spec 5.2–5.3: rectangle selection, lock cursor -----

    private static async Task Selection()
    {
        View.GoToPage(0); await Settle();
        var a = Add(0, Ink(100, 760));
        var b = Add(0, Ink(200, 760));
        window.SetTool(ToolKind.Select); window.Selection.Select(null); await Settle();
        var tool = View.Tool!;
        tool.PointerDown(Ptr(Dip(0, new(90, 740)), ModifierKeys.Shift));
        tool.PointerMove(Ptr(Dip(0, new(250, 790)), ModifierKeys.Shift));
        tool.PointerUp(Ptr(Dip(0, new(250, 790)), ModifierKeys.Shift)); await Settle();
        Check("Shift + drag from an empty place selects what the rectangle touches", window.Selection.SelectedIds.ToHashSet().SetEquals([a.Id, b.Id]));
        Session.Execute(DocumentEdits.SetLocked(Session.Current, [a.Id], true));
        window.Selection.Select(null);
        ((IHoverTool)window.Selection).PointerHover(Dip(0, new(120, 765)));
        Check("the lock cursor over a locked annotation", window.Selection.Cursor?.Shape == CursorShape.Lock);
        window.SetTool(ToolKind.AreaEraser);
        ((IHoverTool)View.Tool!).PointerHover(Dip(0, new(120, 765)));
        Check("…also with the eraser", View.Tool!.Cursor?.Shape == CursorShape.Lock);
        ((IHoverTool)View.Tool!).PointerHover(Dip(0, new(400, 100)));
        Check("a cross elsewhere", View.Tool!.Cursor?.Shape == CursorShape.Cross);
    }

    // ----- spec 24.10, 24.7, 3.4 -----

    private static async Task HiddenAndFavorites()
    {
        window.SetNotesHidden(true); await Settle();
        window.SetTool(ToolKind.Pen); await Settle();
        Check("hidden notes come back when a drawing tool is chosen", !window.NotesHidden && window.CurrentTool == ToolKind.Pen);
        window.SetNotesHidden(true); await Settle();
        Check("…and with a favorite", window.Favorites[0] is not null);
        window.ApplyFavorite(0); await Settle();
        Check("…the favorite applies", !window.NotesHidden && window.CurrentTool == ToolKind.Pen);
        window.SetTool(ToolKind.FreeHighlight); window.SetMarkKind(TextMarkupKind.Underline); await Settle();
        window.StoreFavorite(3); window.SetMarkKind(null); window.ApplyFavorite(0); window.ApplyFavorite(3); await Settle();
        Check("a highlighter favorite keeps its kind (underline)", window.CurrentTool == ToolKind.FreeHighlight && window.MarkKind == TextMarkupKind.Underline &&
            window.Favorites[3] is { MarkKind: TextMarkupKind.Underline });
        var slot = (Button)((Panel)window.FindName("FavoritesPanel")).Children[0];
        Check("favorite tooltips name their shortcut", slot.ToolTip as string is { } tip && tip.Contains("Ctrl+1"), slot.ToolTip?.ToString() ?? "");
        window.SetMarkKind(null);
        for (byte i = 1; i <= 14; i++) window.SetColor(new(i, 0x31, 0x77));
        Check("user colors: a fixed list of 12, the first ones stay", window.RecentColors.Count == 12 && window.RecentColors[0] == new RgbColor(1, 0x31, 0x77));
        window.RemoveUserColor(window.RecentColors[0]);
        window.SetColor(new(99, 0x31, 0x77));
        Check("removing one makes room at the end", window.RecentColors[^1] == new RgbColor(99, 0x31, 0x77) && window.RecentColors.Count == 12);
    }

    // ----- spec 11.10, 14.5: one export window; never onto the open file -----

    private static async Task Export()
    {
        string own = Path.Combine(folder, "note.pdf");
        window.SaveDialogOverride = _ => own;
        Check("save the note", await window.SaveAsync(saveAs: false));
        PageExportDialog? seen = null;
        window.ExportDialogOverride = d => { seen = d; return true; };
        int files = await window.ExportPagesAsync(flatten: true);
        Check("the merged PDF command opens the export window preset: 필기 합친 PDF, every page", seen is { Flatten: true } && seen.RangeBox.Text == PageRanges.All);
        Check("the open document's own file is refused", files == 0);
        string other = Path.Combine(folder, "note_flattened.pdf");
        window.SaveDialogOverride = _ => other;
        files = await window.ExportPagesAsync(flatten: true);
        Check("another file is written", files == 1 && File.Exists(other));
        window.SaveDialogOverride = null; window.ExportDialogOverride = null;
    }

    // ----- spec 14.6: the other-app annotation command -----

    private static async Task ForeignCommand()
    {
        string pdf = Path.Combine(folder, "foreign.pdf");
        ForeignAnnotationSample.Create(pdf);
        Check("open a PDF with other apps' annotations", await window.OpenFileAsync(pdf));
        for (int i = 0; i < 100 && window.ForeignAnnotationCount == 0; i++) await Task.Delay(50);
        var button = (Button)window.FindName("ForeignEditButton");
        Check("counted, the command is offered", window.ForeignAnnotationCount == ForeignAnnotationSample.ConvertiblePdfAnnotations + 1 && button.Visibility == Visibility.Visible && button.IsEnabled);
        int made = await window.ConvertForeignAsync();
        Check("the command converts them as one Undo unit", made == window.ForeignAnnotationCount && Session.UndoLabel == "undo.convertForeign" && Session.IsDirty && !button.IsEnabled);
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("Undo: read-only again, the command back", !Session.Current.AllAnnotations.Any() && button.IsEnabled);
    }

    // ----- spec 4.6, 11.4, 18.2: several windows, backups -----

    private static async Task Windows(List<string> messages)
    {
        string a = Path.Combine(folder, "pages-a.pdf");
        ProbeSamplePdf.Create(a, 2);
        Check("window A opens a PDF", await window.OpenFileAsync(a));
        await Settle(400);
        Add(0, Ink(120, 130));
        var b = OffScreen(new MainWindow());
        b.Width = 1000; b.Height = 700; b.MessageSink = messages.Add; b.PromptOverride = (_, _) => MessageBoxResult.No;
        b.Show(); await Settle(600);
        string appFolder = Path.Combine(folder, "settings");
        long handleA = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        Check("the other window finds A by the file it shows", PdfNotes.Editor.Services.BackupStore.WindowShowing(appFolder, a, b.Backups) == handleA);
        var list = (ListBox)window.FindName("PageList");
        list.SelectedItems.Clear(); list.SelectedItems.Add(window.PageThumbnails[0]);
        Check("A copies its first page", window.CopySelectedPages());
        // No wait: B pastes at once and waits itself while A still writes the PDF.
        int before
 = b.Document.Session.Current.Pages.Count;
        Check("B pastes A's page", b.PastePages() && b.LastForeignPagePaste is { } paste && await paste);
        await Settle(300);
        var pages = b.Document.Session.Current.Pages;
        Check("…after its active page, with A's notes (new IDs), as one Undo unit", pages.Count == before + 1 && pages[1].Annotations.OfType<InkStroke>().Count() == 1 &&
            b.Document.Session.UndoCount == 1, $"{pages.Count} pages");
        Check("A still pastes its own copy (same document)", window.PastePages() && window.Document.Session.Current.Pages.Count == 3);
        b.Close();

        // Backups: usage and 백업 비우기 (spec 18.2).
        Check("a backup", await window.RunBackupAsync());
        Check("usage counts it", window.Backups.Usage().Count >= 1);
        Check("백업 비우기 asks, then deletes the app's backups", window.ClearBackups() && window.Backups.Usage().Count == 0);
    }

    private static void Check(string name, bool passed, string detail = "")
    {
        checks.Add(new { Name = name, Passed = passed, Detail = detail });
        if (!passed) failed++;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        File.AppendAllText(Path.Combine(folder, "progress.txt"), $"{(passed ? "PASS" : "FAIL")} {name}: {detail}{Environment.NewLine}"); // survives a hang
    }

    private static Task Settle(int ms = 250) => Task.Delay(ms);
}
