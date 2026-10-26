using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using TextAlignment = PdfNotes.Core.Models.TextAlignment;
using TextSelection = PdfNotes.Core.Geometry.TextSelection;

namespace PdfNotes.Probe;

/// <summary>
/// D09 real-app check: glyph highlight tool on PDF text (one line, two lines, rotated page, manual height), no text on
/// generated pages, highlights cannot move but can be styled/deleted/lassoed, select-tool text selection with I-beam,
/// Ctrl+C and right-click highlight, text box highlights that follow typing, save → separate-process reopen.
/// <c>logic</c> sends pointer events to the tool directly (no OS input) and is reported as such.
/// </summary>
internal static class D09Check
{
    private static MainWindow window = null!;
    private static string folder = "";
    private static bool logic;
    private static readonly List<object> checks = [];
    private static readonly List<string> messages = [];
    private static DocumentSession Session => window.Document.Session;
    private static DocumentView View => (DocumentView)window.FindName("View");
    private static int page;
    private static PdfTextHighlight[] Highlights => Session.Current.AllAnnotations.OfType<PdfTextHighlight>().ToArray();

    private static string Metadata(DocumentModel model)
    {
        var data = SchemaV3.Serialize(model);
        return JsonSerializer.Serialize(new { data.Header, Envelopes = data.Annotations.Select(a => a.Envelope) });
    }

    private static void Check(string name, bool passed, string detail = "")
    {
        checks.Add(new { Name = name, Passed = passed, Detail = detail });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        if (!passed) throw new InvalidOperationException(name + ": " + detail);
    }

    public static int Run(string output, bool reopen, bool logicOnly)
    {
        folder = output; logic = logicOnly;
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
                if (!logic && !reopen) await Foreground();
                if (reopen) await Reopen(); else await Edit();
                result = 0;
            }
            catch (Exception e) { checks.Add(new { Name = "harness", Passed = false, Detail = e.ToString() }); Console.WriteLine(e); }
            finally
            {
                string name = reopen ? "d09-reopen-result.json" : logic ? "d09-logic-result.json" : "d09-result.json";
                File.WriteAllText(Path.Combine(folder, name), JsonSerializer.Serialize(new { Passed = result == 0, Input = reopen || logic ? "tool-calls" : "os-mouse", Checks = checks, Messages = messages },
                    new JsonSerializerOptions { WriteIndented = true }));
                window.PromptOverride = (_, _) => MessageBoxResult.No; window.Close(); app.Shutdown();
            }
        };
        app.Run();
        if (!reopen && result == 0)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("d09-reopen"); start.ArgumentList.Add(folder);
            using var child = Process.Start(start)!; child.WaitForExit(); result = child.ExitCode;
        }
        return result;
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

    private static async Task Edit()
    {
        string pdf = Path.Combine(folder, "source.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        Check("open sample PDF", await window.OpenFileAsync(pdf));
        await ShowPage(0);
        var glyphs = window.PageGlyphs(0);
        int original = Find(glyphs, "Original");
        Check("page glyphs are extracted", original >= 0 && glyphs.Count > 30, $"{glyphs.Count} glyphs");

        // ----- highlight tool on PDF text -----
        // D14g: the one highlighter, kind 형광 with 글자에 맞추기 on (spec 9.1).
        ((ToggleButton)window.FindName("HighlighterToolButton")).IsChecked = true; await Settle();
        Check("highlighter and style bar", window.CurrentTool == ToolKind.FreeHighlight && window.MarkKind is null && window.HighlighterSnap &&
            ((ToggleButton)window.FindName("HighlighterSnapButton")).Visibility == Visibility.Visible && ((ToggleButton)window.FindName("HighlightAutoButton")).Visibility == Visibility.Visible &&
            ((ToggleButton)window.FindName("HighlightAutoButton")).IsChecked == true && window.TextHighlightToolStyle.Style.TransparencyPercent == 60);
        await Hover(Edge(glyphs[original], false));
        Check("I-beam over PDF text", View.Cursor == Cursors.IBeam, $"{View.Cursor}");
        int undo = Session.UndoCount;
        await Drag(Edge(glyphs[original], false), Edge(glyphs[original + 3], true), Edge(glyphs[original + 7], true));
        var one = Highlights.Single();
        Check("drag over one line makes one quad on the glyphs", one.Quads.Length == 1 && one.SourceTextHint == "Original" && Session.UndoCount == undo + 1 &&
            Session.UndoLabel == "undo.textHighlight" && one.HeightMode == HighlightHeightMode.Auto && Math.Abs(one.Opacity - .4) < 1e-9 &&
            glyphs.Skip(original).Take(8).All(g => TextSelection.InQuad(one.Quads[0], g.Center)), $"hint '{one.SourceTextHint}'");
        var q = one.Quads[0];
        var color = WindowColor(new((q.UpperLeft.X + q.UpperRight.X) / 2, q.UpperLeft.Y + 1));
        Check("highlight is drawn over the PDF", color.B < 200 && color.R > 200 && color.G > 190, $"{color}");

        int english = Find(glyphs, "English");
        int lineEnd = Find(glyphs, "PDF");
        await Drag(Edge(glyphs[lineEnd], false), Edge(glyphs[english], false), Edge(glyphs[english + 6], true));
        var two = Highlights.Single(h => h.Id != one.Id);
        Check("drag across two lines makes one quad per line", two.Quads.Length == 2 && two.SourceTextHint!.StartsWith("PDF", StringComparison.Ordinal) &&
            two.SourceTextHint.EndsWith("English", StringComparison.Ordinal), $"'{two.SourceTextHint}'");

        var widthBox = (TextBox)window.FindName("WidthBox");
        Check("the width stays on for free strokes with automatic height (spec 9.1, D14g)", widthBox.IsEnabled && ((Slider)window.FindName("WidthSlider")).IsEnabled);
        window.SetHighlightAuto(false); await Settle();
        Check("manual height enables the width", widthBox.IsEnabled);
        window.SetWidth(30); await Settle();
        Check("width sets the manual height", !window.TextHighlightToolStyle.Auto && window.TextHighlightToolStyle.Style.WidthPt == 30 &&
            ((ToggleButton)window.FindName("HighlightAutoButton")).IsChecked == false);
        int digits = Find(glyphs, "123");
        await Drag(Edge(glyphs[digits], false), Edge(glyphs[digits + 2], true));
        var manual = Highlights.Single(h => h.Id != one.Id && h.Id != two.Id);
        var mq = TextSelection.Painted(manual.Quads[0], manual.HeightMode, manual.ManualHeightPt);
        Check("manual height is centered on the line", manual.HeightMode == HighlightHeightMode.Manual && manual.ManualHeightPt == 30 &&
            Math.Abs(mq.UpperLeft.DistanceTo(mq.LowerLeft) - 30) < 1e-6);
        window.SetHighlightAuto(true);
        Check("auto toggle returns to glyph height", window.TextHighlightToolStyle.Auto);
        undo = Session.UndoCount;
        View.ScrollToPagePoint(page, new(425, 605)); await Settle(); // on screen for the real mouse too
        await Drag(new(400, 600), new(450, 610)); // blank area: a free highlight stroke (spec 9.2, D14g)
        Check("drag on a blank page area draws a free highlight stroke", Highlights.Length == 3 && Session.Current.AllAnnotations.OfType<FreeHighlightStroke>().Count() == 1 &&
            Session.UndoCount == undo + 1, $"{Highlights.Length} highlights, {Session.Current.AllAnnotations.OfType<FreeHighlightStroke>().Count()} strokes, undo +{Session.UndoCount - undo}, tool {window.CurrentTool}, label {Session.UndoLabel}");
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        View.GoToPage(page); await Settle();

        undo = Session.UndoCount;
        var onOne = Edge(glyphs[original + 2], false);
        await Drag(onOne, new(onOne.X + .5, onOne.Y));
        await Settle();
        Check("click on an existing highlight selects it for editing", window.CurrentTool == ToolKind.Select && window.Selection.SelectedIds.SequenceEqual([one.Id]) &&
            Session.UndoCount == undo && window.StyleTarget is { Kind: AnnotationKind.PdfTextHighlight }, $"tool {window.CurrentTool}, selected {window.Selection.SelectedIds.Count}");
        Check("the highlighter is marked as the tool to go back to (spec 5.4, D14g)", window.ReturnTool == ToolKind.FreeHighlight &&
            PdfNotes.App.Themes.ToolMarks.GetIsReturnTarget((ToggleButton)window.FindName("HighlighterToolButton")));
        window.SetTool(ToolKind.FreeHighlight); await Settle();
        await Drag(Edge(glyphs[original], false), Edge(glyphs[original + 4], true));
        Check("a drag that starts on a highlight still highlights", Highlights.Length == 4 && window.CurrentTool == ToolKind.FreeHighlight);
        ApplicationCommands.Undo.Execute(null, window); await Settle();

        // ----- the highlight in the selection tool -----
        window.SetTool(ToolKind.Select); await Settle();
        var center = new Point2((q.UpperLeft.X + q.LowerRight.X) / 2, (q.UpperLeft.Y + q.LowerRight.Y) / 2);
        undo = Session.UndoCount;
        await Drag(center, new(center.X + 40, center.Y + 30));
        Check("selected highlight cannot be dragged away from its text", window.Selection.SelectedIds.SequenceEqual([one.Id]) && Session.UndoCount == undo &&
            Highlights.Single(h => h.Id == one.Id).Quads[0] == q && window.Selection.HandlePoints(View.PageTransform(0)).Length == 0,
            $"selected {window.Selection.SelectedIds.Count}, undo {Session.UndoCount - undo}");
        Check("style bar shows the highlight", window.StyleTarget is { Kind: AnnotationKind.PdfTextHighlight } t && t.Style.TransparencyPercent == 60);
        window.SetColor(new(0x43, 0xA0, 0x47)); window.SetTransparency(30);
        Check("color and transparency change it", Highlights.Single(h => h.Id == one.Id) is { Color: { G: 0xA0 } } changed && Math.Abs(changed.Opacity - .7) < 1e-9);
        window.Selection.Nudge(1, 0, true);
        Check("arrow keys do not move it", Highlights.Single(h => h.Id == one.Id).Quads[0] == q);
        await Key(0x2E); // Delete
        Check("Delete removes the highlight", Highlights.All(h => h.Id != one.Id));
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("delete undo", Highlights.Any(h => h.Id == one.Id));
        window.Selection.Select(null);

        window.SetTool(ToolKind.StrokeEraser); await Settle();
        await Drag(new(q.UpperLeft.X + 2, center.Y), new(q.UpperRight.X - 2, center.Y)); // inside "Original" only
        Check("the eraser takes a glyph highlight whole (spec 7.3, D14g)", Highlights.Length == 2 && Highlights.All(h => h.Id != one.Id),
            $"{Highlights.Length} highlights, tool {window.CurrentTool}, undo {Session.UndoLabel}");
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("erase undo", Highlights.Length == 3);
        window.SetTool(ToolKind.Lasso); await Settle();
        await Drag(new(q.UpperLeft.X - 10, q.UpperLeft.Y - 10), new(q.UpperLeft.X + 30, q.UpperLeft.Y - 10), new(q.UpperLeft.X + 30, q.LowerLeft.Y + 5),
            new(q.UpperLeft.X - 10, q.LowerLeft.Y + 5));
        Check("lasso over part of a highlight selects it", window.Selection.SelectedIds.Contains(one.Id) && window.CurrentTool == ToolKind.Select);
        window.Selection.Select(null); await Settle();

        // ----- select tool: PDF text selection, Ctrl+C, right-click highlight -----
        int plain = Find(glyphs, "원본 본문"); // not under a highlight: a highlight would be picked as an annotation first
        await Hover(Edge(glyphs[plain], false));
        Check("select tool shows the I-beam over PDF text", View.Cursor == Cursors.IBeam, $"{View.Cursor}");
        await Drag(Edge(glyphs[plain], false), Edge(glyphs[plain + 2], true), Edge(glyphs[plain + 4], true));
        Check("drag on text selects glyphs instead of panning", window.Selection.HasTextSelection && window.Selection.SelectedText == "원본 본문" &&
            window.Selection.SelectedIds.Count == 0, $"'{window.Selection.SelectedText}'");
        if (!logic)
        {
            Clipboard.Clear();
            await Key(0x43, control: true); // Ctrl+C
            Check("Ctrl+C copies the original text", Clipboard.ContainsText() && Clipboard.GetText() == "원본 본문", Clipboard.ContainsText() ? Clipboard.GetText() : "(none)");
        }
        else Check("copy selected text", window.CopySelectedText() && Clipboard.GetText() == "원본 본문");
        var dip = Dip(Edge(glyphs[plain + 1], false));
        var menu = window.OpenAnnotationMenu(dip)!;
        var items = menu.Items.OfType<MenuItem>().Where(i => i.Header is string).ToDictionary(i => (string)i.Header);
        Check("right-click on selected text offers copy and highlight", items.ContainsKey("복사") && items.ContainsKey("하이라이트") && items.Count == 2);
        items["하이라이트"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false; await Settle();
        Check("right-click highlight uses the tool style", Highlights.Length == 4 && Highlights[^1].SourceTextHint == "원본 본문" && !window.Selection.HasTextSelection);
        await Drag(new(400, 600), new(380, 560));
        Check("drag on blank area still pans", !window.Selection.HasTextSelection);

        // ----- rotated page and a page without text -----
        await ShowPage(2);
        var rotated = window.PageGlyphs(2);
        int rotatedStart = Find(rotated, "Original");
        window.SetTool(ToolKind.FreeHighlight); await Settle();
        await Drag(Edge(rotated[rotatedStart], false), Edge(rotated[rotatedStart + 7], true));
        var onRotated = Highlights.Single(h => Session.Current.Pages[2].Annotations.Contains(h));
        Check("highlight on a /Rotate 270 page with crop offset sits on its glyphs", onRotated.Quads.Length == 1 &&
            rotated.Skip(rotatedStart).Take(8).All(g => TextSelection.InQuad(onRotated.Quads[0], g.Center)), $"'{onRotated.SourceTextHint}'");
        var rq = onRotated.Quads[0];
        var rotatedColor = WindowColor(new((rq.UpperLeft.X + rq.UpperRight.X) / 2, rq.UpperLeft.Y + 1));
        Check("rotated page highlight is drawn in place", rotatedColor.B < 200 && rotatedColor.R > 200, $"{rotatedColor}");
        int word = Find(rotated, "본문");
        undo = Session.UndoCount;
        await DoubleClick(Edge(rotated[word], true));
        var wordHighlight = Highlights.Last();
        Check("double-click highlights the word", Session.UndoCount == undo + 1 && wordHighlight.SourceTextHint == "본문" && wordHighlight.Quads.Length == 1 &&
            window.CurrentTool == ToolKind.FreeHighlight, $"'{wordHighlight.SourceTextHint}'");
        int pdfWord = Find(rotated, "PDF");
        await DoubleClick(Edge(rotated[pdfWord + 1], false));
        Check("double-click stops at spaces and punctuation", Highlights.Last().SourceTextHint == "PDF", $"'{Highlights.Last().SourceTextHint}'");
        Snapshot(logic ? "d09-logic-rotated.png" : "d09-rotated.png");

        // ----- text box highlights -----
        await ShowPage(0);
        var box = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Translation(80, 360), "강조할 문장 abc",
            [new TextRun(0, 10, "Malgun Gothic", 20, RgbColor.Black, false, false, false, false)], TextAlignment.Left, 260, TextBoxStyle.Default, []);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, box));
        await Settle();
        var chars = PdfNotes.Text.TextLayout.Measure(box).Characters;
        Point2 Char(int offset, bool end) { var c = chars.Single(x => x.Offset == offset).Bounds; return box.Transform.Transform(new(c.X + c.Width * (end ? .8 : .2), c.Y + c.Height / 2)); }
        undo = Session.UndoCount;
        await Drag(Char(0, false), Char(1, false), Char(2, true));
        var marked = (TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation;
        Check("highlight tool on a text box marks its characters", marked.Highlights is [{ Start: 0, Length: 3 }] && Session.UndoCount == undo + 1 && Highlights.Length == 7,
            string.Join(",", marked.Highlights));
        var boxColor = WindowColor(box.Transform.Transform(new(chars[1].Bounds.X + chars[1].Bounds.Width / 2, chars[1].Bounds.Y + 1)));
        Check("text box highlight is drawn", boxColor.B < 200 && boxColor.R > 200, $"{boxColor}");

        window.BeginTextEdit(box.Id); await Settle();
        var editor = window.ActiveTextEditor!;
        await Type(editor, 1, "X"); // strictly inside "강조할" → joins
        await Type(editor, 0, "Y"); // at its start edge → does not join
        await Type(editor, 5, "Z"); // at its end edge ("Y강X조할|") → does not join
        var typed = window.EditingText!;
        Check("typing moves the highlight with its characters", typed.Text.StartsWith("Y강X조할Z", StringComparison.Ordinal) && typed.Highlights is [{ Start: 1, Length: 4 }],
            $"'{typed.Text}' {string.Join(",", typed.Highlights)}");
        Select(editor, 7, 3);
        window.ToggleTextHighlight(); await Settle();
        typed = window.EditingText!;
        Check("text bar toggle adds a highlight to the selection", typed.Highlights.Length == 2 && typed.Highlights[1] is { Start: 7, Length: 3 }, string.Join(",", typed.Highlights));
        Select(editor, 7, 3);
        window.ToggleTextHighlight(); await Settle();
        Check("toggle again removes it", window.EditingText!.Highlights.Length == 1);
        window.FinishTextEditing(); await Settle();
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("text edits undo with their highlight ranges", ((TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation).Highlights.Length == 2);
        ApplicationCommands.Redo.Execute(null, window); await Settle();
        window.SetTool(ToolKind.FreeHighlight); await Settle();
        var edited = (TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation;
        var layout = PdfNotes.Text.TextLayout.Measure(edited).Characters;
        var wordChar = layout.Single(c => c.Offset == edited.Text.IndexOf("문장", StringComparison.Ordinal)).Bounds;
        await DoubleClick(edited.Transform.Transform(new(wordChar.X + wordChar.Width / 2, wordChar.Y + wordChar.Height / 2)));
        var boxWord = (TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation;
        int at = edited.Text.IndexOf("문장", StringComparison.Ordinal);
        Check("double-click highlights a text box word", boxWord.Highlights.Any(h => h.Start == at && h.Length == 2) && boxWord.Highlights.Length == 2,
            string.Join(",", boxWord.Highlights.Select(h => $"{h.Start}+{h.Length}")));

        string saved = Path.Combine(folder, "editable.pdf");
        Check("save", await window.SaveToAsync(saved) && !Session.IsDirty);
        File.WriteAllText(Path.Combine(folder, "expected.json"), Metadata(Session.Current));
        window.SetTool(ToolKind.Select); await ShowPage(0);
        Snapshot(logic ? "d09-logic.png" : "d09.png");
        using (var engine = new PdfNotes.Pdf.Engine.PdfiumDocument(File.ReadAllBytes(saved)))
            PdfNotes.App.Prototype.ProbeFactory.SavePng(engine.Render(0, 1.5), Path.Combine(folder, "editable-page.png"));

        // ----- a page without extractable text -----
        Check("new note", await window.CreateNoteAsync(NewPageSpec.Default));
        window.SetTool(ToolKind.FreeHighlight); await ShowPage(0);
        ((TextBlock)window.FindName("StatusText")).Text = "";
        await Drag(new(200, 200), new(300, 200));
        Check("no text: a free stroke, no glyph highlight and no notice (spec 9.2)", ((TextBlock)window.FindName("StatusText")).Text == "" &&
            Session.Current.AllAnnotations.Single() is FreeHighlightStroke);
    }

    private static async Task Reopen()
    {
        Check("open", await window.OpenFileAsync(Path.Combine(folder, "editable.pdf")));
        Check("restart exact metadata", Metadata(Session.Current) == File.ReadAllText(Path.Combine(folder, "expected.json")));
        var box = Session.Current.AllAnnotations.OfType<TextBoxAnnotation>().Single();
        Check("glyph and text box highlights reopen editable", Highlights.Length == 7 && box.Highlights.Length == 2 && messages.Count == 0, $"{Highlights.Length}, box {box.Highlights.Length}");
        var first = Highlights[0];
        window.SetTool(ToolKind.Select); window.Selection.Select(first.Id);
        window.SetWidth(20);
        Check("reopened highlight takes a manual height", Highlights[0] is { HeightMode: HighlightHeightMode.Manual, ManualHeightPt: 20 });
        string path = Path.Combine(folder, "reedited.pdf");
        Check("re-edit and save", await window.SaveToAsync(path));
        var bytes = File.ReadAllBytes(path);
        var read = EditablePdfReader.Read(bytes, PdfPageGeometryReader.Read(bytes));
        Check("re-edit round trip", read.ReadOnlyCount == 0 && Metadata(read.Model) == Metadata(Session.Current), $"restored {read.RestoredCount}");
        using var engine = new PdfNotes.Pdf.Engine.PdfiumDocument(bytes);
        PdfNotes.App.Prototype.ProbeFactory.SavePng(engine.Render(0, 1.5), Path.Combine(folder, "reedited-page.png"));
    }

    // ----- text editor helpers -----

    private static TextPointer At(RichTextBox editor, int offset)
    {
        var p = editor.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        for (int i = 0; i < offset; i++) p = p.GetNextInsertionPosition(LogicalDirection.Forward) ?? p;
        return p;
    }

    private static async Task Type(RichTextBox editor, int offset, string text)
    {
        if (!logic) await EnsureForeground();
        editor.Focus();
        editor.CaretPosition = At(editor, offset); await Settle();
        if (logic) { editor.CaretPosition.InsertTextInRun(text); window.FlushTextEdit(); await Settle(); return; }
        // Unicode key events: the same characters whatever keyboard layout or IME mode is active (not a TSF composition test).
        foreach (char c in text)
        {
            var down = new Input { Type = 1, Scan = c, Flags = 4 };
            var up = new Input { Type = 1, Scan = c, Flags = 4 | 2 };
            SendInput(2, [down, up], Marshal.SizeOf<Input>());
            await Task.Delay(40);
        }
        await Settle();
    }

    private static void Select(RichTextBox editor, int start, int length) => editor.Selection.Select(At(editor, start), At(editor, start + length));

    // ----- input -----

    private static Point Dip(Point2 pagePoint)
    {
        var p = View.PageTransform(page).PageToDip.Transform(pagePoint);
        return new(p.X, p.Y);
    }

    private static async Task Drag(params Point2[] points)
    {
        if (logic)
        {
            var tool = View.Tool!;
            if (!tool.PointerDown(Ptr(Dip(points[0]), ModifierKeys.None))) { await Settle(); return; }
            foreach (var p in points.Skip(1)) tool.PointerMove(Ptr(Dip(p), ModifierKeys.None));
            if (tool.IsActive) tool.PointerUp(Ptr(Dip(points[^1]), ModifierKeys.None));
            await Settle(); return;
        }
        await EnsureForeground();
        Cursor(Dip(points[0])); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(50);
        foreach (var p in points.Skip(1)) { Cursor(Dip(p)); await Task.Delay(25); }
        await Task.Delay(40);
        mouse_event(4, 0, 0, 0, 0); await Settle();
    }

    /// <summary>Two clicks at one point within the system double-click time (the second press has ClickCount 2).</summary>
    private static async Task DoubleClick(Point2 pagePoint)
    {
        if (logic)
        {
            var tool = View.Tool!;
            if (tool.PointerDown(Ptr(Dip(pagePoint), ModifierKeys.None, 1)) && tool.IsActive) tool.PointerUp(Ptr(Dip(pagePoint), ModifierKeys.None));
            if (tool.PointerDown(Ptr(Dip(pagePoint), ModifierKeys.None, 2)) && tool.IsActive) tool.PointerUp(Ptr(Dip(pagePoint), ModifierKeys.None));
            await Settle(); return;
        }
        await EnsureForeground();
        Cursor(Dip(pagePoint)); await Task.Delay(80);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0);
        await Settle();
    }

    private static async Task Hover(Point2 pagePoint)
    {
        if (logic) { (View.Tool as IHoverTool)?.PointerHover(Dip(pagePoint)); View.Cursor = PdfNotes.App.Tools.SelectionCursors.For(View.Tool?.Cursor); await Settle(); return; }
        await EnsureForeground();
        Cursor(Dip(new(pagePoint.X - 3, pagePoint.Y))); await Task.Delay(40);
        Cursor(Dip(pagePoint)); await Settle();
    }

    private static async Task Key(byte key, bool control = false)
    {
        if (logic)
        {
            if (key == 0x2E) Session.Execute(DocumentEdits.DeleteAnnotations(Session.Current, window.Selection.SelectedIds));
            await Settle(); return;
        }
        await EnsureForeground();
        View.Focus();
        if (control) keybd_event(0x11, 0, 0, 0);
        keybd_event(key, 0, 0, 0); await Task.Delay(30); keybd_event(key, 0, 2, 0);
        if (control) keybd_event(0x11, 0, 2, 0);
        await Settle();
    }

    private static int regained;

    /// <summary>Another window may take the foreground during a long run; real input is only sent to our own window.</summary>
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

    private static Color WindowColor(Point2 pagePoint)
    {
        var inWindow = View.TranslatePoint(Dip(pagePoint), window);
        double dpi = View.DpiScale;
        var pixel = new byte[4];
        Capture().CopyPixels(new Int32Rect((int)Math.Floor(inWindow.X * dpi), (int)Math.Floor(inWindow.Y * dpi), 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    private static void Snapshot(string name)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Capture()));
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
