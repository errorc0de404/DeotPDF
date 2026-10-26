using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.App;
using PdfNotes.App.Canvas;
using PdfNotes.App.Tools;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Probe;

/// <summary>
/// D11 real-app checks with OS mouse/keyboard input. <c>d11a</c>: double-click highlight transparency, return to the
/// previous tool after an automatic switch to selection, rotation snapping, selection frame hit area and cursors.
/// </summary>
internal static class D11Check
{
    private static MainWindow window = null!;
    private static string folder = "";
    private static readonly List<object> checks = [];
    private static readonly List<string> messages = [];
    private static DocumentSession Session => window.Document.Session;
    private static DocumentView View => (DocumentView)window.FindName("View");
    private static int page;
    private static PdfTextHighlight[] Highlights => Session.Current.AllAnnotations.OfType<PdfTextHighlight>().ToArray();

    private static void Check(string name, bool passed, string detail = "")
    {
        checks.Add(new { Name = name, Passed = passed, Detail = detail });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        if (!passed) throw new InvalidOperationException(name + ": " + detail);
    }

    public static int Run(string output, string part)
    {
        folder = output;
        Environment.SetEnvironmentVariable("PDFNOTES_SETTINGS_DIR", Path.Combine(folder, "settings"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                window = new MainWindow { Width = 1420, Height = 900, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                window.MessageSink = messages.Add; window.PromptOverride = (_, _) => MessageBoxResult.No;
                window.Show(); await Settle();
                if (!part.EndsWith("-reopen", StringComparison.Ordinal)) await Foreground();
                if (part == "d11a") await PartA();
                else if (part == "d11b") await PartB();
                else if (part == "d11c") await PartC();
                else if (part == "d11d") await PartD();
                else if (part == "d11e") await PartE();
                else if (part == "d11e-reopen") await PartEReopen();
                else if (part == "d11f") await PartF();
                else if (part == "d11f-reopen") await PartFReopen();
                result = 0;
            }
            catch (Exception e) { checks.Add(new { Name = "harness", Passed = false, Detail = e.ToString() }); Console.WriteLine(e); }
            finally
            {
                File.WriteAllText(Path.Combine(folder, part + "-result.json"), JsonSerializer.Serialize(new { Passed = result == 0, Input = "os-mouse", Checks = checks, Messages = messages },
                    new JsonSerializerOptions { WriteIndented = true }));
                window.PromptOverride = (_, _) => MessageBoxResult.No; window.Close(); app.Shutdown();
            }
        };
        app.Run();
        if (part is "d11e" or "d11f" && result == 0)
        {
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(part + "-reopen"); start.ArgumentList.Add(folder);
            using var child = System.Diagnostics.Process.Start(start)!; child.WaitForExit(); result = child.ExitCode;
        }
        return result;
    }

    private static async Task PartA()
    {
        string pdf = Path.Combine(folder, "source.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        Check("open sample PDF", await window.OpenFileAsync(pdf));
        await ShowPage(0);
        var glyphs = window.PageGlyphs(0);

        // ----- D09 leftover 1: a double-clicked word uses the tool transparency (60% → opacity 0.4) -----
        window.SetTool(ToolKind.FreeHighlight); await Settle();
        int word = Find(glyphs, "English");
        await DoubleClick(Edge(glyphs[word + 2], false));
        var made = Highlights.LastOrDefault();
        Check("double-click word highlight opacity equals the tool transparency", made is { SourceTextHint: "English" } && Math.Abs(made.Opacity - .4) < 1e-9,
            made is null ? "none" : $"'{made.SourceTextHint}' opacity {made.Opacity}, count {Highlights.Length}, tool {window.CurrentTool}");
        // The user's report: clicking on after a double-click (click count 3, 4, …) stacked copies until the word vanished.
        window.SetTransparency(44); await Settle();
        int other = Find(glyphs, "Original");
        int undo = Session.UndoCount;
        await MultiClick(Edge(glyphs[other + 3], false), 5);
        var originals = Highlights.Where(h => h.SourceTextHint == "Original").ToArray();
        Check("five fast clicks on a word make one highlight with the tool transparency", originals is [{ } single] && Math.Abs(single.Opacity - .56) < 1e-9 &&
            Session.UndoCount == undo + 1, $"{originals.Length} highlights, undo +{Session.UndoCount - undo}");
        var q = originals[0].Quads[0];
        var under = WindowColor(new((q.UpperLeft.X + q.UpperRight.X) / 2, q.UpperLeft.Y + 1));
        Check("one translucent layer is drawn (not a stack)", under.B is > 80 and < 160, $"{under}"); // one 44% layer of yellow on white: B ≈ 112
        await Task.Delay(600); // a new click run
        undo = Session.UndoCount;
        int digits = Find(glyphs, "123");
        await DoubleClick(Edge(glyphs[digits + 1], false));
        await Task.Delay(600);
        await DoubleClick(Edge(glyphs[digits + 1], false)); // the first click selects it (existing highlight), the second does nothing
        Check("double-click again on a highlighted word does not stack a copy", Highlights.Count(h => h.SourceTextHint == "123") == 1 && Session.UndoCount == undo + 1,
            $"{Highlights.Count(h => h.SourceTextHint == "123")}, undo +{Session.UndoCount - undo}, tool {window.CurrentTool}");
        Snapshot("d11a-highlights.png");

        // ----- D09 leftover 2: back to the highlight tool after its automatic switch to selection -----
        var english = Highlights.Single(h => h.SourceTextHint == "English");
        window.SetTool(ToolKind.FreeHighlight); await Settle();
        var onEnglish = Edge(glyphs[word + 1], false);
        await ClickPage(onEnglish);
        Check("click on a highlight switches to selection and remembers the highlighter", window.CurrentTool == ToolKind.Select &&
            window.Selection.SelectedIds.SequenceEqual([english.Id]) && window.ReturnTool == ToolKind.FreeHighlight, $"tool {window.CurrentTool}, return {window.ReturnTool}");
        await Key(0x1B); // Esc
        Check("Esc clears it and goes back to the highlighter", window.CurrentTool == ToolKind.FreeHighlight && window.Selection.SelectedIds.Count == 0);
        // D14g (spec 5.4): a style change keeps the selection for more editing; the selection emptying goes back.
        await ClickPage(onEnglish);
        window.SetColor(new(0x43, 0xA0, 0x47)); await Settle();
        Check("a style change keeps the highlight selected", window.CurrentTool == ToolKind.Select && window.Selection.SelectedIds.SequenceEqual([english.Id]) &&
            Highlights.Single(h => h.Id == english.Id).Color == new RgbColor(0x43, 0xA0, 0x47));
        window.SetTransparency(30, continuous: true); await Settle();
        Check("a slider drag in progress stays in selection", window.CurrentTool == ToolKind.Select);
        window.CommitStyleChange(); await Settle();
        Check("the committed slider change keeps the selection too", window.CurrentTool == ToolKind.Select && Math.Abs(Highlights.Single(h => h.Id == english.Id).Opacity - .7) < 1e-9);
        int annotationsBefore = Session.Current.AllAnnotations.Count();
        await ClickPage(new(500, 500)); // on screen (760 is below the view)
        Check("a click on an empty place goes back to the highlighter and draws nothing", window.CurrentTool == ToolKind.FreeHighlight &&
            window.Selection.SelectedIds.Count == 0 && Session.Current.AllAnnotations.Count() == annotationsBefore, $"tool {window.CurrentTool}");
        await ClickPage(onEnglish);
        await Key(0x2E); // Delete
        Check("Delete removes it and goes back", window.CurrentTool == ToolKind.FreeHighlight && Highlights.All(h => h.Id != english.Id));
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        await ClickPage(onEnglish);
        await ClickButton("SelectToolButton");
        Check("choosing the selection tool directly forgets the return", window.CurrentTool == ToolKind.Select && window.ReturnTool is null);
        await Key(0x1B);
        Check("then Esc stays in selection", window.CurrentTool == ToolKind.Select);

        // ----- lasso → selection → back, and a selection tool chosen directly -----
        Check("new note", await window.CreateNoteAsync(NewPageSpec.Default));
        await ShowPage(0);
        var a = Stroke([new(100, 100), new(160, 100)]);
        var b = Stroke([new(100, 200), new(160, 200)]);
        var square = Stroke([new(300, 300), new(400, 300), new(400, 400), new(300, 400), new(300, 300)]);
        window.SetTool(ToolKind.Lasso); await Settle();
        await Drag(new(80, 80), new(180, 80), new(180, 120), new(80, 120));
        Check("lasso selects and switches to selection", window.CurrentTool == ToolKind.Select && window.Selection.SelectedIds.SequenceEqual([a.Id]) &&
            window.ReturnTool == ToolKind.Lasso);
        undo = Session.UndoCount;
        await Drag(new(130, 100), new(140, 105), new(150, 110));
        Check("a finished move keeps the selection in the selection tool (spec 5.4, D14g)", window.CurrentTool == ToolKind.Select && Session.UndoCount == undo + 1 &&
            window.Selection.SelectedIds.SequenceEqual([a.Id]) && Moved(a, 20, 10) && window.ReturnTool == ToolKind.Lasso, $"tool {window.CurrentTool}");
        await ClickPage(new(500, 500));
        Check("a click on an empty place goes back to the lasso", window.CurrentTool == ToolKind.Lasso && window.Selection.SelectedIds.Count == 0 && Session.UndoCount == undo + 1);
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        window.Selection.Select(null);
        await Drag(new(80, 80), new(180, 80), new(180, 120), new(80, 120));
        await Key(0x1B);
        Check("Esc after a lasso goes back to the lasso", window.CurrentTool == ToolKind.Lasso && window.Selection.SelectedIds.Count == 0);
        window.SetTool(ToolKind.Select); await Settle();
        await Drag(new(130, 100), new(140, 105), new(150, 110));
        Check("a selection tool chosen directly stays after a move", window.CurrentTool == ToolKind.Select && window.ReturnTool is null && Moved(a, 20, 10));
        ApplicationCommands.Undo.Execute(null, window); await Settle();

        // ----- selection frame: inside and a few pixels around it move; farther out pans (spec 5.3) -----
        window.Selection.SetSelection([a.Id, b.Id]); await Settle();
        var corners = FrameDip();
        var middle = new Point((corners[0].X + corners[2].X) / 2, (corners[0].Y + corners[2].Y) / 2);
        await HoverDip(middle);
        Check("move cursor inside the selection frame", View.Cursor == Cursors.SizeAll, $"{View.Cursor}");
        undo = Session.UndoCount;
        await DragDip([middle, new(middle.X + 10, middle.Y), new(middle.X + 20, middle.Y)]);
        double d20 = View.PageTransform(0).DipLengthToPoints(20);
        Check("a press on the blank inside of the frame moves the selection", Session.UndoCount == undo + 1 && Moved(a, d20, 0) && Moved(b, d20, 0), $"undo +{Session.UndoCount - undo}");
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        corners = FrameDip();
        var nearBorder = new Point(corners[0].X - 4, middle.Y);
        await HoverDip(nearBorder);
        Check("move cursor just outside the frame border", View.Cursor == Cursors.SizeAll, $"{View.Cursor}");
        undo = Session.UndoCount;
        await DragDip([nearBorder, new(nearBorder.X, nearBorder.Y + 10), new(nearBorder.X, nearBorder.Y + 20)]);
        Check("a press 4 px outside the border still moves it", Session.UndoCount == undo + 1 && Moved(a, 0, d20) && Moved(b, 0, d20), $"undo +{Session.UndoCount - undo}");
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        corners = FrameDip();
        var far = new Point(corners[0].X - 14, middle.Y);
        await HoverDip(far);
        Check("no move cursor farther away", View.Cursor != Cursors.SizeAll, $"{View.Cursor}");
        undo = Session.UndoCount;
        await DragDip([far, new(far.X, far.Y + 20)]);
        Check("a press 14 px outside pans and clears the selection instead", Session.UndoCount == undo && window.Selection.SelectedIds.Count == 0 && Moved(a, 0, 0));

        // ----- handle cursors and right-angle rotation snapping -----
        window.Selection.Select(square.Id); await Settle();
        var handles = window.Selection.HandlePoints(View.PageTransform(0));
        await HoverDip(new(handles[0].X, handles[0].Y));
        Check("top-left handle shows the diagonal resize cursor", View.Cursor == Cursors.SizeNWSE, $"{View.Cursor}");
        await HoverDip(new(handles[1].X, handles[1].Y));
        Check("top-right handle shows the other diagonal", View.Cursor == Cursors.SizeNESW, $"{View.Cursor}");
        await HoverDip(new(handles[4].X, handles[4].Y));
        Check("rotation handle shows the rotation cursor", View.Cursor == SelectionCursors.RotateFor(View.DpiScale), $"{View.Cursor}");
        Check("88 degrees snaps to 90", await RotateBy(square, 88, snapped: 90));
        Check("92 degrees snaps to 90", await RotateBy(square, 92, snapped: 90));
        Check("86 degrees stays free", await RotateBy(square, 86));
        Check("Alt turns snapping off at 88 degrees", await RotateBy(square, 88, alt: true));
        Check("Shift keeps 15 degree steps (50 → 45)", await RotateBy(square, 50, shift: true, snapped: 45));
        Check("-2 degrees snaps back to 0", await RotateBy(square, -2, snapped: 0, expectChange: false));
        Snapshot("d11a-frame.png");

        // ----- tool settings and view mode are not document state (D11 task 9) -----
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, a with { Id = Guid.NewGuid() }));
        Check("save", await window.SaveToAsync(Path.Combine(folder, "d11a.pdf")) && !Session.IsDirty);
        undo = Session.UndoCount;
        window.SetTool(ToolKind.Pen); window.SetWidth(7); window.SetTransparency(20); window.SetColor(new(0x12, 0x34, 0x56)); window.CommitStyleChange();
        window.SetTool(ToolKind.AreaEraser); window.SetEraserSize(33);
        window.SetViewMode(ViewMode.SinglePage); window.SetViewMode(ViewMode.Continuous);
        window.SetTool(ToolKind.FreeHighlight); window.SetHighlightAuto(false); window.SetHighlightAuto(true); window.SetMarkKind(TextMarkupKind.Underline); window.SetMarkKind(null);
        await Settle();
        Check("tool settings, eraser size and view mode keep the document clean and out of Undo", !Session.IsDirty && Session.UndoCount == undo && !window.Title.Contains('*'));
    }

    // ----- D11b: search (spec 12) -----

    private static async Task PartB()
    {
        string pdf = Path.Combine(folder, "source.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        Check("open sample PDF", await window.OpenFileAsync(pdf));
        await ShowPage(0);
        var box = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Translation(80, 400), "Box english note",
            [new TextRun(0, 16, "Malgun Gothic", 18, RgbColor.Black, false, false, false, false)], PdfNotes.Core.Models.TextAlignment.Left, 260, TextBoxStyle.Default, []);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, box));
        var pageIds = Session.Current.Pages.Select(p => p.Id).ToArray();
        int undo = Session.UndoCount;

        await Key(0x46, control: true); // Ctrl+F
        var searchBox = (System.Windows.Controls.TextBox)window.FindName("SearchBox");
        Check("Ctrl+F opens the search bar with the cursor in it", window.IsSearchOpen && searchBox.IsKeyboardFocusWithin);
        await TypeText("English");
        await SearchDone();
        var hits = window.SearchHits;
        Check("PDF text of every page and the text box are found", hits.Count == 4 && hits.Count(h => h.BoxId == box.Id) == 1 &&
            hits.Where(h => h.BoxId is null).Select(h => h.PageId).SequenceEqual(pageIds), $"{hits.Count} hits");
        Check("results are in page order, inside the page by position (PDF line above the box)", hits[0].PageId == pageIds[0] && hits[0].BoxId is null && hits[1].BoxId == box.Id);
        var count = (System.Windows.Controls.TextBlock)window.FindName("SearchCount");
        Check("count shows the current result", count.Text == "1/4" && window.SearchIndex == 0, count.Text);
        Snapshot("d11b-search.png");
        await Key(0x0D); // Enter
        Check("Enter goes to the next result", window.SearchIndex == 1 && count.Text == "2/4");
        await Key(0x0D, shift: true);
        Check("Shift+Enter goes back", window.SearchIndex == 0);
        await Key(0x0D); await Key(0x0D); await Key(0x0D);
        var third = window.CurrentSearchHit!;
        Check("a result on another page is scrolled into view", third.PageId == pageIds[2] && View.CurrentPageIndex == 2 && InView(2, third.Quads), $"view page {View.CurrentPageIndex}");
        await Key(0x0D);
        Check("next wraps around to the first result", window.SearchIndex == 0);
        await Key(0x0D, shift: true);
        Check("previous wraps around to the last result", window.SearchIndex == 3);
        Check("searching changed nothing in the document", Session.UndoCount == undo && Session.IsDirty);

        // ----- focus: Delete and Ctrl+C in the search box belong to the box -----
        ((System.Windows.Controls.ListBox)window.FindName("PageList")).SelectedIndex = 1;
        searchBox.Focus(); searchBox.SelectAll(); await Settle();
        Clipboard.Clear();
        await Key(0x43, control: true); // Ctrl+C
        Check("Ctrl+C in the search box copies its text, not a page", Clipboard.ContainsText() && Clipboard.GetText() == "English" && !window.HasPageClipboard);
        searchBox.CaretIndex = 0; searchBox.SelectionLength = 0; await Settle();
        await Key(0x2E); // Delete
        Check("Delete in the search box deletes a letter, not the selected page", searchBox.Text == "nglish" && Session.Current.Pages.Count == 3);
        await Key(0x5A, control: true); // Ctrl+Z
        Check("Ctrl+Z in the search box undoes typing, not the document", Session.UndoCount == undo && searchBox.Text == "English", searchBox.Text);
        await SearchDone();
        int firstInView = window.SearchHits.Select((h, i) => (h, i)).First(x => Session.Current.IndexOfPage(x.h.PageId) >= View.CurrentPageIndex).i;
        Check("a new query starts at the current page of the status bar", window.SearchIndex == firstInView, $"index {window.SearchIndex}, page {View.CurrentPageIndex + 1}");
        for (int i = 0; i < 4 && window.CurrentSearchHit?.PageId != pageIds[2]; i++) await Key(0x0D);
        Check("on the page 3 result", window.CurrentSearchHit?.PageId == pageIds[2]);

        // ----- document changes: the current result keeps pointing at its own page -----
        Session.Execute(DocumentEdits.DeletePages(Session.Current, [pageIds[1]]));
        await SearchDone();
        Check("deleting another page keeps the current result on its page", window.SearchHits.Count == 3 && window.CurrentSearchHit?.PageId == pageIds[2] && count.Text == "3/3", count.Text);
        Session.Execute(PageEdits.MovePages(Session.Current, [pageIds[2]], 0));
        await SearchDone();
        Check("moving pages keeps it too and renumbers it", window.CurrentSearchHit?.PageId == pageIds[2] && window.SearchIndex == 0 && count.Text == "1/3", count.Text);
        Session.Execute(DocumentEdits.DeletePages(Session.Current, [pageIds[2]]));
        await SearchDone();
        Check("deleting the page of the current result moves to the next result", window.CurrentSearchHit?.PageId == pageIds[0] && window.SearchHits.Count == 2);
        await Key(0x0D);
        Check("the box result", window.CurrentSearchHit?.BoxId == box.Id);
        var edited = (TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation;
        Session.Execute(DocumentEdits.UpdateAnnotations(Session.Current, [edited with { Text = "Box note", Runs = [edited.Runs[0] with { Length = 8 }] }], "텍스트"));
        await SearchDone();
        Check("editing the box text removes its result", window.SearchHits.Count == 1 && window.SearchHits.All(h => h.BoxId is null) && window.SearchIndex == 0);
        Session.Undo(); await SearchDone();
        Check("undo brings it back", window.SearchHits.Count == 2);

        // ----- page transforms (D10): positions follow, cut text disappears and comes back -----
        var before = window.SearchHits[0].Quads[0];
        Session.Execute(PageTransforms.Shrink(Session.Current, [pageIds[0]], 50, alignment: 0));
        await SearchDone();
        var shrunk = window.SearchHits.First(h => h.BoxId is null).Quads[0];
        Check("shrunk content moves its result", Math.Abs(shrunk.UpperLeft.X - before.UpperLeft.X * .5) < .5 && Math.Abs(shrunk.UpperLeft.Y - before.UpperLeft.Y * .5) < .5,
            $"{before.UpperLeft} → {shrunk.UpperLeft}");
        Session.Undo(); await SearchDone();
        Session.Execute(PageTransforms.Resize(Session.Current, [pageIds[0]], 30, 30, PageResizeMode.ClipContent, alignment: 0));
        await SearchDone();
        Check("text cut off the new paper is not found", window.SearchHits.Count == 0 && count.Text == "0/0", count.Text);
        Session.Undo(); await SearchDone();
        Check("restored paper finds it again", window.SearchHits.Count == 2);

        // ----- the marks are temporary: not in the document, not in the saved PDF -----
        await ShowPage(0);
        var mark = window.SearchHits[0].Quads[0];
        var shown = WindowColor(new((mark.UpperLeft.X + mark.UpperRight.X) / 2, mark.UpperLeft.Y + 1));
        Check("the current result is marked on screen", shown.R > 200 && shown.B < 150, $"{shown}");
        string saved = Path.Combine(folder, "searched.pdf");
        Check("save with the search open", await window.SaveToAsync(saved));
        using (var engine = new PdfNotes.Pdf.Engine.PdfiumDocument(File.ReadAllBytes(saved)))
        {
            var bitmap = engine.Render(0, 1);
            PdfNotes.App.Prototype.ProbeFactory.SavePng(bitmap, Path.Combine(folder, "searched-page.png"));
            int x = (int)Math.Round((mark.UpperLeft.X + mark.UpperRight.X) / 2), y = (int)Math.Round(mark.UpperLeft.Y + 1);
            int at = (y * bitmap.Width + x) * 4;
            Check("the saved PDF has no search mark", !(bitmap.Bgra[at + 2] > 200 && bitmap.Bgra[at] < 150 && bitmap.Bgra[at + 1] < 200),
                $"BGR {bitmap.Bgra[at]},{bitmap.Bgra[at + 1]},{bitmap.Bgra[at + 2]}");
        }
        Check("the document holds no search data", Session.Current.AllAnnotations.Count() == 1);
        searchBox.Focus(); await Key(0x1B); // Esc
        Check("Esc closes the bar and clears the marks", !window.IsSearchOpen && window.SearchHits.Count == 0 && View.IsKeyboardFocusWithin);
    }

    // ----- D11c: quick menu, shortcuts by focus, mixed selection (spec 5.6, 17, 3.1, 9.4) -----

    private static async Task PartC()
    {
        string pdf = Path.Combine(folder, "source.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        Check("open sample PDF", await window.OpenFileAsync(pdf));
        await ShowPage(0, .6); // the whole page in view
        var red = new RgbColor(0xE5, 0x39, 0x35);
        var a = Stroke([new(120, 420), new(220, 420)]);
        var b = Stroke([new(120, 470), new(220, 480)]);
        var recolored = (InkStroke)Session.Current.FindAnnotation(b.Id)!.Annotation with { Color = red, WidthPt = 6 };
        Session.Execute(DocumentEdits.UpdateAnnotations(Session.Current, [recolored]));
        var lockedStroke = Stroke([new(300, 600), new(400, 600)]);
        Session.Execute(DocumentEdits.SetLocked(Session.Current, [lockedStroke.Id], true));
        Check("save the start state", await window.SaveToAsync(Path.Combine(folder, "start.pdf")) && !Session.IsDirty);

        // ----- quick menu on blank page area (spec 5.6, A32) by a real right-click -----
        window.SetTool(ToolKind.Pen); await Settle();
        var blank = new Point2(400, 700);
        int undo = Session.UndoCount;
        var menu = await RightClick(blank);
        var items = menu?.Items.OfType<System.Windows.Controls.MenuItem>().Where(i => i.Header is string).ToDictionary(i => (string)i.Header) ?? [];
        string[] tools = ["선택", "올가미", "그리기 펜", "형광펜", "획 지우개", "영역 지우개", "직선", "곡선"]; // one highlighter since D14g
        Check("blank right-click shows text box and every tool switch", items.ContainsKey("텍스트 박스 추가") && tools.All(items.ContainsKey), string.Join(",", items.Keys));
        Check("the current tool is checked in the menu", items["그리기 펜"].IsChecked && tools.Where(t => t != "그리기 펜").All(t => !items[t].IsChecked));
        await Key(0x1B, keepFocus: true); // Esc closes the menu (the menu has the keyboard)
        Check("cancelling the menu changes nothing", menu is { IsOpen: false } && Session.UndoCount == undo && !Session.IsDirty && window.CurrentTool == ToolKind.Pen);
        var lineSettings = (window.LineStyle, window.LineToolOptions);
        menu = await RightClick(blank);
        await ClickMenuItem(menu!, "곡선");
        Check("a menu tool switch equals the toolbar and keeps the tool settings", window.CurrentTool == ToolKind.Curve &&
            ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("ShapeToolButton")).IsChecked == true &&
            Session.UndoCount == undo && (window.LineStyle, window.LineToolOptions) == lineSettings && Session.Current.AllAnnotations.Count() == 3, $"{window.CurrentTool}");
        menu = await RightClick(blank);
        await ClickMenuItem(menu!, "텍스트 박스 추가");
        Check("text box starts editing at the right-click point", window.ActiveTextEditor is not null && window.EditingText is { } editing &&
            // 2026-10-15 (24.24-6): the first line starts at the click — the box sits its 4pt padding left and half a line up.
            Math.Abs(editing.Transform.OffsetX + 4 - blank.X) < .5 && editing.Transform.OffsetY < blank.Y && blank.Y - editing.Transform.OffsetY < 20, window.EditingText?.Transform.ToString() ?? "none");
        await TypeText("menu");
        window.FinishTextEditing(); await Settle();
        var added = Session.Current.AllAnnotations.OfType<TextBoxAnnotation>().SingleOrDefault();
        Check("typed text box is kept where the menu was opened", added is { Text: "menu" } && Math.Abs(added.Transform.OffsetX + 4 - blank.X) < .5 && Session.UndoCount == undo + 1);
        menu = await RightClick(new(170, 420));
        var annotationItems = menu?.Items.OfType<System.Windows.Controls.MenuItem>().Select(i => (string)i.Header).ToArray() ?? [];
        Check("right-click on an annotation shows the annotation menu first", annotationItems.Contains("잠금") && !annotationItems.Contains("텍스트 박스 추가"));
        await Key(0x1B);
        menu = await RightClick(new(350, 600));
        annotationItems = menu?.Items.OfType<System.Windows.Controls.MenuItem>().Select(i => (string)i.Header).ToArray() ?? [];
        Check("right-click on a locked annotation offers unlocking", annotationItems.Contains("잠금 해제"));
        await Key(0x1B);
        window.SetTool(ToolKind.Pen); await Settle();
        undo = Session.UndoCount;
        await PenWithRightClick(new(300, 300), new(360, 330));
        Check("a right-click during a stroke cancels it and opens no menu", Session.UndoCount == undo && (window.LastCanvasMenu is not { IsOpen: true }));
        var outside = new Point(View.PageTransform(0).PageToDip.Transform(new(0, 0)).X - 30, 200);
        var before = window.LastCanvasMenu;
        if (outside.X > 5)
        {
            await RightClickDip(outside);
            Check("no quick menu outside the page", ReferenceEquals(window.LastCanvasMenu, before) || window.LastCanvasMenu is not { IsOpen: true });
        }

        // ----- shortcuts by focus (spec 17, A29) -----
        window.SetTool(ToolKind.Select); await Settle();
        await ClickPage(new(170, 420));
        Check("toolbar clicks keep the canvas focus", await ClickToolbar("PenToolButton") && View.IsKeyboardFocusWithin && window.CurrentTool == ToolKind.Pen);
        window.SetTool(ToolKind.Select); await ClickPage(new(170, 420));
        Clipboard.Clear();
        await Key(0x43, control: true); // Ctrl+C on the canvas
        Check("canvas Ctrl+C copies the selected annotation", Clipboard.ContainsData("PdfNotes.Annotations.v1") && !window.HasPageClipboard);
        undo = Session.UndoCount;
        // The pointer off the pages: Ctrl+V keeps the place, 10pt aside (spec 5.7, D14g — over a page it pastes at the pointer).
        var away = window.PointToScreen(new(window.ActualWidth / 2, 12)); SetCursorPos((int)away.X, (int)away.Y); await Task.Delay(100);
        await Key(0x56, control: true); // Ctrl+V
        var pasted = window.Selection.SelectedAnnotations.SingleOrDefault();
        Check("canvas Ctrl+V pastes a copy 10pt away", pasted is InkStroke && pasted.Id != a.Id && Math.Abs(pasted.Transform.OffsetX - a.Transform.OffsetX - 10) < .01 && Session.UndoCount == undo + 1);
        await Key(0x2E);
        Check("canvas Delete removes the selected annotation", Session.Current.FindAnnotation(pasted!.Id) is null && Session.Current.Pages.Count == 3);
        await Key(0x41, control: true); // Ctrl+A
        Check("canvas Ctrl+A selects the unlocked annotations of the page", window.Selection.SelectedIds.Count == 3 && !window.Selection.SelectedIds.Contains(lockedStroke.Id),
            $"{window.Selection.SelectedIds.Count}");
        await Key(0x1B);
        await Key(0x5A, control: true); // Ctrl+Z (the delete)
        await Key(0x5A, control: true, shift: true); // Ctrl+Shift+Z
        Check("Ctrl+Z and Ctrl+Shift+Z undo and redo on the canvas", Session.Current.FindAnnotation(pasted.Id) is null && Session.CanUndo && Session.CanRedo == false);

        var list = (System.Windows.Controls.ListBox)window.FindName("PageList");
        await ClickElement(list.ItemContainerGenerator.ContainerFromIndex(1) as FrameworkElement ?? list);
        Check("page panel has the focus after clicking a page", list.IsKeyboardFocusWithin);
        await Key(0x43, control: true);
        Check("page panel Ctrl+C copies the page, not annotations", window.HasPageClipboard);
        int prompts = 0;
        window.PromptOverride = (_, _) => { prompts++; return MessageBoxResult.No; };
        await Key(0x2E);
        Check("page panel Delete asks to delete the page (No keeps it)", prompts == 1 && Session.Current.Pages.Count == 3);
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        await Key(0x41, control: true);
        Check("page panel Ctrl+A selects every page", window.SelectedPageIds.Length == 3);
        list.SelectedIndex = 0; await Settle();

        var box = Session.Current.AllAnnotations.OfType<TextBoxAnnotation>().Single();
        window.BeginTextEdit(box.Id); await Settle();
        await Key(0x41, control: true); // Ctrl+A in the editor
        var editor = window.ActiveTextEditor!;
        Check("Ctrl+A while editing selects the text, not annotations", editor.Selection.Text.TrimEnd() == "menu" && window.Selection.SelectedIds.Count == 0);
        Clipboard.Clear();
        await Key(0x43, control: true);
        Check("Ctrl+C while editing copies the text", Clipboard.ContainsText() && Clipboard.GetText().TrimEnd() == "menu" && !Clipboard.ContainsData("PdfNotes.Annotations.v1"));
        await Key(0x2E);
        Check("Delete while editing deletes characters, not the box or a page", window.EditingText is not null && editor.Selection.IsEmpty && Session.Current.Pages.Count == 3);
        await Key(0x46, control: true); // Ctrl+F while editing
        Check("Ctrl+F while editing ends editing and opens search", window.IsSearchOpen && window.ActiveTextEditor is null);
        ((System.Windows.Controls.TextBox)window.FindName("SearchBox")).Focus();
        await Key(0x1B);
        ApplicationCommands.Undo.Execute(null, window); await Settle(); // the emptied box comes back
        Check("the emptied box is restored by Undo", Session.Current.FindAnnotation(box.Id) is not null);

        // ----- mixed selection: shared properties, mixed values (spec 3.1, 9.4) -----
        window.SetTool(ToolKind.Select);
        window.Selection.SetSelection([a.Id, b.Id]); await Settle();
        var widthBox = (System.Windows.Controls.TextBox)window.FindName("WidthBox");
        var bar = (FrameworkElement)window.FindName("StyleBar");
        var palette = (System.Windows.Controls.Panel)window.FindName("PalettePanel");
        Check("two strokes with different color and width: bar on, values shown as mixed", bar.IsEnabled && widthBox.Text == "" &&
            palette.Children.OfType<System.Windows.Controls.Button>().All(x => x.Tag is null) && window.StyleMixed is { Color: true, Width: true, Transparency: false });
        undo = Session.UndoCount;
        await ClickElement(palette.Children.OfType<System.Windows.Controls.Button>().First(x => Equals(x.DataContext, new RgbColor(0x1E, 0x88, 0xE5))));
        Check("a color click changes both in one Undo unit", Session.UndoCount == undo + 1 &&
            new[] { a.Id, b.Id }.All(id => ((InkStroke)Session.Current.FindAnnotation(id)!.Annotation).Color == new RgbColor(0x1E, 0x88, 0xE5)) && window.StyleMixed.Color == false);
        await ClickElement(widthBox);
        widthBox.SelectAll(); await TypeText("5"); await Key(0x0D);
        Check("a width typed for a mixed selection applies to both", new[] { a.Id, b.Id }.All(id => ((InkStroke)Session.Current.FindAnnotation(id)!.Annotation).WidthPt == 5) && widthBox.Text == "5",
            widthBox.Text);
        var image = new ImageAnnotation(Guid.NewGuid(), false, Affine.Translation(400, 380), Guid.NewGuid(), new(0, 0, 40, 40));
        var la = new LineAnnotation(Guid.NewGuid(), false, Affine.Identity, LineKind.Straight, new(100, 650), new(250, 650), [], 2, PdfNotes.Core.Models.DashStyle.Dash, false, true, RgbColor.Black, 1);
        var lb = la with { Id = Guid.NewGuid(), Start = new(100, 700), End = new(250, 700), Dash = PdfNotes.Core.Models.DashStyle.Dot, EndArrow = false };
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, la));
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, lb));
        window.Selection.SetSelection([la.Id, lb.Id]); await Settle();
        var dash = (System.Windows.Controls.Panel)window.FindName("LineOptionsPanel");
        var dashBox = dash.Children.OfType<System.Windows.Controls.ComboBox>().First();
        Check("two lines: line options shown with mixed dash and arrows off", dash.Visibility == Visibility.Visible && dashBox.SelectedIndex == -1 &&
            dash.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().All(t => t.IsChecked == false));
        window.SetLineDash(PdfNotes.Core.Models.DashStyle.Solid); await Settle();
        Check("a dash choice applies to both lines", new[] { la.Id, lb.Id }.All(id => ((LineAnnotation)Session.Current.FindAnnotation(id)!.Annotation).Dash == PdfNotes.Core.Models.DashStyle.Solid) &&
            dashBox.SelectedIndex == 0);
        window.Selection.SetSelection([a.Id, box.Id]); await Settle();
        Check("a stroke with a text box: only shared commands, the style bar is off", !bar.IsEnabled && window.StyleTarget is null);
        Snapshot("d11c.png");
    }

    // ----- D11d: settings window (spec 18.1) and quick erase (spec 7.5) -----

    private static async Task PartD()
    {
        string pdf = Path.Combine(folder, "source.pdf");
        ProbeSamplePdf.Create(pdf, 1);
        Check("open sample PDF", await window.OpenFileAsync(pdf));
        await ShowPage(0, .6);
        var a = Stroke([new(100, 420), new(300, 420)]);
        var b = (FreeHighlightStroke)AddAnnotation(new FreeHighlightStroke(Guid.NewGuid(), false, Affine.Identity, [new(100, 520), new(300, 520)], 14, new(0xFF, 0xE6, 0), .4));
        var line = AddAnnotation(new LineAnnotation(Guid.NewGuid(), false, Affine.Identity, LineKind.Straight, new(100, 620), new(300, 620), [], 2,
            PdfNotes.Core.Models.DashStyle.Solid, false, false, RgbColor.Black, 1));
        Check("save the start state", await window.SaveToAsync(Path.Combine(folder, "start.pdf")) && !Session.IsDirty);
        int undo = Session.UndoCount;

        // ----- settings window -----
        string settingsFile = Path.Combine(folder, "settings", "settings.json");
        await ClickElement((FrameworkElement)window.FindName("SettingsButton"));
        var dialog = await Modal<PdfNotes.App.Views.SettingsDialog>();
        var categories = Field<System.Windows.Controls.ListBox>(dialog, "CategoryList");
        Check("the gear opens the settings window with its categories", categories.Items.Cast<System.Windows.Controls.ListBoxItem>().Select(i => (string)i.Content)
            .SequenceEqual(["필기", "저장", "화면"]) && Field<System.Windows.Controls.CheckBox>(dialog, "QuickEraseBox").IsChecked == true); // 호환 is gone (D14g)
        await ClickElement((FrameworkElement)categories.ItemContainerGenerator.ContainerFromIndex(1));
        var backup = Field<System.Windows.Controls.CheckBox>(dialog, "AutoBackupBox");
        Check("automatic backup is shown and can be changed (D12)", backup.IsVisible && backup.IsEnabled);
        await ClickElement((FrameworkElement)categories.ItemContainerGenerator.ContainerFromIndex(0));
        await ClickElement(Field<System.Windows.Controls.CheckBox>(dialog, "PageDragBox"));
        await ClickElement(Field<System.Windows.Controls.Button>(dialog, "CancelButton"));
        Check("Cancel discards the change", !dialog.IsVisible && !window.PageDragEnabled && (!File.Exists(settingsFile) || !PdfNotes.Editor.Services.AppSettings.Load(settingsFile).PageDrag));
        await Key(0xBC, control: true); // Ctrl+,
        dialog = await Modal<PdfNotes.App.Views.SettingsDialog>();
        Check("Ctrl+, opens the settings window", dialog.IsVisible);
        await ClickElement(Field<System.Windows.Controls.CheckBox>(dialog, "QuickEraseBox"));
        await ClickElement(Field<System.Windows.Controls.CheckBox>(dialog, "PageDragBox"));
        await ClickElement(Field<System.Windows.Controls.Button>(dialog, "OkButton"));
        var stored = PdfNotes.Editor.Services.AppSettings.Load(settingsFile);
        Check("OK stores both values in the app settings", !window.QuickEraseEnabled && window.PageDragEnabled && !stored.QuickErase && stored.PageDrag);
        Check("settings do not touch the document (clean, no Undo step)", !Session.IsDirty && Session.UndoCount == undo && !window.Title.Contains('*'));
        var restarted = new MainWindow();
        Check("a restarted window reads the settings", !restarted.QuickEraseEnabled && restarted.PageDragEnabled);
        restarted.Close();

        // ----- quick erase off: the middle button does nothing -----
        window.SetTool(ToolKind.Pen); await Settle();
        await MiddleDrag(new(200, 400), new(200, 440));
        Check("with quick erase off the middle button changes nothing", Session.UndoCount == undo && window.CurrentTool == ToolKind.Pen);
        await Key(0xBC, control: true);
        dialog = await Modal<PdfNotes.App.Views.SettingsDialog>();
        await ClickElement(Field<System.Windows.Controls.CheckBox>(dialog, "QuickEraseBox"));
        await ClickElement(Field<System.Windows.Controls.Button>(dialog, "OkButton"));
        Check("quick erase on again", window.QuickEraseEnabled);

        // ----- quick erase in the pen -----
        await HoverDip(Dip(new(150, 300)));
        var penCursor = View.Cursor;
        var penStyle = window.PenStyle;
        bool held = false;
        await MiddleDrag(new(200, 400), new(200, 440), during: () => held = View.Cursor == Cursors.Cross && window.CurrentTool == ToolKind.Pen &&
            ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("PenToolButton")).IsChecked == true && window.StyleTarget is { Kind: AnnotationKind.Ink });
        Check("while held: area eraser cursor, the toolbar and style bar still show the pen", held);
        var pieces = Session.Current.AllAnnotations.OfType<InkStroke>().ToArray();
        Check("one rub with the middle button erases part of the pen stroke as one Undo unit", Session.UndoCount == undo + 1 && pieces.Length == 2 && pieces.All(p => p.IsOutlined) &&
            Session.Current.FindAnnotation(line.Id) is not null, $"{pieces.Length} pieces, undo +{Session.UndoCount - undo}");
        Check("the pen is back after release with its settings", window.CurrentTool == ToolKind.Pen && View.Cursor == penCursor && window.PenStyle == penStyle, $"{View.Cursor}");
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("Undo restores the stroke", Session.Current.FindAnnotation(a.Id) is not null && Session.UndoCount == undo);
        await MiddleDrag(new(200, 600), new(200, 640));
        Check("lines are not erased by quick erase", Session.UndoCount == undo && Session.Current.FindAnnotation(line.Id) is not null);

        // ----- cancel: Esc, the left button -----
        await MiddleDrag(new(200, 400), new(200, 440), cancelKey: true);
        Check("Esc during the rub cancels it and keeps the pen", Session.UndoCount == undo && Session.Current.FindAnnotation(a.Id) is not null && window.CurrentTool == ToolKind.Pen);
        await MiddleDrag(new(200, 400), new(200, 440), cancelLeft: true);
        Check("the left button during the rub cancels it", Session.UndoCount == undo && Session.Current.FindAnnotation(a.Id) is not null && window.CurrentTool == ToolKind.Pen &&
            !Session.Current.AllAnnotations.Any(x => x is InkStroke && x.Id != a.Id));

        // ----- the middle button while drawing with the left button is ignored -----
        await LeftDragWithMiddle(new(120, 300), new(280, 330));
        Check("a left stroke keeps going when the middle button is pressed", Session.UndoCount == undo + 1 && Session.Current.AllAnnotations.OfType<InkStroke>().Count() == 2 &&
            Session.Current.FindAnnotation(a.Id) is not null);
        ApplicationCommands.Undo.Execute(null, window); await Settle();

        // ----- the free highlighter too; glyph highlight tools are excluded -----
        window.SetTool(ToolKind.FreeHighlight); await Settle();
        await MiddleDrag(new(200, 500), new(200, 540));
        Check("quick erase in the free highlighter, back to it afterwards", Session.UndoCount == undo + 1 &&
            Session.Current.AllAnnotations.OfType<FreeHighlightStroke>().Count(h => h.IsOutlined) == 2 && // the first piece keeps the stroke ID (D08)
            window.CurrentTool == ToolKind.FreeHighlight && window.StyleTarget is { Kind: AnnotationKind.FreeHighlight },
            $"undo +{Session.UndoCount - undo}, original {(Session.Current.FindAnnotation(b.Id) is null ? "gone" : "kept")}, tool {window.CurrentTool}, highlighters {Session.Current.AllAnnotations.OfType<FreeHighlightStroke>().Count()}");
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        window.SetMarkKind(TextMarkupKind.Underline); await Settle();
        await MiddleDrag(new(200, 400), new(200, 440));
        Check("quick erase in the highlighter's underline kind too (spec 7.5, D14g)", Session.UndoCount == undo + 1 && window.CurrentTool == ToolKind.FreeHighlight);
        ApplicationCommands.Undo.Execute(null, window); window.SetMarkKind(null); await Settle();

        window.SetTool(ToolKind.Pen); await Settle();
        await MiddleDrag(new(150, 30), new(150, 60), outsidePage: true);
        Check("a middle press outside the page does nothing", Session.UndoCount == undo);
        Snapshot("d11d.png");
    }

    // ----- D11e: shape tool (spec 8 preface) -----

    private static ShapeAnnotation[] Shapes => Session.Current.AllAnnotations.OfType<ShapeAnnotation>().ToArray();

    private static async Task ChooseShape(string header)
    {
        await ClickElement((FrameworkElement)window.FindName("ShapeMenuButton"));
        var menu = window.LastShapeMenu!;
        await Settle();
        await ClickMenuItem(menu, header);
    }

    private static async Task PartE()
    {
        Check("new note", await window.CreateNoteAsync(NewPageSpec.Default));
        await ShowPage(0, .6);
        await ChooseShape("사각형");
        Check("the shape menu chooses the rectangle tool", window.CurrentTool == ToolKind.Shape && window.ShapeToolKind == ShapeKind.Rectangle &&
            ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("ShapeToolButton")).IsChecked == true);
        int undo = Session.UndoCount;
        await Drag(new(100, 100), new(160, 130), new(220, 160));
        var rect = Shapes.SingleOrDefault();
        Check("a drag draws a rectangle from corner to corner, selected for styling", rect is { ShapeKind: ShapeKind.Rectangle } && Math.Abs(rect.Box.X - 100) < 1.5 &&
            Math.Abs(rect.Box.Width - 120) < 1.5 && Math.Abs(rect.Box.Height - 60) < 1.5 && Session.UndoCount == undo + 1 && window.Selection.SelectedIds.SequenceEqual([rect.Id]) &&
            ((FrameworkElement)window.FindName("ShapeOptionsPanel")).Visibility == Visibility.Visible, rect is null ? "none" : $"{rect.Box}");
        await Drag(new(300, 300), new(301, 300.5));
        Check("a drag shorter than 4 px draws nothing", Shapes.Length == 1);
        await ChooseShape("타원");
        await DragDip([Dip(new(300, 100)), Dip(new(340, 120)), Dip(new(380, 125))], shift: true);
        var circle = Shapes.Last();
        Check("Shift draws a circle", circle.ShapeKind == ShapeKind.Ellipse && Math.Abs(circle.Box.Width - circle.Box.Height) < 1e-6 && Math.Abs(circle.Box.Width - 80) < 1.5, $"{circle.Box}");
        await ChooseShape("삼각형");
        await Drag(new(100, 250), new(200, 330));
        Check("triangle", Shapes.Last().ShapeKind == ShapeKind.Triangle);

        // ----- free polygon -----
        await ChooseShape("다각형");
        undo = Session.UndoCount;
        await ClickPage(new(300, 250)); await ClickPage(new(400, 260)); await ClickPage(new(420, 340));
        Check("polygon corners are not committed yet", Session.UndoCount == undo && Shapes.Length == 3);
        await Task.Delay(600);
        await DoubleClick(new(320, 350));
        var polygon = Shapes.Last();
        Check("a double-click finishes the polygon with its four corners", polygon.ShapeKind == ShapeKind.Polygon && polygon.Points.Length == 4 && Session.UndoCount == undo + 1,
            $"{polygon.ShapeKind} {polygon.Points.Length}");
        await ClickPage(new(100, 400)); await ClickPage(new(160, 400)); await ClickPage(new(130, 450));
        await Key(0x0D); // Enter
        Check("Enter finishes a polygon", Shapes.Last() is { ShapeKind: ShapeKind.Polygon, Points.Length: 3 } && Shapes.Length == 5);
        await ClickPage(new(250, 400)); await ClickPage(new(300, 420));
        await Key(0x1B);
        Check("Esc discards a polygon in progress", Shapes.Length == 5 && !View.IsToolActive);
        await ClickPage(new(250, 400)); await ClickPage(new(300, 420)); await ClickPage(new(280, 470));
        await ClickPage(new(250.5, 400.5)); // on the first corner
        Check("a click on the first corner closes the polygon", Shapes.Length == 6 && Shapes.Last().Points.Length == 3);

        // ----- fill and border (style bar) -----
        var target = Shapes.Last();
        var palette = (System.Windows.Controls.Panel)window.FindName("PalettePanel");
        System.Windows.Controls.Button Swatch(RgbColor c) => palette.Children.OfType<System.Windows.Controls.Button>().First(x => Equals(x.DataContext, c));
        var red = new RgbColor(0xE5, 0x39, 0x35); var green = new RgbColor(0x43, 0xA0, 0x47);
        undo = Session.UndoCount;
        await ClickElement(Swatch(red));
        Check("with the fill target a color changes the fill of the new shape and the tool", ((ShapeAnnotation)Session.Current.FindAnnotation(target.Id)!.Annotation).Style.FillColor == red &&
            window.ShapeToolStyle.FillColor == red && Session.UndoCount == undo + 1);
        var options = (System.Windows.Controls.Panel)window.FindName("ShapeOptionsPanel");
        await ClickElement(options.Children.OfType<System.Windows.Controls.RadioButton>().Single(r => (string)r.Content == "테두리"));
        await ClickElement(Swatch(green));
        var styled = ((ShapeAnnotation)Session.Current.FindAnnotation(target.Id)!.Annotation).Style;
        Check("with the border target it changes the border", styled.BorderColor == green && styled.FillColor == red && !window.ShapeFillTarget);
        var borderBox = options.Children.OfType<System.Windows.Controls.ComboBox>().Single();
        window.SetShapeBorder(null); await Settle();
        Check("no border", !((ShapeAnnotation)Session.Current.FindAnnotation(target.Id)!.Annotation).Style.BorderEnabled && borderBox.SelectedIndex == 0);
        window.SetShapeBorder(PdfNotes.Core.Models.DashStyle.Dash); await Settle();
        Check("a dashed border again", ((ShapeAnnotation)Session.Current.FindAnnotation(target.Id)!.Annotation).Style is { BorderEnabled: true, BorderDash: PdfNotes.Core.Models.DashStyle.Dash } &&
            borderBox.SelectedIndex == 2);
        window.SetTransparency(50); await Settle();
        Check("border transparency", Math.Abs(((ShapeAnnotation)Session.Current.FindAnnotation(target.Id)!.Annotation).Style.BorderOpacity - .5) < 1e-9);
        window.SetShapeColorTarget(true);

        // ----- selection: free resize, Shift keeps the ratio, rotation, lasso -----
        window.SetTool(ToolKind.Select); await Settle();
        rect = (ShapeAnnotation)Session.Current.FindAnnotation(rect!.Id)!.Annotation;
        await ClickPage(new(160, 130));
        Check("a click inside a filled shape selects it", window.Selection.SelectedIds.SequenceEqual([rect.Id]));
        var handles = window.Selection.HandlePoints(View.PageTransform(0));
        var corner = new Point(handles[2].X, handles[2].Y); // bottom-right
        await DragDip([corner, new(corner.X + 30, corner.Y), new(corner.X + 60, corner.Y)]);
        var resized = (ShapeAnnotation)Session.Current.FindAnnotation(rect.Id)!.Annotation;
        Check("a corner drag stretches a single shape freely", resized.Box.Width > rect.Box.Width + 30 && Math.Abs(resized.Box.Height - rect.Box.Height) < 1 &&
            resized.Style.BorderWidthPt == rect.Style.BorderWidthPt, $"{rect.Box} → {resized.Box}");
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        handles = window.Selection.HandlePoints(View.PageTransform(0));
        corner = new Point(handles[2].X, handles[2].Y);
        await DragDip([corner, new(corner.X + 30, corner.Y), new(corner.X + 60, corner.Y)], shift: true);
        resized = (ShapeAnnotation)Session.Current.FindAnnotation(rect.Id)!.Annotation;
        Check("Shift keeps the ratio", Math.Abs(resized.Box.Width / resized.Box.Height - rect.Box.Width / rect.Box.Height) < .02, $"{resized.Box}");
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("88 degrees snaps for a shape too", await RotateBy(rect, 88, snapped: 90));
        window.SetTool(ToolKind.Lasso); await Settle();
        await Drag(new(290, 90), new(390, 90), new(390, 140), new(290, 140));
        Check("lasso over a filled ellipse selects it", window.Selection.SelectedIds.SequenceEqual([circle.Id]) && window.CurrentTool == ToolKind.Select);
        await Key(0x2E);
        Check("Delete removes the shape (shapes are not erased by erasers)", Session.Current.FindAnnotation(circle.Id) is null);
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        window.SetTool(ToolKind.AreaEraser); await Settle();
        await Drag(new(100, 290), new(220, 290));
        Check("the area eraser leaves shapes alone", Shapes.Length == 6);
        Snapshot("d11e.png");

        // ----- a rotated page: shapes are drawn axis-aligned on screen -----
        string pdf = Path.Combine(folder, "source.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        string saved = Path.Combine(folder, "shapes.pdf");
        Check("save shapes", await window.SaveToAsync(saved));
        File.WriteAllText(Path.Combine(folder, "expected.json"), Metadata(Session.Current));
        Check("open sample PDF", await window.OpenFileAsync(pdf));
        await ShowPage(1, .6); // /Rotate 90
        window.SetShapeKind(ShapeKind.Rectangle); await Settle();
        var p0 = new Point(View.ActualWidth / 2 - 80, View.ActualHeight / 2 - 40);
        await DragDip([p0, new(p0.X + 80, p0.Y + 40), new(p0.X + 160, p0.Y + 80)]);
        var turned = Shapes.Single();
        var screen = AnnotationGeometry.Corners(turned.Box, turned.Transform.Then(View.PageTransform(1).PageToDip));
        Check("on a /Rotate 90 page the rectangle is axis-aligned on screen where it was dragged",
            Math.Abs(screen[0].Y - screen[1].Y) < .01 && Math.Abs(screen[0].X - screen[3].X) < .01 && Math.Abs(screen[0].X - p0.X) < 2 && Math.Abs(screen[2].X - (p0.X + 160)) < 2,
            string.Join(" ", screen.Select(c => $"({c.X:0.#},{c.Y:0.#})")));
        Snapshot("d11e-rotated.png");
        window.PromptOverride = (_, _) => MessageBoxResult.No;
    }

    private static string Metadata(DocumentModel model)
    {
        var data = PdfNotes.Core.Serialization.SchemaV6.Serialize(model);
        return JsonSerializer.Serialize(new { data.Header, Envelopes = data.Annotations.Select(a => a.Envelope) });
    }

    private static async Task PartEReopen()
    {
        Check("reopen in a new process", await window.OpenFileAsync(Path.Combine(folder, "shapes.pdf")));
        Check("all shapes come back editable with the same data", Metadata(Session.Current) == File.ReadAllText(Path.Combine(folder, "expected.json")) && messages.Count == 0,
            string.Join(" | ", messages));
        await ShowPage(0, .6);
        var rect = Shapes.First();
        window.SetTool(ToolKind.Select); window.Selection.Select(rect.Id); await Settle();
        window.SetShapeColorTarget(true);
        window.SetColor(new(0x8E, 0x24, 0xAA)); await Settle();
        Check("a reopened shape can be restyled", ((ShapeAnnotation)Session.Current.FindAnnotation(rect.Id)!.Annotation).Style.FillColor == new RgbColor(0x8E, 0x24, 0xAA));
        string again = Path.Combine(folder, "shapes-again.pdf");
        Check("re-save", await window.SaveToAsync(again));
        using var engine = new PdfNotes.Pdf.Engine.PdfiumDocument(File.ReadAllBytes(again));
        PdfNotes.App.Prototype.ProbeFactory.SavePng(engine.Render(0, 1.5), Path.Combine(folder, "shapes-page.png"));
        Snapshot("d11e-reopen.png");
    }

    // ----- D11f: other apps' annotations (spec 14.6) -----

    private static async Task PartF()
    {
        string pdf = Path.Combine(folder, "foreign.pdf");
        PdfNotes.Pdf.Prototype.ForeignAnnotationSample.Create(pdf);
        byte[] original = File.ReadAllBytes(pdf);
        var status = (System.Windows.Controls.TextBlock)window.FindName("StatusText");
        // D14g (spec 14.6): opening converts nothing; the 다른 프로그램 주석 편집 command converts, as one Undo unit.
        Check("open", await window.OpenFileAsync(pdf));
        Check("opening keeps other apps' annotations read-only", !Session.Current.AllAnnotations.Any() && !Session.IsDirty);
        await ShowPage(0, .6);
        Snapshot("d11f-off.png");
        int expected = PdfNotes.Pdf.Prototype.ForeignAnnotationSample.ConvertiblePdfAnnotations + 1;
        for (int i = 0; i < 100 && window.ForeignAnnotationCount == 0; i++) await Task.Delay(50); // counted in the background
        var button = (System.Windows.Controls.Button)window.FindName("ForeignEditButton");
        Check("they are counted once in the status bar and the 필기 tab offers the command", window.ForeignAnnotationCount == expected &&
            status.Text.Contains($"다른 프로그램 주석 {expected}개") && button.IsVisible && button.IsEnabled && messages.Count == 0, $"{window.ForeignAnnotationCount}, '{status.Text}'");
        await ClickElement(button);
        for (int i = 0; i < 100 && !Session.Current.AllAnnotations.Any(); i++) await Task.Delay(50);
        int count = Session.Current.AllAnnotations.Count();
        Check("the command makes the convertible annotations editable", count == expected && status.Text.Contains($"{count}개") && !button.IsEnabled, $"{count}, '{status.Text}'");
        Check("converting is one Undo unit and marks the document unsaved", Session.IsDirty && Session.UndoLabel == "undo.convertForeign" && window.Title.Contains('*'));
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("Undo shows the originals read-only again", !Session.Current.AllAnnotations.Any() && !Session.IsDirty && button.IsEnabled);
        ApplicationCommands.Redo.Execute(null, window); await Settle();
        Check("Redo converts them again", Session.Current.AllAnnotations.Count() == expected);
        await ShowPage(0, .6);
        Snapshot("d11f-on.png");

        // ----- edit them like our own -----
        window.SetTool(ToolKind.Select); await Settle();
        var ink = Session.Current.AllAnnotations.OfType<InkStroke>().First();
        Point2 C(double x, double y) => PdfNotes.Pdf.Reading.PdfPageGeometryReader.Read(original)[0].Box.PdfUserToCanonical.Transform(new(x, y));
        await ClickPage(C(110, 615));
        Check("a converted ink stroke is selected by a click", window.Selection.SelectedIds.SequenceEqual([ink.Id]));
        int undo = Session.UndoCount;
        await Drag(C(110, 615), C(120, 605), C(130, 595));
        Check("and moved (one Undo unit)", Session.UndoCount == undo + 1 && Moved(ink, 20, 20));
        var text = Session.Current.AllAnnotations.OfType<TextBoxAnnotation>().First(t => t.Text == "Foreign note");
        await Task.Delay(600);
        await DoubleClick(C(380, 620));
        Check("a converted FreeText opens in the text editor", window.ActiveTextEditor is not null && window.EditingText?.Id == text.Id);
        window.ActiveTextEditor!.CaretPosition = window.ActiveTextEditor.Document.ContentEnd.GetInsertionPosition(System.Windows.Documents.LogicalDirection.Backward);
        await TypeText("!");
        window.FinishTextEditing(); await Settle();
        Check("its text is edited", ((TextBoxAnnotation)Session.Current.FindAnnotation(text.Id)!.Annotation).Text == "Foreign note!");
        var locked = Session.Current.AllAnnotations.OfType<ShapeAnnotation>().Single(s => s.Locked);
        await ClickPage(C(450, 251));
        Check("a locked one stays locked", !window.Selection.SelectedIds.Contains(locked.Id));
        string saved = Path.Combine(folder, "saved.pdf");
        Check("save", await window.SaveToAsync(saved) && !Session.IsDirty);
        Check("the original file is unchanged", File.ReadAllBytes(pdf).AsSpan().SequenceEqual(original));
        File.WriteAllText(Path.Combine(folder, "expected.json"), Metadata(Session.Current));
    }

    private static async Task PartFReopen()
    {
        string saved = Path.Combine(folder, "saved.pdf");
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        Check("reopen the saved file in a new process", await window.OpenFileAsync(saved));
        var status = (System.Windows.Controls.TextBlock)window.FindName("StatusText");
        await Task.Delay(1500); // the background count of other apps' annotations
        Check("no duplicates: our annotations are restored and nothing is left to convert", Metadata(Session.Current) == File.ReadAllText(Path.Combine(folder, "expected.json")) &&
            window.ForeignAnnotationCount == 0 && !((System.Windows.Controls.Button)window.FindName("ForeignEditButton")).IsVisible && messages.Count == 0,
            $"'{status.Text}' {string.Join("|", messages)}");

        await ShowPage(0, .6);
        Snapshot("d11f-reopen.png");
        using var engine = new PdfNotes.Pdf.Engine.PdfiumDocument(File.ReadAllBytes(saved));
        PdfNotes.App.Prototype.ProbeFactory.SavePng(engine.Render(0, 1.2), Path.Combine(folder, "saved-page.png"));
    }

    private static Annotation AddAnnotation(Annotation annotation)
    {
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, annotation));
        return annotation;
    }

    /// <summary>Middle button press at <paramref name="from"/>, drag to <paramref name="to"/>, release (or cancel with Esc / the left button).</summary>
    private static async Task MiddleDrag(Point2 from, Point2 to, Action? during = null, bool cancelKey = false, bool cancelLeft = false, bool outsidePage = false)
    {
        await Task.Delay(400);
        await EnsureForeground();
        var start = Dip(from);
        if (outsidePage) start = new(View.PageTransform(0).PageToDip.Transform(new(0, 0)).X - 20, start.Y);
        Cursor(start); await Task.Delay(60);
        mouse_event(0x20, 0, 0, 0, 0); await Task.Delay(50);
        for (int i = 1; i <= 4; i++) { Cursor(Dip(new(from.X + (to.X - from.X) * i / 4, from.Y + (to.Y - from.Y) * i / 4))); await Task.Delay(30); }
        await Settle();
        during?.Invoke();
        if (cancelKey) { keybd_event(0x1B, 0, 0, 0); await Task.Delay(30); keybd_event(0x1B, 0, 2, 0); await Settle(); }
        if (cancelLeft) { mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0); await Settle(); }
        mouse_event(0x40, 0, 0, 0, 0);
        await Settle(); await Settle();
    }

    private static async Task LeftDragWithMiddle(Point2 from, Point2 to)
    {
        await Task.Delay(400);
        await EnsureForeground();
        Cursor(Dip(from)); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(50);
        Cursor(Dip(new((from.X + to.X) / 2, (from.Y + to.Y) / 2))); await Task.Delay(40);
        mouse_event(0x20, 0, 0, 0, 0); await Task.Delay(40);
        Cursor(Dip(new((from.X * 1 + to.X * 3) / 4, (from.Y + to.Y * 3) / 4))); await Task.Delay(40);
        mouse_event(0x40, 0, 0, 0, 0); await Task.Delay(40);
        Cursor(Dip(to)); await Task.Delay(40);
        mouse_event(4, 0, 0, 0, 0);
        await Settle(); await Settle();
    }

    private static async Task<T> Modal<T>() where T : Window
    {
        for (int i = 0; i < 30; i++) { if (Application.Current.Windows.OfType<T>().FirstOrDefault(w => w.IsVisible) is { } modal) { await Task.Delay(150); return modal; } await Task.Delay(100); }
        throw new InvalidOperationException("Modal did not open: " + typeof(T).Name);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var c = VisualTreeHelper.GetChild(parent, i); yield return c; foreach (var d in Descendants(c)) yield return d; }
    }

    private static T Field<T>(Window dialog, string name) where T : FrameworkElement => Descendants(dialog).OfType<T>().Single(c => c.Name == name);

    private static async Task<System.Windows.Controls.ContextMenu?> RightClick(Point2 pagePoint) => await RightClickDip(Dip(pagePoint));

    private static async Task<System.Windows.Controls.ContextMenu?> RightClickDip(Point dip)
    {
        await Task.Delay(400);
        await EnsureForeground();
        Cursor(dip); await Task.Delay(60);
        mouse_event(8, 0, 0, 0, 0); await Task.Delay(40); mouse_event(16, 0, 0, 0, 0);
        await Settle(); await Settle();
        return window.LastCanvasMenu is { IsOpen: true } open ? open : null;
    }

    private static async Task ClickMenuItem(System.Windows.Controls.ContextMenu menu, string header)
    {
        var item = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(i => (string)i.Header == header);
        var p = item.PointToScreen(new(item.ActualWidth / 3, item.ActualHeight / 2));
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y)); await Task.Delay(80);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0);
        await Settle(); await Settle();
    }

    /// <summary>Left drag with the pen; the right button is pressed in the middle of the stroke.</summary>
    private static async Task PenWithRightClick(Point2 from, Point2 to)
    {
        await Task.Delay(400);
        await EnsureForeground();
        Cursor(Dip(from)); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(50);
        Cursor(Dip(new((from.X + to.X) / 2, (from.Y + to.Y) / 2))); await Task.Delay(40);
        mouse_event(8, 0, 0, 0, 0); await Task.Delay(40); mouse_event(16, 0, 0, 0, 0); await Task.Delay(40);
        Cursor(Dip(to)); await Task.Delay(40);
        mouse_event(4, 0, 0, 0, 0);
        await Settle(); await Settle();
    }

    private static async Task<bool> ClickToolbar(string name)
    {
        await ClickElement((FrameworkElement)window.FindName(name));
        return true;
    }

    private static async Task ClickElement(FrameworkElement element)
    {
        await Task.Delay(400);
        if (Window.GetWindow(element) == window) await EnsureForeground();
        var p = element.PointToScreen(new(element.ActualWidth / 2, element.ActualHeight / 2));
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y)); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0);
        await Settle();
    }

    private static bool InView(int pageIndex, IEnumerable<Quad> quads)
    {
        var m = View.PageTransform(pageIndex).PageToDip;
        var ring = quads.SelectMany(TextSelection.Ring).Select(m.Transform).ToArray();
        return ring.Min(p => p.X) >= 0 && ring.Min(p => p.Y) >= 0 && ring.Max(p => p.X) <= View.ViewportWidth && ring.Max(p => p.Y) <= View.ViewportHeight;
    }

    private static async Task SearchDone()
    {
        await Task.Delay(MainWindow.SearchDelayMs + 50);
        for (int i = 0; i < 100 && window.IsSearching; i++) await Task.Delay(50);
        await Settle();
    }

    /// <summary>Unicode key events into the focused control (not an IME composition test).</summary>
    private static async Task TypeText(string text)
    {
        await EnsureForeground();
        foreach (char c in text)
        {
            var down = new Input { Type = 1, Scan = c, Flags = 4 };
            var up = new Input { Type = 1, Scan = c, Flags = 4 | 2 };
            SendInput(2, [down, up], Marshal.SizeOf<Input>());
            await Task.Delay(40);
        }
        await Settle();
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Vk;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public nint Extra;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);

    private static InkStroke Stroke(Point2[] points)
    {
        var stroke = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [.. points], 3, RgbColor.Black, 1);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, stroke));
        return stroke;
    }

    private static bool Moved(Annotation original, double dx, double dy)
    {
        var now = Session.Current.FindAnnotation(original.Id)!.Annotation.Transform;
        return Math.Abs(now.OffsetX - original.Transform.OffsetX - dx) < 1.01 && Math.Abs(now.OffsetY - original.Transform.OffsetY - dy) < 1.01;
    }

    private static Point[] FrameDip()
    {
        var f = window.Selection.SelectionFrame()!.Value;
        return AnnotationGeometry.Corners(f.Bounds, f.Frame.Then(View.PageTransform(0).PageToDip)).Select(p => new Point(p.X, p.Y)).ToArray();
    }

    /// <summary>
    /// Drags the rotation handle of the single selection around the frame center by <paramref name="degrees"/> (far from
    /// the center for precision), then checks the committed angle: exactly <paramref name="snapped"/> or within 1° of
    /// the requested one. Undoes the rotation afterwards.
    /// </summary>
    private static async Task<bool> RotateBy(Annotation target, double degrees, bool shift = false, bool alt = false, double? snapped = null, bool expectChange = true)
    {
        var handle = window.Selection.HandlePoints(View.PageTransform(0))[4];
        var f = window.Selection.SelectionFrame()!.Value;
        var c = f.Frame.Then(View.PageTransform(0).PageToDip).Transform(new(f.Bounds.X + f.Bounds.Width / 2, f.Bounds.Y + f.Bounds.Height / 2));
        var press = View.PointFromScreen(View.PointToScreen(new(Math.Round(handle.X), Math.Round(handle.Y))));
        double startAngle = Math.Atan2(press.Y - c.Y, press.X - c.X);
        double radius = Math.Min(220, Math.Min(Math.Min(c.X, c.Y), Math.Min(View.ActualWidth - c.X, View.ActualHeight - c.Y)) - 4);
        var path = Enumerable.Range(1, 6).Select(i =>
        {
            double t = startAngle + degrees * Math.PI / 180 * i / 6;
            return new Point(c.X + radius * Math.Cos(t), c.Y + radius * Math.Sin(t));
        });
        int undo = Session.UndoCount;
        await DragDip([press, .. path], shift, alt);
        var now = Session.Current.FindAnnotation(target.Id)!.Annotation.Transform;
        double angle = Math.Atan2(now.M12, now.M11) * 180 / Math.PI;
        bool ok = snapped is { } s ? Math.Abs(angle - (s > 180 ? s - 360 : s)) < 1e-6 : Math.Abs(angle - degrees) < 1;
        messages.Add($"rotate {degrees}° shift={shift} alt={alt}: angle {angle:0.######}°");
        if (Session.UndoCount != undo) ApplicationCommands.Undo.Execute(null, window);
        await Settle();
        return ok && (!expectChange || angle != 0);
    }

    // ----- helpers -----

    private static async Task Foreground()
    {
        var handle = new WindowInteropHelper(window).Handle;
        SetForegroundWindow(handle); window.Activate(); await Settle();
        for (int attempt = 0; attempt < 5 && GetForegroundWindow() != handle; attempt++)
        { keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); window.Activate(); SetForegroundWindow(handle); await Task.Delay(200); }
        if (GetForegroundWindow() != handle) { window.Topmost = true; window.Activate(); SetForegroundWindow(handle); await Settle(); window.Topmost = false; }
        Check("foreground", GetForegroundWindow() == handle, $"target={handle}, actual={GetForegroundWindow()}");
    }

    private static int Find(IReadOnlyList<TextGlyph> glyphs, string text) =>
        string.Concat(glyphs.Select(g => g.Text.Length == 1 ? g.Text : "�")).IndexOf(text, StringComparison.Ordinal);

    /// <summary>Page point a little inside a glyph's start (left) or end (right) along its reading direction.</summary>
    private static Point2 Edge(TextGlyph g, bool end)
    {
        double f = end ? .8 : .2;
        var top = new Point2(g.Box.UpperLeft.X + (g.Box.UpperRight.X - g.Box.UpperLeft.X) * f, g.Box.UpperLeft.Y + (g.Box.UpperRight.Y - g.Box.UpperLeft.Y) * f);
        var bottom = new Point2(g.Box.LowerLeft.X + (g.Box.LowerRight.X - g.Box.LowerLeft.X) * f, g.Box.LowerLeft.Y + (g.Box.LowerRight.Y - g.Box.LowerLeft.Y) * f);
        return new((top.X + bottom.X) / 2, (top.Y + bottom.Y) / 2);
    }

    private static async Task ShowPage(int index, double zoom = 1)
    {
        page = index;
        View.SetZoom(zoom); await Settle();
        View.GoToPage(index); await Settle();
    }

    private static Point Dip(Point2 pagePoint)
    {
        var p = View.PageTransform(page).PageToDip.Transform(pagePoint);
        return new(p.X, p.Y);
    }

    private static async Task Drag(params Point2[] points) => await DragDip(points.Select(Dip).ToArray());

    private static async Task DragDip(Point[] points, bool shift = false, bool alt = false)
    {
        await EnsureForeground();
        Cursor(points[0]); await Task.Delay(60);
        if (shift) keybd_event(0x10, 0, 0, 0);
        if (alt) keybd_event(0x12, 0, 0, 0);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(50);
        foreach (var p in points.Skip(1)) { Cursor(p); await Task.Delay(25); }
        await Task.Delay(40);
        mouse_event(4, 0, 0, 0, 0);
        if (alt) keybd_event(0x12, 0, 2, 0);
        if (shift) keybd_event(0x10, 0, 2, 0);
        await Settle();
    }

    /// <summary>Two clicks at one point within the system double-click time (the second press has ClickCount 2).</summary>
    private static async Task DoubleClick(Point2 pagePoint)
    {
        await EnsureForeground();
        Cursor(Dip(pagePoint)); await Task.Delay(80);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0);
        await Settle();
    }

    /// <summary><paramref name="count"/> presses at one point within the double-click time (click counts 1, 2, 3, …).</summary>
    private static async Task MultiClick(Point2 pagePoint, int count)
    {
        await EnsureForeground();
        Cursor(Dip(pagePoint)); await Task.Delay(80);
        for (int i = 0; i < count; i++) { mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0); await Task.Delay(60); }
        await Settle();
    }

    /// <summary>A click that moves half a point (below the click threshold).</summary>
    private static async Task ClickPage(Point2 pagePoint)
    {
        await Task.Delay(600); // never part of an earlier click run
        await Drag(pagePoint, new(pagePoint.X + .5, pagePoint.Y));
        await Settle();
    }

    private static async Task ClickButton(string name)
    {
        var button = (FrameworkElement)window.FindName(name);
        await EnsureForeground();
        var p = button.PointToScreen(new(button.ActualWidth / 2, button.ActualHeight / 2));
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y)); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0);
        await Settle();
    }

    private static Color WindowColor(Point2 pagePoint)
    {
        var inWindow = View.TranslatePoint(Dip(pagePoint), window);
        double dpi = View.DpiScale;
        var pixel = new byte[4];
        Capture().CopyPixels(new Int32Rect((int)Math.Floor(inWindow.X * dpi), (int)Math.Floor(inWindow.Y * dpi), 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    private static async Task HoverDip(Point dip)
    {
        await EnsureForeground();
        Cursor(new(dip.X - 3, dip.Y)); await Task.Delay(40);
        Cursor(dip); await Settle();
    }

    private static async Task Key(byte key, bool control = false, bool shift = false, bool keepFocus = false)
    {
        await EnsureForeground();
        // keys go where the focus is: canvas, page panel, editor or search box (an open menu keeps it: keepFocus)
        if (!keepFocus && !window.IsKeyboardFocusWithin) View.Focus();
        if (control) keybd_event(0x11, 0, 0, 0);
        if (shift) keybd_event(0x10, 0, 0, 0);
        keybd_event(key, 0, 0, 0); await Task.Delay(30); keybd_event(key, 0, 2, 0);
        if (shift) keybd_event(0x10, 0, 2, 0);
        if (control) keybd_event(0x11, 0, 2, 0);
        await Settle();
    }

    private static int regained;

    private static async Task EnsureForeground()
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (GetForegroundWindow() == handle) return;
        regained++;
        messages.Add($"foreground regained before input #{regained} (was {GetForegroundWindow()})");
        for (int attempt = 0; attempt < 5 && GetForegroundWindow() != handle; attempt++)
        { keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); window.Activate(); SetForegroundWindow(handle); await Task.Delay(200); }
        if (GetForegroundWindow() != handle) throw new InvalidOperationException("The app window could not get the foreground for real input.");
        await Settle();
    }

    private static void Cursor(Point dip)
    {
        // Never send real input outside the document view (a stray click once opened a taskbar jump list).
        if (dip.X < 0 || dip.Y < 0 || dip.X > View.ActualWidth || dip.Y > View.ActualHeight)
            throw new InvalidOperationException($"Input point {dip} is outside the document view.");
        var p = View.PointToScreen(dip);
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    private static Task Settle() => Task.Delay(150);

    private static RenderTargetBitmap Capture()
    {
        double dpi = View.DpiScale;
        var bitmap = new RenderTargetBitmap((int)Math.Round(window.ActualWidth * dpi), (int)Math.Round(window.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        return bitmap;
    }

    private static void Snapshot(string name)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Capture()));
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
