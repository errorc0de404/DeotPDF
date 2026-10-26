using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.App;
using PdfNotes.App.Canvas;
using PdfNotes.App.Tools;
using PdfNotes.App.Views;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.Probe;

/// <summary>
/// <c>d13-real</c>: the D13i, 2026-10-15 feedback and D13j user test lists (24.23, 24.24, 24.26) with OS mouse/keyboard
/// input (2026-10-16, delegated by the user while away). Favorites (click, right-click store/clear), highlighter glyph
/// snapping with real drags, underline/strikeout, sticky notes (type, Esc, reopen by click, drag), 필기 숨기기, dark pages
/// through the settings window, a pen held in single page mode while PageDown/→/wheel arrive, a text box at the click,
/// the merge dialog (▲, typed ranges, 삽입), the export dialog (typed <c>;</c> groups) and 이 페이지의 필기 삭제 from the
/// page list menu, and 파일 › PDF 합치기. File pickers are replaced; Explorer drag and drop (OLE) stays a user test.
/// </summary>
internal static class D13RealCheck
{
    private static MainWindow window = null!;
    private static string folder = "";
    private static readonly List<object> checks = [];
    private static readonly List<string> messages = [];
    private static MessageBoxResult answer = MessageBoxResult.No;
    private static DocumentSession Session => window.Document.Session;
    private static DocumentView View => (DocumentView)window.FindName("View");
    private static int page;
    private static string pdf = "";

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
                PdfNotes.App.Themes.Theme.SystemThemeOverride = () => PdfNotes.App.Themes.AppTheme.Light;
                await Open();
                await Favorites();
                await Snap();
                await Markups();
                await Notes();
                await HideNotes();
                await DarkPages();
                await HeldPen();
                await TextAtClick();
                await Merge();
                await Export();
                await ClearNotes();
                await MergeNew();
                result = 0;
            }
            catch (Exception e) { checks.Add(new { Name = "harness", Passed = false, Detail = e.ToString() }); Console.WriteLine(e); }
            finally
            {
                File.WriteAllText(Path.Combine(folder, "d13-real-result.json"), JsonSerializer.Serialize(new { Passed = result == 0, Input = "os-mouse-keyboard", Checks = checks, Messages = messages },
                    new JsonSerializerOptions { WriteIndented = true }));
                if (window is not null) { window.PromptOverride = (_, _) => MessageBoxResult.No; window.Close(); }
                app.Shutdown();
            }
        };
        app.Run();
        return result;
    }

    private static async Task Open()
    {
        window = new MainWindow { Width = 1200, Height = 860, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window.MessageSink = messages.Add;
        window.PromptOverride = (text, _) => { messages.Add("prompt: " + text); return answer; };
        window.SaveDialogOverride = name => Path.Combine(folder, "saved-" + Path.GetFileName(name));
        window.Show(); await Settle(); await Foreground();
        pdf = Path.Combine(folder, "source.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        Check("open sample PDF", await window.OpenFileAsync(pdf));
        await Settle();
        await ShowPage(0);
    }

    // ----- 24.23-3 favorites -----

    private static async Task Favorites()
    {
        var buttons = ((Panel)window.FindName("FavoritesPanel")).Children.OfType<Button>().ToArray();
        await ClickElement(buttons[1]);
        Check("favorite 2 click: red pen 1pt", window.CurrentTool == ToolKind.Pen && window.PenStyle.WidthPt == 1 && window.PenStyle.Color == new RgbColor(0xE5, 0x39, 0x35));
        int before = Session.Current.Pages[0].Annotations.Count;
        await Drag(new(80, 560), new(140, 580), new(200, 560));
        var ink = Session.Current.Pages[0].Annotations.Last() as InkStroke;
        Check("…and draws with it", Session.Current.Pages[0].Annotations.Count == before + 1 && ink is { WidthPt: 1 } && ink.Color == new RgbColor(0xE5, 0x39, 0x35));
        await ClickElement(buttons[2]);
        Check("favorite 3 click: highlighter 14pt", window.CurrentTool == ToolKind.FreeHighlight && window.HighlighterStyle.WidthPt == 14);
        buttons = ((Panel)window.FindName("FavoritesPanel")).Children.OfType<Button>().ToArray();
        await RightClick(buttons[3]);
        await ClickScreen(buttons[3].ContextMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "현재 설정으로 바꾸기"));
        Check("right-click 현재 설정으로 바꾸기 stores the highlighter in slot 4", window.Favorites[3] is { Tool: ToolKind.FreeHighlight, Style.WidthPt: 14 });
        buttons = ((Panel)window.FindName("FavoritesPanel")).Children.OfType<Button>().ToArray();
        await RightClick(buttons[3]);
        await ClickScreen(buttons[3].ContextMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "비우기"));
        Check("right-click 비우기 empties it", window.Favorites[3] is null);
        await ClickElement(((Panel)window.FindName("FavoritesPanel")).Children.OfType<Button>().First());
        Check("favorite 1: black pen 2pt", window.CurrentTool == ToolKind.Pen && window.PenStyle.WidthPt == 2);
    }

    // ----- 24.23-5 highlighter snapping with real drags -----

    private static async Task Snap()
    {
        var glyphs = window.PageGlyphs(0);
        int start = Find(glyphs, "English");
        await ClickElement(Named("HighlighterToolButton"));
        int count = Session.Current.Pages[0].Annotations.Count;
        await Drag(Steps(Edge(glyphs[start], false), Edge(glyphs[start + 6], true)));
        var made = Session.Current.Pages[0].Annotations.Last();
        Check("highlighter drag over PDF glyphs: a glyph highlight", Session.Current.Pages[0].Annotations.Count == count + 1 && made is PdfTextHighlight { SourceTextHint: "English" },
            made.GetType().Name);
        await Drag(Steps(new(100, 640), new(300, 640)));
        Check("highlighter drag on blank space: a free stroke", Session.Current.Pages[0].Annotations.Last() is FreeHighlightStroke);
        keybd_event(0x12, 0, 0, 0); await Task.Delay(60);
        await Drag(Steps(Edge(glyphs[start], false), Edge(glyphs[start + 6], true)));
        keybd_event(0x12, 0, 2, 0); await Settle();
        keybd_event(0x1B, 0, 0, 0); keybd_event(0x1B, 0, 2, 0); await Settle(); // leave any Alt menu mode
        Check("Alt + drag over glyphs: a free stroke", Session.Current.Pages[0].Annotations.Last() is FreeHighlightStroke);
        Snapshot("t1-snap.png");
    }

    // ----- 24.23-6 underline / strikeout -----

    private static async Task Markups()
    {
        var glyphs = window.PageGlyphs(0);
        await ClickElement(Named("HighlighterToolButton")); // one highlighter since D14g
        await ClickElement(Named("MarkUnderlineButton"));
        int english = Find(glyphs, "English");
        await Drag(Steps(Edge(glyphs[english], false), Edge(glyphs[english + 6], true)));
        var u = Session.Current.Pages[0].Annotations.Last() as PdfTextMarkup;
        Check("밑줄 button + drag: underline on the glyphs", u is { MarkupKind: TextMarkupKind.Underline, SourceTextHint: "English" } && Session.UndoLabel == "undo.underline", u?.ToString() ?? "none");
        await ClickElement(Named("MarkStrikeOutButton"));
        int digits = Find(glyphs, "123");
        await Drag(Steps(Edge(glyphs[digits], false), Edge(glyphs[digits + 2], true)));
        Check("취소선 button + drag: strikeout", Session.Current.Pages[0].Annotations.Last() is PdfTextMarkup { MarkupKind: TextMarkupKind.StrikeOut } && Session.UndoLabel == "undo.strikeOut");
        await ClickElement(Named("MarkHighlightButton"));
        Snapshot("t2-markups.png");
    }

    // ----- 24.23-7 sticky notes -----

    private static async Task Notes()
    {
        await ClickElement(Named("NoteToolButton"));
        await ClickPage(new(450, 420));
        Check("note tool click opens a note card for typing", window.OpenNoteAnnotation is not null && window.NoteTextBox is { IsKeyboardFocusWithin: true });
        await TypeText("회의 메모 4장");
        Snapshot("t3-note-open.png");
        await RawKey(0x1B);
        var note = Session.Current.Pages[0].Annotations.OfType<StickyNote>().SingleOrDefault();
        Check("Esc closes it; the note is added with the typed text", window.OpenNoteAnnotation is null && note is { Text: "회의 메모 4장" } && Session.UndoLabel == "undo.addNote", note?.Text ?? "none");
        await ClickElement(Named("SelectToolButton"));
        var center = new Point2(note!.Transform.OffsetX + 10, note.Transform.OffsetY + 10);
        await ClickPage(center);
        Check("a click on the icon with the selection tool opens it again", window.OpenNoteAnnotation?.Id == note.Id, window.OpenNoteAnnotation?.Id.ToString() ?? "closed");
        await RawKey(0x1B);
        await Task.Delay(400);
        await Drag(Steps(center, new(center.X + 60, center.Y + 40)));
        var moved = (StickyNote)Session.Current.FindAnnotation(note.Id)!.Annotation;
        Check("dragging the icon moves it (move only)", Math.Abs(moved.Transform.OffsetX - note.Transform.OffsetX - 60) < 2 && Math.Abs(moved.Transform.OffsetY - note.Transform.OffsetY - 40) < 2 && moved.Transform.IsTranslation,
            $"{moved.Transform.OffsetX - note.Transform.OffsetX:0.#}, {moved.Transform.OffsetY - note.Transform.OffsetY:0.#}");
        if (window.OpenNoteAnnotation is not null) await RawKey(0x1B);
        await Key(0x46, control: true);
        await TypeText("4장");
        for (int i = 0; i < 30 && (window.IsSearching || window.SearchHits.Count == 0); i++) await Settle();
        Check("Ctrl+F search for the note's text finds the note", window.SearchHits.Any(h => h.BoxId == note.Id), $"{window.SearchHits.Count} hits");
        window.CloseSearch(); await Settle();
    }

    // ----- 24.23-2 필기 숨기기 -----

    private static async Task HideNotes()
    {
        await ClickElement(Named("ViewTabButton"));
        await ClickElement(Named("HideNotesButton"));
        Check("보기 › 필기 숨기기 click hides the notes; status shows 필기 숨김", window.NotesHidden && Named("NotesHiddenText").IsVisible && window.CurrentTool == ToolKind.Select);
        Snapshot("t4-hidden.png");
        await ClickElement(Named("HideNotesButton"));
        Check("a second click shows them again", !window.NotesHidden && !Named("NotesHiddenText").IsVisible);
        await ClickElement(Named("HomeTabButton"));
    }

    // ----- 24.23-1 dark pages through the settings window -----

    private static async Task DarkPages()
    {
        await SetDarkPages(true);
        Check("settings › 화면 › 어두운 페이지 → pages shown inverted", window.DarkPagesShown && window.DisplaySettings.DarkPages);
        Snapshot("t5-dark.png");
        string saved = Path.Combine(folder, "dark-saved.pdf");
        Check("saving with dark pages on", await window.SaveToAsync(saved));
        using (var engine = new PdfNotes.Pdf.Engine.PdfiumDocument(saved))
        {
            var bitmap = engine.Render(0, 0.5);
            int at = bitmap.Height / 2 * bitmap.Stride + 16;
            Check("the saved file keeps white paper", bitmap.Bgra[at] > 240 && bitmap.Bgra[at + 1] > 240 && bitmap.Bgra[at + 2] > 240);
        }
        await SetDarkPages(false);
        Check("turned off again", !window.DarkPagesShown);
    }

    private static async Task SetDarkPages(bool on)
    {
        _ = ClickElement(Named("SettingsButton"));
        var dialog = await Modal<SettingsDialog>();
        var list = Field<ListBox>(dialog, "CategoryList");
        CheckBox? Box() => Descendants(dialog).OfType<CheckBox>().FirstOrDefault(c => c.Name == "DarkPagesBox" && c.IsVisible);
        for (int i = 0; i < list.Items.Count && Box() is null; i++)
            await ClickElement((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(i));
        var box = Box() ?? throw new InvalidOperationException("DarkPagesBox not found in any category");
        box.BringIntoView(); await Settle();
        if (box.IsChecked != on) await ClickElement(box);
        await ClickElement(Field<Button>(dialog, "OkButton"));
        await Settle();
    }

    // ----- 24.24-2 a held pen in single page mode -----

    private static async Task HeldPen()
    {
        window.SetViewMode(ViewMode.SinglePage); await Settle();
        await ShowPage(0);
        await ClickElement(Named("PenToolButton"));
        int count = Session.Current.Pages[0].Annotations.Count;
        await EnsureForeground();
        Cursor(Dip(new(100, 700))); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(60);
        Cursor(Dip(new(140, 710))); await Task.Delay(60);
        foreach (byte key in new byte[] { 0x22, 0x27, 0x28 }) { keybd_event(key, 0, 0, 0); await Task.Delay(30); keybd_event(key, 0, 2, 0); await Task.Delay(150); }
        mouse_event(0x0800, 0, 0, -120, 0); await Task.Delay(300);
        Cursor(Dip(new(200, 720))); await Task.Delay(60);
        mouse_event(4, 0, 0, 0, 0); await Settle(); await Settle();
        Check("single page view, pen held: PageDown/→/↓/wheel keep the page, the app stays up, the stroke is drawn", View.CurrentPageIndex == 0 &&
            Session.Current.Pages[0].Annotations.Count == count + 1, $"page {View.CurrentPageIndex + 1}, {Session.Current.Pages[0].Annotations.Count - count} new");
        await Key(0x22);
        Check("after release PageDown turns the page", View.CurrentPageIndex == 1);
        window.SetViewMode(ViewMode.Continuous); await Settle();
        await ShowPage(0);
    }

    // ----- 24.24-6 a new text box at the click -----

    private static async Task TextAtClick()
    {
        await ClickElement(Named("TextToolButton"));
        var click = new Point2(300, 480);
        await ClickPage(click);
        var box = window.EditingText;
        Check("text tool click: the first line starts where clicked", box is not null && Math.Abs(box.Transform.OffsetX + 4 - click.X) < 1.5 && box.Transform.OffsetY < click.Y && click.Y - box.Transform.OffsetY < 20,
            box is null ? "no editor" : $"{box.Transform.OffsetX:0.#}, {box.Transform.OffsetY:0.#}");
        await TypeText("클릭한 곳");
        await RawKey(0x1B);
        await ClickElement(Named("SelectToolButton"));
    }

    // ----- 24.26-1 merge dialog with real clicks -----

    private static async Task Merge()
    {
        string b = Path.Combine(folder, "b.pdf"), c = Path.Combine(folder, "c.pdf");
        ProbeSamplePdf.Create(b, 4);
        ProbeSamplePdf.Create(c, 5);
        window.ImportFilesDialogOverride = () => [b, c];
        await ClickElement(Named("PageTabButton"));
        await ClickThumb(0);
        int count = Session.Current.Pages.Count, undo = Session.UndoCount;
        var second = Session.Current.Pages[1].Id;
        await ClickElement(Named("InsertPageMenuButton"));
        _ = ClickScreen(window.LastInsertMenu!.Items.OfType<MenuItem>().Single(i => (string)i.Header == "PDF에서 불러오기…"));
        var dialog = await Modal<PdfMergeDialog>();
        Check("two files → the merge dialog in that order", dialog.Files.Select(f => f.Name).SequenceEqual(["b.pdf", "c.pdf"]), string.Join(",", dialog.Files.Select(f => f.Name)));
        var up = Descendants(dialog).OfType<Button>().Where(x => (string)x.ToolTip == "위로").ElementAt(1);
        await ClickElement(up);
        Check("▲ on the second row moves c.pdf up", dialog.Files.Select(f => f.Name).SequenceEqual(["c.pdf", "b.pdf"]));
        await TypeInto(Field<TextBox>(dialog, "MergeRange0"), "1-2");
        await TypeInto(Field<TextBox>(dialog, "MergeRange1"), "9");
        Check("a bad typed range names the file and disables 삽입", !dialog.CanAccept && dialog.ErrorText.StartsWith("2번 파일"), dialog.ErrorText);
        await TypeInto(Field<TextBox>(dialog, "MergeRange1"), "2, 4");
        Check("fixed: 파일 2개 · 4쪽", dialog.CanAccept && dialog.TotalText == "파일 2개 · 4쪽", dialog.TotalText);
        Snapshot(dialog, "t6-merge.png");
        await ClickElement(Field<Button>(dialog, "MergeAcceptButton"));
        for (int i = 0; i < 30 && Session.Current.Pages.Count == count; i++) await Task.Delay(200);
        Check("삽입: four pages after the current one, one Undo", Session.Current.Pages.Count == count + 4 && Session.Current.Pages[5].Id == second && Session.UndoCount == undo + 1,
            $"{Session.Current.Pages.Count} pages");
        await Key(0x5A, control: true);
        Check("Ctrl+Z takes all of them out", Session.Current.Pages.Count == count && Session.Current.Pages[1].Id == second);
    }

    // ----- 24.26-4 export dialog with typed groups -----

    private static async Task Export()
    {
        string outBase = Path.Combine(folder, "out.pdf");
        window.SaveDialogOverride = _ => outBase;
        await RightClickThumb(0);
        _ = ClickScreen(MenuItem(window.FindName("PageList") is ListBox l ? l.ContextMenu : null, "페이지 내보내기…"));
        var dialog = await Modal<PageExportDialog>();
        await TypeInto(Field<TextBox>(dialog, "ExportRangeBox"), "1,3; 2");
        Check("typed 1,3; 2 → 파일 2개 · 3쪽", dialog.SummaryText == "파일 2개 · 3쪽", dialog.SummaryText);
        Snapshot(dialog, "t7-export.png");
        await ClickElement(Field<Button>(dialog, "ExportAcceptButton"));
        string o1 = Path.Combine(folder, "out_1.pdf"), o2 = Path.Combine(folder, "out_2.pdf");
        for (int i = 0; i < 40 && !(File.Exists(o1) && File.Exists(o2)); i++) await Task.Delay(200);
        await Settle();
        Check("내보내기 writes out_1 and out_2", File.Exists(o1) && File.Exists(o2));
        using var f1 = PdfSourceDocument.Open(o1);
        using var f2 = PdfSourceDocument.Open(o2);
        var m1 = f1.CreateModel();
        Check("out_1: pages 1 and 3 with page 1's notes editable; out_2: page 2", m1.Pages.Count == 2 && m1.Pages[0].Annotations.Count == Session.Current.Pages[0].Annotations.Count &&
            f2.CreateModel().Pages.Count == 1, $"{m1.Pages.Count} / {m1.Pages[0].Annotations.Count}");
        window.SaveDialogOverride = name => Path.Combine(folder, "saved-" + Path.GetFileName(name));
    }

    // ----- 24.26-5 이 페이지의 필기 삭제 from the page list menu -----

    private static async Task ClearNotes()
    {
        int notes = Session.Current.Pages[0].Annotations.Count, undo = Session.UndoCount;
        Check("page 1 has notes", notes > 3, notes.ToString());
        answer = MessageBoxResult.No;
        await RightClickThumb(0);
        await ClickScreen(MenuItem(((ListBox)window.FindName("PageList")).ContextMenu, "이 페이지의 필기 삭제"));
        Check("No keeps them (asked once)", Session.Current.Pages[0].Annotations.Count == notes && messages.Last().StartsWith("prompt: 이 페이지의 필기"), messages.Last());
        answer = MessageBoxResult.Yes;
        await RightClickThumb(0);
        await ClickScreen(MenuItem(((ListBox)window.FindName("PageList")).ContextMenu, "이 페이지의 필기 삭제"));
        Check("Yes deletes every note of page 1 as one Undo", Session.Current.Pages[0].Annotations.Count == 0 && Session.UndoCount == undo + 1);
        await ClickElement(Named("PageTabButton"));
        await Key(0x5A, control: true);
        Check("Ctrl+Z brings them back", Session.Current.Pages[0].Annotations.Count == notes);
        await ClickThumb(0);
        await ClickElement(Named("ClearPageNotesButton"));
        Check("페이지 탭 필기 삭제 does the same", Session.Current.Pages[0].Annotations.Count == 0);
        await Key(0x5A, control: true);
        answer = MessageBoxResult.No;
    }

    // ----- 24.26-3 파일 › PDF 합치기 -----

    private static async Task MergeNew()
    {
        string b = Path.Combine(folder, "b.pdf"), c = Path.Combine(folder, "c.pdf");
        window.ImportFilesDialogOverride = () => [b, c];
        await ClickElement(Named("FileTabButton"));
        _ = ClickElement(Named("MergePdfsButton"));
        var dialog = await Modal<PdfMergeDialog>();
        Check("PDF 합치기 opens the merge dialog with 만들기", dialog.Title == "PDF 합치기" && Field<Button>(dialog, "MergeAcceptButton").Content as string == "만들기");
        await ClickElement(Field<Button>(dialog, "MergeAcceptButton"));
        for (int i = 0; i < 30 && Session.Current.Pages.Count != 9; i++) await Task.Delay(200);
        Check("a new unsaved document with all 9 pages", Session.Current.Pages.Count == 9 && Session.IsDirty && window.Document.FilePath is null, $"{Session.Current.Pages.Count} pages");
        string merged = Path.Combine(folder, "merged.pdf");
        window.SaveDialogOverride = _ => merged;
        await Key(0x53, control: true);
        for (int i = 0; i < 30 && Session.IsDirty; i++) await Task.Delay(200);
        using var engine = new PdfNotes.Pdf.Engine.PdfiumDocument(merged);
        Check("Ctrl+S saves the merged PDF (9 pages)", !Session.IsDirty && engine.PageCount == 9);
    }

    // ----- helpers -----

    private static MenuItem MenuItem(ContextMenu? menu, string header) =>
        menu!.Items.OfType<MenuItem>().Single(i => i.Header as string == header);

    private static async Task RightClickThumb(int index)
    {
        var list = (ListBox)window.FindName("PageList");
        list.ScrollIntoView(list.Items[index]); await Settle();
        var item = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(index);
        await RightClick(item);
        for (int i = 0; i < 10 && list.ContextMenu?.IsOpen != true; i++) await Task.Delay(100);
        await Settle();
    }

    private static async Task ClickThumb(int index)
    {
        var list = (ListBox)window.FindName("PageList");
        list.ScrollIntoView(list.Items[index]); await Settle();
        await ClickElement((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(index));
        Check($"thumbnail {index + 1} selected", window.SelectedPageIds.SequenceEqual([Session.Current.Pages[index].Id]));
    }

    /// <summary>A real click into a dialog field, Ctrl+A and typed text (no foreground change to the main window).</summary>
    private static async Task TypeInto(TextBox field, string text)
    {
        await ClickElement(field);
        keybd_event(0x11, 0, 0, 0); keybd_event(0x41, 0, 0, 0); keybd_event(0x41, 0, 2, 0); keybd_event(0x11, 0, 2, 0); await Settle();
        foreach (char ch in text)
        {
            SendInput(2, [new Input { Type = 1, Scan = ch, Flags = 4 }, new Input { Type = 1, Scan = ch, Flags = 6 }], Marshal.SizeOf<Input>());
            await Task.Delay(40);
        }
        await Settle(); await Settle();
    }

    private static int Find(IReadOnlyList<TextGlyph> glyphs, string text) =>
        string.Concat(glyphs.Select(g => g.Text.Length == 1 ? g.Text : "?")).IndexOf(text, StringComparison.Ordinal);

    private static Point2 Edge(TextGlyph g, bool right)
    {
        var q = g.Box;
        Point2 l = new((q.UpperLeft.X + q.LowerLeft.X) / 2, (q.UpperLeft.Y + q.LowerLeft.Y) / 2), r = new((q.UpperRight.X + q.LowerRight.X) / 2, (q.UpperRight.Y + q.LowerRight.Y) / 2);
        double t = right ? 0.9 : 0.1;
        return new(l.X + (r.X - l.X) * t, l.Y + (r.Y - l.Y) * t);
    }

    private static Point2[] Steps(Point2 a, Point2 b) => [.. Enumerable.Range(0, 9).Select(i => new Point2(a.X + (b.X - a.X) * i / 8, a.Y + (b.Y - a.Y) * i / 8))];

    private static void Check(string name, bool passed, string detail = "")
    {
        checks.Add(new { Name = name, Passed = passed, Detail = detail });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        if (!passed) throw new InvalidOperationException(name + ": " + detail);
    }

    private static FrameworkElement Named(string name) => (FrameworkElement)window.FindName(name);

    private static async Task<T> Modal<T>() where T : Window
    {
        for (int i = 0; i < 50; i++) { if (Application.Current.Windows.OfType<T>().FirstOrDefault(w => w.IsVisible) is { } modal) { await Task.Delay(500); return modal; } await Task.Delay(100); }
        throw new InvalidOperationException("Modal did not open: " + typeof(T).Name);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var c = VisualTreeHelper.GetChild(parent, i); yield return c; foreach (var d in Descendants(c)) yield return d; }
    }

    private static T Field<T>(Window dialog, string name) where T : FrameworkElement => Descendants(dialog).OfType<T>().Single(c => c.Name == name);

    private static async Task ClickElement(FrameworkElement element)
    {
        await Task.Delay(400);
        var owner = Window.GetWindow(element);
        if (owner == window) await EnsureForeground();
        if (!element.IsVisible || element.ActualWidth == 0) throw new InvalidOperationException($"Element is not visible: {element.Name} {element}");
        var p = element.PointToScreen(new(element.ActualWidth / 2, element.ActualHeight / 2));
        var topLeft = owner.PointToScreen(new(0, 0)); var bottomRight = owner.PointToScreen(new(owner.ActualWidth, owner.ActualHeight));
        if (p.X < topLeft.X || p.Y < topLeft.Y || p.X > bottomRight.X || p.Y > bottomRight.Y) throw new InvalidOperationException($"Click point {p} is outside its window.");
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y)); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0);
        await Settle();
    }

    private static async Task ClickScreen(FrameworkElement element)
    {
        await Task.Delay(400);
        if (!element.IsVisible || element.ActualWidth == 0) throw new InvalidOperationException($"Element is not visible: {element}");
        var p = element.PointToScreen(new(element.ActualWidth / 2, element.ActualHeight / 2));
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y)); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0);
        await Settle();
    }

    private static async Task RightClick(FrameworkElement element)
    {
        await Task.Delay(400);
        await EnsureForeground();
        var p = element.PointToScreen(new(element.ActualWidth / 2, element.ActualHeight / 2));
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y)); await Task.Delay(60);
        mouse_event(8, 0, 0, 0, 0); await Task.Delay(30); mouse_event(0x10, 0, 0, 0, 0);
        await Settle(); await Settle();
    }

    private static async Task RawKey(byte key)
    {
        keybd_event(key, 0, 0, 0); await Task.Delay(30); keybd_event(key, 0, 2, 0);
        await Settle(); await Settle();
    }

    private static async Task ShowPage(int index)
    {
        page = index;
        View.SetZoom(0.6); await Settle();
        View.GoToPage(index); await Settle(); await Settle();
    }

    private static Point Dip(Point2 pagePoint)
    {
        var p = View.PageTransform(page).PageToDip.Transform(pagePoint);
        return new(p.X, p.Y);
    }

    private static async Task Drag(params Point2[] points)
    {
        await EnsureForeground();
        var dips = points.Select(Dip).ToArray();
        Cursor(dips[0]); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(50);
        foreach (var p in dips.Skip(1)) { Cursor(p); await Task.Delay(25); }
        await Task.Delay(40);
        mouse_event(4, 0, 0, 0, 0);
        await Settle();
    }

    private static async Task ClickPage(Point2 pagePoint)
    {
        await Task.Delay(600);
        await EnsureForeground();
        Cursor(Dip(pagePoint)); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(40); mouse_event(4, 0, 0, 0, 0);
        await Settle(); await Settle();
    }

    private static async Task TypeText(string text)
    {
        foreach (char c in text)
        {
            SendInput(2, [new Input { Type = 1, Scan = c, Flags = 4 }, new Input { Type = 1, Scan = c, Flags = 6 }], Marshal.SizeOf<Input>());
            await Task.Delay(40);
        }
        await Settle();
    }

    private static async Task Key(byte key, bool control = false)
    {
        await EnsureForeground();
        if (!window.IsKeyboardFocusWithin) View.Focus();
        if (control) keybd_event(0x11, 0, 0, 0);
        keybd_event(key, 0, 0, 0); await Task.Delay(30); keybd_event(key, 0, 2, 0);
        if (control) keybd_event(0x11, 0, 2, 0);
        await Settle(); await Settle();
    }

    private static async Task Foreground()
    {
        var handle = new WindowInteropHelper(window).Handle;
        SetForegroundWindow(handle); window.Activate(); await Settle();
        for (int attempt = 0; attempt < 5 && GetForegroundWindow() != handle; attempt++)
        { keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); window.Activate(); SetForegroundWindow(handle); await Task.Delay(200); }
        if (GetForegroundWindow() != handle) { window.Topmost = true; window.Activate(); SetForegroundWindow(handle); await Settle(); window.Topmost = false; }
        Check("foreground", GetForegroundWindow() == handle, $"target={handle}, actual={GetForegroundWindow()}");
    }

    private static async Task EnsureForeground()
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (GetForegroundWindow() == handle) return;
        messages.Add($"foreground regained (was {GetForegroundWindow()})");
        for (int attempt = 0; attempt < 5 && GetForegroundWindow() != handle; attempt++)
        { keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); window.Activate(); SetForegroundWindow(handle); await Task.Delay(200); }
        if (GetForegroundWindow() != handle) throw new InvalidOperationException("The app window could not get the foreground for real input.");
        await Settle();
    }

    private static void Cursor(Point dip)
    {
        if (dip.X < 0 || dip.Y < 0 || dip.X > View.ActualWidth || dip.Y > View.ActualHeight)
            throw new InvalidOperationException($"Input point {dip} is outside the document view.");
        var p = View.PointToScreen(dip);
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    private static Task Settle() => Task.Delay(200);

    private static void Snapshot(string name) => Snapshot(window, name);

    private static void Snapshot(Window w, string name)
    {
        double dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Round(w.ActualWidth * dpi), (int)Math.Round(w.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(w);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
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
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
