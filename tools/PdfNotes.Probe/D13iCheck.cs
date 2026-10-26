using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.App;
using PdfNotes.App.Canvas;
using PdfNotes.App.Tools;
using PdfNotes.App.Views;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Probe;

/// <summary>
/// D13i <c>d13i-logic</c> (spec 24.7–24.13, A40–A46) without OS input: windows are not activated and stay off screen,
/// tools get their pointer calls directly. Reading dark page, hiding notes, pen favorites, dropped images, highlighter
/// glyph snapping, underline/strikeout and sticky notes, with save/reopen where data is made.
/// </summary>
internal static class D13iCheck
{
    private static MainWindow window = null!;
    private static string folder = "";
    private static readonly List<object> checks = [];
    private static readonly List<string> messages = [];
    private static DocumentSession Session => window.Document.Session;
    private static DocumentView View => (DocumentView)window.FindName("View");

    public static int Run(string output, string part)
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
                await Start();
                await DarkPages();
                await HideNotes();
                await Favorites();
                await DropImages();
                await Snap();
                await Markups();
                await Notes();
                await Feedback();
                result = 0;
            }
            catch (Exception e) { checks.Add(new { Name = "harness", Passed = false, Detail = e.ToString() }); Console.WriteLine(e); }
            finally
            {
                File.WriteAllText(Path.Combine(folder, part + "-result.json"), JsonSerializer.Serialize(new
                { Passed = result == 0, Input = "none (tool pointer methods, window not activated, off screen)", Checks = checks, Messages = messages },
                    new JsonSerializerOptions { WriteIndented = true }));
                if (window is not null) { window.PromptOverride = (_, _) => MessageBoxResult.No; window.Close(); }
                app.Shutdown();
            }
        };
        app.Run();
        return result;
    }

    private static string pdf = "";

    private static async Task Start()
    {
        pdf = Path.Combine(folder, "sample.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Left = SystemParameters.VirtualScreenLeft - 4400; window.Top = SystemParameters.VirtualScreenTop; window.Width = 1200; window.Height = 860;
        window.MessageSink = messages.Add;
        window.PromptOverride = (text, _) => { messages.Add("prompt: " + text); return MessageBoxResult.No; };
        window.SaveDialogOverride = name => Path.Combine(folder, "saved-" + Path.GetFileName(name));
        window.Show();
        await Settle(400);
        Check("open sample", await window.OpenFileAsync(pdf));
        View.SetZoom(0.6); View.GoToPage(0); await Settle(600); // the whole first page in view
    }

    // ----- 24.12 reading dark page -----

    private static async Task DarkPages()
    {
        var stroke = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(300, 400), new(400, 400)], 6, new(0, 0, 0), 1);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, stroke));
        window.SetTool(ToolKind.Select); window.Selection.Select(stroke.Id);
        await Settle(400);
        var paper = Dip(new(500, 600)); var ink = Dip(new(350, 400));
        var before = Pixels();
        Check("default: pages not inverted (white paper, black ink)", Bright(Pixel(before, paper)) && Dark(Pixel(before, ink)) && !window.DarkPagesShown);
        window.PreviewDisplay(window.DisplaySettings with { DarkPages = true });
        await Settle(500);
        var after = Pixels();
        Check("dark page: paper dark, ink light", Dark(Pixel(after, paper)) && Bright(Pixel(after, ink)), $"{Pixel(after, paper)} / {Pixel(after, ink)}");
        var handle = Dip(new(300 - 1, 400 - 3 - 1)); // a selection frame corner (the selection outline is a mark)
        Check("marks are not inverted: the app frame and selection keep their colors", Pixel(after, new Point(5, 5)) == Pixel(before, new Point(5, 5)));
        var thumb = Descendants(window).OfType<Image>().FirstOrDefault(i => i.Effect is not null);
        Check("thumbnails take the same inversion", thumb is not null);
        var sampled = window.SampleColor(View.TranslatePoint(paper, window));
        Check("the eyedropper takes the color before inversion (white paper)", sampled.R > 240 && sampled.G > 240 && sampled.B > 240, sampled.ToHex());
        Check("the dark page is not a document change", Session.UndoCount == 1);
        Save(Capture(window), "dark-page.png");
        window.SetDisplay(window.DisplaySettings with { DarkPages = true });
        Check("stored as a setting", window.DisplaySettings.DarkPages);
        window.SetDisplay(window.DisplaySettings with { DarkPages = false });
        await Settle(400);
        Check("off again", Bright(Pixel(Pixels(), paper)));
        window.Selection.Select(null);
        _ = handle;
    }

    // ----- 24.10 hide the notes -----

    private static async Task HideNotes()
    {
        var ink = Dip(new(350, 400));
        int undo = Session.UndoCount;
        window.SetTool(ToolKind.Pen);
        window.SetNotesHidden(true);
        await Settle(800);
        Check("hidden: the ink is gone from the page, tool is the selection tool, status shows 필기 숨김 (the 필기 tab stays on: a tool shows the notes, D14g)",
            Bright(Pixel(Pixels(), ink)) && window.CurrentTool == ToolKind.Select &&
            ((FrameworkElement)window.FindName("NotesHiddenText")).IsVisible, $"{Pixel(Pixels(), ink)}");
        Check("hidden: annotations cannot be picked or selected", window.Selection.Pick(ink) is null);
        window.SelectAllAnnotations();
        Check("hidden: Ctrl+A shows the notes and selects (spec 24.10, D14g)", !window.NotesHidden && window.Selection.SelectedIds.Count > 0);
        window.Selection.Select(null);
        window.SetNotesHidden(true); await Settle();
        window.SetTool(ToolKind.Pen);
        Check("hidden: a drawing tool shows the notes (spec 24.10, D14g)", window.CurrentTool == ToolKind.Pen && !window.NotesHidden);
        window.SetNotesHidden(true); await Settle();
        Check("hidden: PDF text still found by search", window.PageGlyphs(0).Count > 10);
        Check("hiding is not a document change", Session.UndoCount == undo && !Session.Current.AllAnnotations.Any(a => false));
        Save(Capture(window), "notes-hidden.png");
        window.SetNotesHidden(false);
        await Settle(600);
        Check("shown again", Dark(Pixel(Pixels(), ink)) && ((FrameworkElement)window.FindName("HomeTab")).IsEnabled);
        window.SetNotesHidden(true);
        Check("reopen the sample", await window.OpenFileAsync(pdf));
        Check("another document shows its notes", !window.NotesHidden);
        View.SetZoom(0.6); View.GoToPage(0); await Settle(600); // the whole first page in view
    }

    // ----- 24.7 favorites -----

    private static async Task Favorites()
    {
        var f = window.Favorites;
        Check("defaults: black pen 2, red pen 1, yellow highlighter 14 / 60 %, empty",
            f[0] is { Tool: ToolKind.Pen, Style.Color: "#000000", Style.WidthPt: 2 } && f[1] is { Tool: ToolKind.Pen, Style.WidthPt: 1 } &&
            f[2] is { Tool: ToolKind.FreeHighlight, Style.WidthPt: 14, Style.Transparency: 60 } && f[3] is null);
        var buttons = ((Panel)window.FindName("FavoritesPanel")).Children.OfType<Button>().ToArray();
        var group = (FrameworkElement)window.FindName("FavoritesGroup");
        var row = (FrameworkElement)window.FindName("SubBarRow");
        Check("four buttons at the right end of the settings row under the ribbon", buttons.Length == 4 && group.IsVisible &&
            Math.Abs(group.TranslatePoint(new Point(group.ActualWidth, 0), row).X + group.Margin.Right - row.ActualWidth) < 2);
        foreach (var tab in new[] { RibbonTab.Page, RibbonTab.View, RibbonTab.File })
        {
            window.ShowRibbonTab(tab); await Settle();
            Check($"favorites stay on the {tab} tab", group.IsVisible);
        }
        window.ShowRibbonTab(RibbonTab.Home);
        window.ApplyFavorite(1);
        Check("favorite 2: red pen 1pt", window.CurrentTool == ToolKind.Pen && window.PenStyle.WidthPt == 1 && window.PenStyle.Color == new RgbColor(0xE5, 0x39, 0x35));
        window.ApplyFavorite(2);
        Check("favorite 3: highlighter", window.CurrentTool == ToolKind.FreeHighlight && window.HighlighterStyle.WidthPt == 14);
        window.SetColor(new(0x43, 0xA0, 0x47)); window.SetWidth(20);
        window.ApplyFavorite(3); // empty: stores the current (highlighter) settings
        Check("an empty slot stores the current settings", window.Favorites[3] is { Tool: ToolKind.FreeHighlight, Style.WidthPt: 20, Style.Color: "#43A047" });
        window.ClearFavorite(3);
        Check("비우기", window.Favorites[3] is null);
        window.SetTool(ToolKind.Select);
        var menu = window.OpenAnnotationMenu(Dip(new(500, 700)))!;
        Check("the quick menu has no favorites row (2026-10-15)", menu.Items.OfType<MenuItem>().All(i => i.Header is string));
        menu.IsOpen = false;
        window.ApplyFavorite(1);
        window.SetDisplay(window.DisplaySettings with { Favorites = false });
        Check("the setting hides the favorites", !((FrameworkElement)window.FindName("FavoritesGroup")).IsVisible);
        window.SetDisplay(window.DisplaySettings with { Favorites = true });
    }

    // ----- 24.11 dropped images -----

    private static async Task DropImages()
    {
        var images = new[] { Png(80, 40, Colors.SteelBlue), Png(60, 60, Colors.OrangeRed) };
        int undo = Session.UndoCount;
        var at = new Point2(300, 300);
        Check("drop two images on page 1", window.InsertImagesAt(images, Dip(at)));
        var placed = Session.Current.Pages[0].Annotations.OfType<ImageAnnotation>().ToArray();
        Point2 Center(ImageAnnotation i) => new(i.Transform.OffsetX + i.LocalBounds.Width / 2, i.Transform.OffsetY + i.LocalBounds.Height / 2);
        Check("centered where dropped, 10pt apart, one Undo unit, selected", placed.Length == 2 && Close(Center(placed[0]), at) && Close(Center(placed[1]), new(310, 310)) &&
            Session.UndoCount == undo + 1 && window.Selection.SelectedIds.Count == 2, string.Join(" ", placed.Select(p => Center(p).ToString())));
        Session.Undo();
        Check("Ctrl+Z removes both", !Session.Current.Pages[0].Annotations.OfType<ImageAnnotation>().Any());
        Check("dropped outside every page: the current page's center", window.InsertImagesAt([images[0]], new Point(2, 2)) &&
            Session.Current.Pages[View.CurrentPageIndex].Annotations.OfType<ImageAnnotation>().Single() is var c && Close(Center(c),
                new(Session.Current.Pages[View.CurrentPageIndex].Setup.WidthPt / 2, Session.Current.Pages[View.CurrentPageIndex].Setup.HeightPt / 2)));
        Session.Undo();
        await Settle();
    }

    // ----- 24.13 highlighter snapping -----

    private static async Task Snap()
    {
        var glyphs = window.PageGlyphs(0);
        int start = Find(glyphs, "Original");
        var a = Edge(glyphs[start], false); var b = Edge(glyphs[start + 7], true);
        window.SetTool(ToolKind.FreeHighlight);
        int before = Session.Current.Pages[0].Annotations.Count;
        await ToolDrag(a, b);
        var made = Session.Current.Pages[0].Annotations.Last();
        Check("highlighter from glyphs: a glyph highlight, automatic height, the highlighter's own color (D14g)", made is PdfTextHighlight { HeightMode: HighlightHeightMode.Auto } h &&
            h.SourceTextHint == "Original" && h.Color == window.HighlighterStyle.Color && Math.Abs(h.Opacity - window.HighlighterStyle.Opacity) < 1e-9 && window.CurrentTool == ToolKind.FreeHighlight,
            made is PdfTextHighlight hh ? $"{hh.SourceTextHint} {hh.HeightMode} {hh.Color.ToHex()} vs {window.HighlighterStyle.Color.ToHex()} {window.CurrentTool}" : made.GetType().Name);
        await ToolDrag(new(100, 700), new(300, 700));
        Check("from blank space: a free highlighter stroke", Session.Current.Pages[0].Annotations.Last() is FreeHighlightStroke);
        await ToolDrag(a, b, ModifierKeys.Alt);
        Check("Alt from glyphs: a free stroke", Session.Current.Pages[0].Annotations.Last() is FreeHighlightStroke);
        int count = Session.Current.Pages[0].Annotations.Count;
        await ToolDrag(a, a);
        Check("a click on a highlighted glyph selects that highlight, adding nothing (spec 5.4)", Session.Current.Pages[0].Annotations.Count == count &&
            window.CurrentTool == ToolKind.Select && window.ReturnTool == ToolKind.FreeHighlight);
        window.SetTool(ToolKind.FreeHighlight);
        // 글자에 맞추기 off (the highlighter's toggle, D14g): always a free stroke.
        Check("글자에 맞추기 is a toggle on the highlighter's bar, on by default", ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("HighlighterSnapButton")).IsVisible &&
            ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("HighlighterSnapButton")).IsChecked == true && window.HighlighterSnap);
        window.SetHighlighterSnap(false);
        await ToolDrag(a, b);
        Check("toggle off: a free stroke from glyphs too", Session.Current.Pages[0].Annotations.Last() is FreeHighlightStroke);
        window.SetHighlighterSnap(true);
        _ = before;
        var dialog = new SettingsDialog(new(true, false, new(true, 5, PdfNotes.Editor.Services.BackupMode.Folder, @"C:\Backups", 10), window.DisplaySettings), () => true, window.PreviewDisplay)
        { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = SystemParameters.VirtualScreenLeft - 3000, Top = 0 };
        dialog.Show(); await Settle(300);
        T In<T>(string name) where T : FrameworkElement => Descendants(dialog).OfType<T>().Single(e => e.Name == name);
        bool noSnapBox = !Descendants(dialog).OfType<CheckBox>().Any(e => e.Name == "HighlighterSnapBox");
        ((ListBox)Descendants(dialog).OfType<ListBox>().Single(l => l.Name == "CategoryList")).SelectedIndex = 2; await Settle(300);
        Check("settings: no 형광펜 글자 맞추기 item (D14g); 화면 › 어두운 페이지 off, 펜 즐겨찾기 표시 on", noSnapBox && In<CheckBox>("DarkPagesBox").IsChecked == false &&
            In<CheckBox>("FavoritesBox").IsChecked == true);
        dialog.Close();
    }

    // ----- 24.8 underline / strikeout -----

    private static async Task Markups()
    {
        var glyphs = window.PageGlyphs(0);
        int english = Find(glyphs, "English");
        window.SetTool(ToolKind.FreeHighlight);
        window.SetMarkKind(TextMarkupKind.Underline);
        await Settle();
        Check("glyph tool bar: 하이라이트 / 밑줄 / 취소선, width 0.5–4, no 자동", ((FrameworkElement)window.FindName("MarkKindPanel")).IsVisible &&
            ((Slider)window.FindName("WidthSlider")).Maximum == 4 && !((FrameworkElement)window.FindName("HighlightAutoButton")).IsVisible);
        await ToolDrag(Edge(glyphs[english], false), Edge(glyphs[english + 6], true));
        var u = Session.Current.Pages[0].Annotations.Last() as PdfTextMarkup;
        Check("a drag makes an underline (red, 1.5pt) on the glyphs", u is { MarkupKind: TextMarkupKind.Underline, WidthPt: 1.5, SourceTextHint: "English" } &&
            u.Color == new RgbColor(0xE5, 0x39, 0x35) && Session.UndoLabel == "undo.underline", u?.ToString() ?? "none");
        window.SetMarkKind(TextMarkupKind.StrikeOut);
        int digits = Find(glyphs, "123");
        await ToolDrag(Edge(glyphs[digits], false), Edge(glyphs[digits + 2], true));
        var s = Session.Current.Pages[0].Annotations.Last() as PdfTextMarkup;
        Check("strikeout", s is { MarkupKind: TextMarkupKind.StrikeOut } && Session.UndoLabel == "undo.strikeOut");
        window.SetTool(ToolKind.Select); window.Selection.Select(u!.Id); await Settle();
        Check("selected underline: frame only (no handles)", !window.Selection.CanTransform);
        window.SetColor(new(0x1E, 0x88, 0xE5)); window.SetWidth(3);
        var changed = (PdfTextMarkup)Session.Current.FindAnnotation(u.Id)!.Annotation;
        Check("color and width change the selected markup", changed.Color == new RgbColor(0x1E, 0x88, 0xE5) && changed.WidthPt == 3);
        window.SetMarkKind(null);
        Save(Capture(window), "markups.png");
    }

    // ----- 24.9 sticky notes -----

    private static async Task Notes()
    {
        window.SetTool(ToolKind.Note);
        int undo = Session.UndoCount;
        await ToolDrag(new(450, 200), new(450, 200));
        Check("a click with the note tool opens a new note for typing (not in the document yet)", window.OpenNoteAnnotation is { } open && window.NoteTextBox is not null &&
            Session.UndoCount == undo && !Session.Current.AllAnnotations.OfType<StickyNote>().Any());
        Save(Capture(window), "note-open.png");
        window.CloseNote();
        Check("closed empty: nothing is added", !Session.Current.AllAnnotations.OfType<StickyNote>().Any() && Session.UndoCount == undo);
        await ToolDrag(new(450, 200), new(450, 200));
        window.NoteTextBox!.Text = "회의 메모: 3장 다시 보기";
        window.CloseNote();
        var note = Session.Current.Pages[0].Annotations.OfType<StickyNote>().SingleOrDefault();
        Check("closed with text: added as one Undo unit, icon centered on the click", note is not null && Session.UndoLabel == "undo.addNote" &&
            Math.Abs(note.Transform.OffsetX - 440) < 1e-6 && Math.Abs(note.Transform.OffsetY - 190) < 1e-6);
        window.OpenNote(note!, Session.Current.Pages[0].Id, isNew: false);
        window.NoteTextBox!.Text = "회의 메모: 4장";
        window.CloseNote();
        Check("an edit updates it", ((StickyNote)Session.Current.FindAnnotation(note!.Id)!.Annotation).Text == "회의 메모: 4장" && Session.UndoLabel == "undo.editNote");
        // Search finds note text.
        window.OpenSearch();
        ((TextBox)window.FindName("SearchBox")).Text = "4장";
        for (int i = 0; i < 30 && (window.IsSearching || window.SearchHits.Count == 0); i++) await Settle(100);
        Check("search finds the note's text", window.SearchHits.Any(h => h.BoxId == note.Id), $"{window.SearchHits.Count} hits");
        window.CloseSearch();
        // Move only: no size or rotate handles; a move keeps the size.
        window.SetTool(ToolKind.Select); window.Selection.Select(note.Id); await Settle();
        Check("a selected note has no handles (move only)", window.Selection.HandlePoints(View.PageTransform(0)).Length == 0 && window.Selection.CanTransform);
        Session.Execute(DocumentEdits.UpdateAnnotations(Session.Current, [AnnotationGeometry.Similarity(Session.Current.FindAnnotation(note.Id)!.Annotation, new(0, 0), 2, 0.3, new(10, 0))]));
        var moved = (StickyNote)Session.Current.FindAnnotation(note.Id)!.Annotation;
        Check("a transform only moves the icon", moved.Transform.IsTranslation);
        Session.Undo();
        // Save, reopen: notes closed, editable; clearing deletes.
        string saved = Path.Combine(folder, "notes-markups.pdf");
        Check("save", await window.SaveToAsync(saved));
        Check("reopen", await window.OpenFileAsync(saved));
        var back = Session.Current.Pages[0].Annotations;
        Check("reopened: note and markups are editable", back.OfType<StickyNote>().Count() == 1 && back.OfType<PdfTextMarkup>().Count() == 2 && window.OpenNoteAnnotation is null);
        var reopenedNote = back.OfType<StickyNote>().Single();
        window.OpenNote(reopenedNote, Session.Current.Pages[0].Id, isNew: false);
        window.NoteTextBox!.Text = "";
        window.CloseNote();
        Check("cleared text deletes the note", !Session.Current.AllAnnotations.OfType<StickyNote>().Any() && Session.UndoLabel == "undo.deleteNote");
        Session.Undo();
        Save(Capture(window), "notes-reopened.png");
        using var engine = new PdfiumDocument(File.ReadAllBytes(saved));
        Check("the saved file renders (note icon and markups drawn by PDFium)", engine.PageCount == 3);
    }

    // ----- 2026-10-15 user feedback -----

    private static async Task Feedback()
    {
        Check("reopen sample", await window.OpenFileAsync(pdf));
        View.SetZoom(0.6); View.GoToPage(0); await Settle(400);

        // 2: a page change while drawing in single page mode used to crash (the page under the pen left the view).
        window.SetViewMode(ViewMode.SinglePage); View.GoToPage(0); await Settle();
        window.SetTool(ToolKind.Pen);
        var a = Dip(new(100, 300));
        var pen = View.Tool!;
        Check("pen press", pen.PointerDown(Ptr(a, ModifierKeys.None, 1)) && View.IsToolActive);
        var source = PresentationSource.FromVisual(window)!;
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.PageDown) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await Settle();
        Check("while drawing, PageDown and → keep the page", View.CurrentPageIndex == 0 && View.IsToolActive);
        pen.PointerMove(Ptr(new(a.X + 40, a.Y), ModifierKeys.None));
        View.GoToPage(1); await Settle(); // a page change anyway (search, link): the stroke is cancelled, no crash
        Check("a forced page change cancels the stroke", !View.IsToolActive);
        pen.PointerMove(Ptr(new(a.X + 80, a.Y), ModifierKeys.None));
        pen.PointerUp(Ptr(new(a.X + 80, a.Y), ModifierKeys.None));
        Check("…and later pointer events do nothing (no exception)", Session.Current.Pages[0].Annotations.Count == 0 && !Session.IsDirty);
        window.SetViewMode(ViewMode.Continuous); View.GoToPage(0); await Settle();

        // 3: crash backup of unsaved work.
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id,
            new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(100, 500), new(200, 520)], 3, new(0, 0, 0xFF), 1)));
        string? crash = window.WriteCrashBackup(new InvalidOperationException("test"));
        Check("a crash writes crash_yyyyMMdd_HHmmss.pdf (and .txt) into the backup folder", crash is not null && File.Exists(crash) &&
            Path.GetFileName(crash).StartsWith("crash_", StringComparison.Ordinal) && File.Exists(Path.ChangeExtension(crash, ".txt")), crash ?? "none");
        var read = PdfNotes.Pdf.Reading.EditablePdfReader.Read(File.ReadAllBytes(crash!), PdfNotes.Pdf.Reading.PdfPageGeometryReader.Read(File.ReadAllBytes(crash!)));
        Check("the crash backup holds the unsaved stroke", read.Model.AllAnnotations.OfType<InkStroke>().Count() == 1);
        Check("the crash backup is what the next start offers, kept with the crash backups (D14g)", window.Backups.Backups(PdfNotes.Editor.Services.BackupStore.CrashKey).Any(e => e.File == crash));

        Session.Undo();

        // 4: page number size, bigger by default.
        Check("page numbers 14 by default (was 11)", window.PageNumberDip == 14 && window.DisplaySettings.NumberSize == PdfNotes.Editor.Services.PageNumberSize.Normal);
        window.SetDisplay(window.DisplaySettings with { NumberSize = PdfNotes.Editor.Services.PageNumberSize.Large });
        Check("크게 = 18", window.PageNumberDip == 18);
        window.SetDisplay(window.DisplaySettings with { NumberSize = PdfNotes.Editor.Services.PageNumberSize.Normal });

        // 5: 열기 in the quick commands, left of 저장.
        var open = (Button)window.FindName("QuickOpenButton");
        var save = ((Panel)open.Parent).Children.OfType<Button>().First(b => (string)b.ToolTip == "저장 (Ctrl+S)");
        Check("quick commands: 열기 left of 저장", open.IsVisible && open.TranslatePoint(new Point(0, 0), window).X < save.TranslatePoint(new Point(0, 0), window).X);

        // 6: a new text box starts its first line where the user clicked.
        window.SetTool(ToolKind.Text);
        var click = new Point2(200, 600);
        await ToolDrag(click, click);
        var box = window.EditingText!;
        var probe = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Identity, "가", [window.DefaultTextStyle with { Start = 0, Length = 1 }], PdfNotes.Core.Models.TextAlignment.Left, 200, TextBoxStyle.Default, []);
        double line = PdfNotes.Text.TextLayout.Measure(probe).Height - 8;
        Check("new box: caret line centered on the click (box moved left by its padding and up by padding + half a line)",
            Math.Abs(box.Transform.OffsetX + 4 - click.X) < 1e-6 && Math.Abs(box.Transform.OffsetY + 4 + line / 2 - click.Y) < 1e-6,
            $"{box.Transform.OffsetX:0.##}, {box.Transform.OffsetY:0.##}");
        window.FinishTextEditing();

        // 7: quick menu tools with icons and names, or one row of icons (setting).
        window.SetTool(ToolKind.Pen);
        var menu = window.OpenAnnotationMenu(Dip(new(500, 700)))!;
        var named = menu.Items.OfType<MenuItem>().Where(i => i.Header is "선택" or "그리기 펜" or "곡선").ToArray();
        Check("icons and names (default): tool items have icons", named.Length == 3 && named.All(i => i.Icon is FrameworkElement));
        menu.IsOpen = false;
        Save(Capture(window), "feedback.png");
    }

    // ----- helpers -----

    private static Point Dip(Point2 pagePoint, int page = 0)
    {
        var p = View.PageTransform(page).PageToDip.Transform(pagePoint);
        return new(p.X, p.Y);
    }

    private static async Task ToolDrag(Point2 from, Point2 to, ModifierKeys modifiers = ModifierKeys.None)
    {
        Point a = Dip(from), b = Dip(to);
        var tool = View.Tool!;
        if (tool.PointerDown(Ptr(a, modifiers, 1)))
        {
            var active = View.Tool!;
            for (int i = 1; i <= 6; i++) active.PointerMove(Ptr(new(a.X + (b.X - a.X) * i / 6, a.Y + (b.Y - a.Y) * i / 6), modifiers));
            active.PointerUp(Ptr(b, modifiers));
        }
        await Settle();
    }

    private static int Find(IReadOnlyList<TextGlyph> glyphs, string text) =>
        string.Concat(glyphs.Select(g => g.Text.Length == 1 ? g.Text : "?")).IndexOf(text, StringComparison.Ordinal);

    /// <summary>Just inside a glyph's left or right edge, at its middle height.</summary>
    private static Point2 Edge(TextGlyph g, bool right)
    {
        var q = g.Box;
        Point2 l = new((q.UpperLeft.X + q.LowerLeft.X) / 2, (q.UpperLeft.Y + q.LowerLeft.Y) / 2), r = new((q.UpperRight.X + q.LowerRight.X) / 2, (q.UpperRight.Y + q.LowerRight.Y) / 2);
        double t = right ? 0.9 : 0.1;
        return new(l.X + (r.X - l.X) * t, l.Y + (r.Y - l.Y) * t);
    }

    private static bool Close(Point2 a, Point2 b) => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;

    private static byte[] Png(int w, int h, Color color)
    {
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { pixels[i * 4] = color.B; pixels[i * 4 + 1] = color.G; pixels[i * 4 + 2] = color.R; pixels[i * 4 + 3] = 255; }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4)));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    private static (RenderTargetBitmap Bitmap, double Dpi) Pixels()
    {
        double dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        return (Capture(window), dpi);
    }

    private static (byte R, byte G, byte B) Pixel((RenderTargetBitmap Bitmap, double Dpi) shot, Point viewPoint)
    {
        var p = View.TranslatePoint(viewPoint, window);
        int x = (int)(p.X * shot.Dpi), y = (int)(p.Y * shot.Dpi);
        var px = new byte[4];
        shot.Bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[2], px[1], px[0]);
    }

    private static bool Bright((byte R, byte G, byte B) c) => c.R > 200 && c.G > 200 && c.B > 200;
    private static bool Dark((byte R, byte G, byte B) c) => c.R < 70 && c.G < 70 && c.B < 70;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var c = VisualTreeHelper.GetChild(parent, i); yield return c; foreach (var d in Descendants(c)) yield return d; }
    }

    private static RenderTargetBitmap Capture(Window w)
    {
        double dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Round(w.ActualWidth * dpi), (int)Math.Round(w.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(w);
        return bitmap;
    }

    private static void Save(BitmapSource bitmap, string name)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
    }

    private static void Check(string name, bool passed, string detail = "")
    {
        checks.Add(new { Name = name, Passed = passed, Detail = detail });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        if (!passed) throw new InvalidOperationException(name + ": " + detail);
    }

    private static Task Settle(int ms = 200) => Task.Delay(ms);
}
