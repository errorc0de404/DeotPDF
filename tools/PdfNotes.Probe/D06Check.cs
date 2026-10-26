using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.App;
using PdfNotes.App.Canvas;
using PdfNotes.App.Services;
using PdfNotes.App.Tools;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfNotes.Text;

namespace PdfNotes.Probe;

internal static class D06Check
{
    private static MainWindow window = null!;
    private static string folder = "";
    private static readonly List<object> checks = [];
    private static readonly List<string> messages = [];
    private static DocumentSession Session => window.Document.Session;
    private static DocumentView View => (DocumentView)window.FindName("View");
    private static string Metadata(DocumentModel model)
    {
        var data = SchemaV1.Serialize(model);
        return JsonSerializer.Serialize(new { data.Header, Envelopes = data.Annotations.Select(a => a.Envelope) });
    }
    private static void Check(string name, bool passed, string detail = "")
    {
        checks.Add(new { Name = name, Passed = passed, Detail = detail }); Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        if (!passed) throw new InvalidOperationException(name + ": " + detail);
    }
    public static int Run(string output, bool reopen, bool caretOnly = false, bool pasteOnly = false, bool createOnly = false, bool fixesOnly = false, bool logicOnly = false)
    {
        folder = output; Environment.SetEnvironmentVariable("PDFNOTES_SETTINGS_DIR", Path.Combine(folder, "settings"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; int result = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                window = new MainWindow { Width = 1420, Height = 900, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                window.MessageSink = messages.Add; window.PromptOverride = (_, _) => MessageBoxResult.No; window.Show(); await Settle();
                if (!logicOnly)
                {
                var handle = new WindowInteropHelper(window).Handle; SetForegroundWindow(handle); window.Activate(); await Settle();
                for (int attempt = 0; attempt < 5 && GetForegroundWindow() != handle; attempt++)
                { keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); window.Activate(); SetForegroundWindow(handle); await Task.Delay(200); }
                if (GetForegroundWindow() != handle)
                { window.Topmost = true; window.Activate(); SetForegroundWindow(handle); await Settle(); window.Topmost = false; }
                Check("foreground", GetForegroundWindow() == handle, $"target={handle}, actual={GetForegroundWindow()}");
                }
                if (fixesOnly) await FixesOnly(logicOnly); else if (createOnly) await CreateOnly(); else if (pasteOnly) await PasteOnly(); else if (caretOnly) await CaretOnly(); else if (reopen) await Reopen(); else await Edit(); result = 0;
            }
            catch (Exception e) { checks.Add(new { Name = "harness", Passed = false, Detail = e.ToString() }); Console.WriteLine(e); }
            finally
            {
                File.WriteAllText(Path.Combine(folder, logicOnly ? "d06-fixes-logic-result.json" : fixesOnly ? "d06-fixes-result.json" : createOnly ? "d06-create-result.json" : pasteOnly ? "d06-paste-result.json" : caretOnly ? "d06-caret-result.json" : reopen ? "d06-reopen-result.json" : "d06-result.json"), JsonSerializer.Serialize(new { Passed = result == 0, Checks = checks, Messages = messages }, new JsonSerializerOptions { WriteIndented = true }));
                window.PromptOverride = (_, _) => MessageBoxResult.No; window.Close(); app.Shutdown();
            }
        };
        app.Run();
        if (!reopen && !caretOnly && !pasteOnly && !createOnly && !fixesOnly && result == 0)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("d06-reopen"); start.ArgumentList.Add(folder); using var child = Process.Start(start)!; child.WaitForExit(); result = child.ExitCode;
        }
        return result;
    }
    private static async Task Edit()
    {
        View.SetZoom(1); await Settle();
        window.BeginNewText(0, new(50, 60)); await Settle();
        Check("empty box is UI only", !Session.IsDirty && Session.UndoCount == 0 && !Session.Current.AllAnnotations.Any());
        await Key(0x1B); Check("empty Esc has no history", window.ActiveTextEditor is null && !Session.IsDirty && Session.UndoCount == 0);
        var screen = View.PageTransform(0).PageToDip.Transform(new(80, 100));
        var menu = window.OpenAnnotationMenu(new(screen.X, screen.Y))!;
        var item = menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "텍스트 박스 추가"); item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false; await Settle();
        // 2026-10-15 (24.24-6): the first line starts at the clicked point — the box sits its padding left and half a line up.
        Check("quick menu uses original page point", window.EditingText is { } temporary && Math.Abs(temporary.Transform.OffsetX + 4 - 80) < .001 && temporary.Transform.OffsetY < 100 && temporary.Transform.OffsetY > 80,
            window.EditingText is { } t ? $"{t.Transform.OffsetX:0.##}, {t.Transform.OffsetY:0.##}" : "no editor");
        Check("quick menu editor focused", ReferenceEquals(Keyboard.FocusedElement, window.ActiveTextEditor), Keyboard.FocusedElement?.GetType().Name ?? "null");
        await Type("한글 English 😀"); await Key(0x0D); await Type("둘째 줄"); await Settle(); window.FlushTextEdit();
        Check("editor committed nonblank content", Session.Current.AllAnnotations.Any(), new TextRange(window.ActiveTextEditor!.Document.ContentStart, window.ActiveTextEditor.Document.ContentEnd).Text);
        var box = (TextBoxAnnotation)Session.Current.AllAnnotations.Single(); Guid id = box.Id;
        Check("native Unicode and Enter", box.Text == "한글 English 😀\n둘째 줄", box.Text);
        Check("continuous input coalesces", Session.UndoCount == 1, $"Undo={Session.UndoCount}");
        var editor = window.ActiveTextEditor!; editor.SelectAll(); window.ApplyTextFormat(r => r with { Underline = true, Strikethrough = true });
        box = (TextBoxAnnotation)Session.Current.FindAnnotation(id)!.Annotation;
        Check("range underline strike", box.Runs.All(r => r.Underline && r.Strikethrough), JsonSerializer.Serialize(box.Runs));
        // Select only the logical prefix then apply another style; direction does not change slot source.
        editor.Selection.Select(editor.Document.ContentStart.GetNextInsertionPosition(LogicalDirection.Forward)!, editor.Document.ContentStart.GetNextInsertionPosition(LogicalDirection.Forward)!.GetPositionAtOffset(2)!);
        window.ApplyTextFormat(r => r with { Bold = true, SizePt = 24, Color = new(180, 20, 70) });
        box = (TextBoxAnnotation)Session.Current.FindAnnotation(id)!.Annotation;
        Check("mixed styles partition exact text", box.Runs.Length >= 2 && box.Runs[0].Bold && box.Runs[0].SizePt == 24 && box.Runs[^1].SizePt == 12 && ModelValidator.ValidateAnnotation(box, _ => false) is null);
        editor.SelectAll(); int beforeSetting = Session.UndoCount; window.SaveTextStyle(0); window.SaveTextStyle(null);
        var settings = AppSettings.Load(); Check("slot and default choose logical start", settings.TextSlots[0]?.Bold == true && settings.DefaultText.SizePt == 24 && Session.UndoCount == beforeSetting);
        // Caret formatting only changes future input. Native rich editor keeps it separate from the existing range.
        editor.Selection.Select(editor.Document.ContentEnd, editor.Document.ContentEnd); string before = box.Text;
        window.ApplyTextFormat(r => r with { Bold = false, SizePt = 18 }); await Type(" 추가"); window.FlushTextEdit();
        box = (TextBoxAnnotation)Session.Current.FindAnnotation(id)!.Annotation;
        Check("caret style affects future text", box.Text == before + " 추가" && box.Runs[^1].SizePt == 18 && box.Runs[0].SizePt == 24, JsonSerializer.Serialize(new { box.Text, box.Runs, before, Focus = Keyboard.FocusedElement?.GetType().Name }));
        Clipboard.SetText("\n 붙여넣기 😀"); await Key(0x56, control: true); window.FlushTextEdit();
        box = (TextBoxAnnotation)Session.Current.FindAnnotation(id)!.Annotation;
        Check("plain paste preserves newline emoji", box.Text.EndsWith("\n 붙여넣기 😀", StringComparison.Ordinal) && ModelValidator.ValidateAnnotation(box, _ => false) is null, JsonSerializer.Serialize(new { box.Text, box.Runs, Error = ModelValidator.ValidateAnnotation(box, _ => false) }));
        window.ApplyTextBox(b => b with { WidthPt = 125, Alignment = PdfNotes.Core.Models.TextAlignment.Center, BoxStyle = new(new(240, 210, 100), .3, true, new(30, 70, 160), .7) });
        box = (TextBoxAnnotation)Session.Current.FindAnnotation(id)!.Annotation;
        await Settle();
        double liveHeight = window.ActiveTextEditor!.Height;
        Check("fixed 4pt editor padding", window.ActiveTextEditor!.Document.PagePadding == new Thickness(4), window.ActiveTextEditor.Document.PagePadding.ToString());
        Check("editor and display same height", Math.Abs(liveHeight - TextLayout.Measure(box).Height) < .001, $"live={liveHeight}, display={TextLayout.Measure(box).Height}");
        await Settle(); Snapshot("editing.png"); window.FinishTextEditing(); await Settle(); Snapshot("display.png");
        Check("editor detached after finish", window.ActiveTextEditor is null && ((Canvas)window.FindName("TextEditorLayer")).Children.Count == 0);
        window.SetTool(ToolKind.Select); window.Selection.Select(id);
        var handles = window.Selection.HandlePoints(View.PageTransform(0)); var from = handles[1]; var to = new Point2(from.X - 50, from.Y);
        window.Selection.PointerDown(Ptr(new(from.X, from.Y), ModifierKeys.None)); window.Selection.PointerMove(Ptr(new(to.X, to.Y), ModifierKeys.None)); window.Selection.PointerUp(Ptr(new(to.X, to.Y), ModifierKeys.None));
        var narrower = (TextBoxAnnotation)Session.Current.FindAnnotation(id)!.Annotation;
        Check("single width resize keeps fonts and reflows", narrower.WidthPt < box.WidthPt && narrower.Runs.Select(r => r.SizePt).SequenceEqual(box.Runs.Select(r => r.SizePt)) && TextLayout.Measure(narrower).Height >= TextLayout.Measure(box).Height);
        Session.Undo(); Check("resize one undo", Math.Abs(((TextBoxAnnotation)Session.Current.FindAnnotation(id)!.Annotation).WidthPt - box.WidthPt) < .001);
        var ink = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(220, 100), new(280, 170)], 4, RgbColor.Black, 1);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, ink)); window.Selection.SetSelection([id, ink.Id]);
        Session.Execute(DocumentEdits.UpdateAnnotations(Session.Current, window.Selection.SelectedAnnotations.Select(a => AnnotationGeometry.Similarity(a, new(80, 100), 1.25, .4)), "다중 변형"));
        var scaled = (TextBoxAnnotation)Session.Current.FindAnnotation(id)!.Annotation;
        Check("group scales fonts once and rotates box", Math.Abs(scaled.Runs[0].SizePt - box.Runs[0].SizePt * 1.25) < .001 && scaled.Transform.IsRigid);
        await Settle(); Snapshot("rotated.png"); Session.Undo();
        window.Selection.Select(id); Check("text clipboard copy", window.CopySelection()); Check("text clipboard paste", window.PasteSelection());
        var copy = (TextBoxAnnotation)Session.Current.AllAnnotations.OfType<TextBoxAnnotation>().Single(t => t.Id != id);
        Check("text clone independent IDs and format", copy.Text == box.Text && copy.Id != id && copy.Runs.SequenceEqual(box.Runs)); Session.Undo();
        window.BeginTextEdit(id); editor = window.ActiveTextEditor!; editor.SelectAll(); string original = box.Text; await Key(0x08); window.FinishTextEditing();
        Check("all text deleted removes box", Session.Current.FindAnnotation(id) is null);
        Session.Undo(); Check("delete undo restores full content format location", ((TextBoxAnnotation)Session.Current.FindAnnotation(id)!.Annotation).Text == original && ((TextBoxAnnotation)Session.Current.FindAnnotation(id)!.Annotation).Runs.SequenceEqual(box.Runs));
        // Save while an editor is still active; final text must be in the snapshot, then the editor is detached.
        window.BeginTextEdit(id); window.ActiveTextEditor!.CaretPosition = window.ActiveTextEditor.Document.ContentEnd; await Type(" 저장");
        Check("save finalizes editor", await window.SaveToAsync(Path.Combine(folder, "editable.pdf")) && window.ActiveTextEditor is null && !Session.IsDirty);
        File.WriteAllText(Path.Combine(folder, "expected.json"), Metadata(Session.Current));
        var read = EditablePdfReader.Read(File.ReadAllBytes(Path.Combine(folder, "editable.pdf")), PdfPageGeometryReader.Read(File.ReadAllBytes(Path.Combine(folder, "editable.pdf"))));
        Check("saved text and styles restore", Metadata(read.Model) == Metadata(Session.Current) && read.ReadOnlyCount == 0);
        Check("no blank text remains", !read.Model.AllAnnotations.OfType<TextBoxAnnotation>().Any(t => t.IsBlank));
        Check("no unexpected warning", messages.Count == 0, string.Join(";", messages));
    }
    private static async Task CreateOnly()
    {
        View.SetZoom(1); window.SetTool(ToolKind.Text); await Settle();
        await Mouse(new(80, 100), new(80, 100));
        Check("native text tool click", window.ActiveTextEditor is not null && Math.Abs(window.EditingText!.WidthPt - 180) < .01 && !Session.IsDirty);
        await Type("더블클릭"); window.FinishTextEditing(); var box = Session.Current.AllAnnotations.OfType<TextBoxAnnotation>().Single();
        window.SetTool(ToolKind.Select); await Settle();
        var click = View.PageTransform(0).PageToDip.Transform(new(90, 110)); var clickScreen = View.PointToScreen(new(click.X, click.Y));
        SetCursorPos((int)Math.Round(clickScreen.X), (int)Math.Round(clickScreen.Y)); await Task.Delay(80);
        for (int i = 0; i < 2; i++) { mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0); await Task.Delay(30); }
        await Settle();
        Check("native double click opens existing editor", window.ActiveTextEditor is not null && window.EditingText!.Id == box.Id);
        window.FinishTextEditing(); window.SetTool(ToolKind.Text); await Settle();
        await Mouse(new(220, 280), new(370, 300));
        Check("native text tool drag width", window.ActiveTextEditor is not null && Math.Abs(window.EditingText!.WidthPt - 150) < 1, $"Width={window.EditingText?.WidthPt}");
        window.FinishTextEditing(); Check("empty dragged box discarded", Session.Current.AllAnnotations.Count() == 1);
    }
    private static async Task FixesOnly(bool logicOnly)
    {
        if (logicOnly)
        {
            View.SetZoom(1); window.BeginNewText(0, new(80, 100));
            window.ActiveTextEditor!.Selection.Text = "더블클릭"; window.FinishTextEditing();
        }
        else await CreateOnly();
        var box = Session.Current.AllAnnotations.OfType<TextBoxAnnotation>().Single();
        window.SetTool(ToolKind.Text); await Settle();
        if (logicOnly)
        {
            var point = View.PageTransform(0).PageToDip.Transform(new(90, 110));
            View.Tool!.PointerDown(Ptr(new(point.X, point.Y), ModifierKeys.None));
        }
        else await Mouse(new(90, 110), new(90, 110));
        // D12 (spec 5.4): the first text tool click selects the box, clicking it again edits it.
        Check("text mode click selects existing box", window.CurrentTool == ToolKind.Select && window.Selection.SelectedIds.SequenceEqual([box.Id]) &&
            window.ActiveTextEditor is null && Session.Current.AllAnnotations.Count() == 1);
        if (logicOnly) window.BeginTextEdit(box.Id);
        else { await Task.Delay(600); await Mouse(new(90, 110), new(90, 110)); }
        await Settle();
        Check("text mode second click edits existing box", window.EditingText?.Id == box.Id && Session.Current.AllAnnotations.Count() == 1);
        window.ActiveTextEditor!.SelectAll(); window.ApplyTextFormat(r => r with { Bold = true, SizePt = 22 }); window.SaveTextStyle(0);
        window.ApplyTextFormat(r => r with { Bold = false, SizePt = 12 });
        int history = Session.UndoCount;
        var menu = window.TextContextMenu();
        var saved = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "저장된 양식"));
        Check("saved style menu shows five slots with empty disabled", saved.Items.Count == 5 && !((MenuItem)saved.Items[1]).IsEnabled);
        ((MenuItem)saved.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("editor right click slot applies selected range", ((TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation).Runs.All(r => r.Bold && r.SizePt == 22) && Session.UndoCount == history + 1);
        if (logicOnly) ApplicationCommands.Undo.Execute(null, window); else await Key(0x5A, control: true);
        Check("context style one undo", ((TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation).Runs.All(r => !r.Bold && r.SizePt == 12));
        window.FinishTextEditing(); window.SetTool(ToolKind.Select);
        var dip = View.PageTransform(0).PageToDip.Transform(new(90, 110));
        var selectionMenu = window.OpenAnnotationMenu(new(dip.X, dip.Y))!;
        var selectionSaved = selectionMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "저장된 양식"));
        ((MenuItem)selectionSaved.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); selectionMenu.IsOpen = false;
        Check("selected text box context slot applies whole box", ((TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation).Runs.All(r => r.Bold && r.SizePt == 22));
        Session.Execute(DocumentEdits.SetLocked(Session.Current, [box.Id], true));
        var lockedMenu = window.OpenAnnotationMenu(new(dip.X, dip.Y))!;
        Check("locked text style menu disabled", !lockedMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "저장된 양식")).IsEnabled); lockedMenu.IsOpen = false;
        Session.Execute(DocumentEdits.SetLocked(Session.Current, [box.Id], false));
        window.SetTool(ToolKind.Pen); await Settle();
        if (logicOnly)
        {
            var pointer = new CanvasPointer(new(dip.X, dip.Y), InputModifiers.None);
            View.Tool!.PointerDown(pointer); View.Tool.PointerUp(pointer);
        }
        else await Mouse(new(90, 110), new(90, 110));
        Check("pen mode still draws over text", window.ActiveTextEditor is null && Session.Current.AllAnnotations.OfType<InkStroke>().Any());
        window.SetTool(ToolKind.Text); await Settle();
        // A second box carries mixed Unicode and an explicit newline for independent-reader checks.
        window.BeginNewText(0, new(80, 200)); await Settle();
        if (logicOnly) window.ActiveTextEditor!.Selection.Text = "한글 English 😀\n둘째 줄";
        else { await Type("한글 English 😀"); await Key(0x0D); await Type("둘째 줄"); }
        window.FinishTextEditing();
        string path = Path.Combine(folder, "searchable.pdf"); await window.Document.SaveAsync(path);
        Check("save with searchable page text", File.Exists(path) && !Session.IsDirty);
        var bytes = File.ReadAllBytes(path);
        using var engine = new PdfNotes.Pdf.Engine.PdfiumDocument(bytes);
        string text = string.Concat(engine.ExtractText(0).Select(c => c.Text));
        Check("PDFium external text search candidates", text.Contains("한글 English 😀", StringComparison.Ordinal) && text.Contains("둘째 줄", StringComparison.Ordinal), text);
        var read = EditablePdfReader.Read(bytes, PdfPageGeometryReader.Read(bytes));
        using var background = new PdfNotes.Pdf.Engine.PdfiumDocument(read.BackgroundPdf);
        Check("editable reopen has no stale search layer", read.RestoredCount == 3 && background.ExtractText(0).Length == 0);
        Snapshot("fixes.png");
    }
    private static async Task Mouse(Point2 from, Point2 to)
    {
        var m = View.PageTransform(0).PageToDip; var a = m.Transform(from); var b = m.Transform(to);
        var p = View.PointToScreen(new(a.X, a.Y)); SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y)); await Task.Delay(80);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(70);
        p = View.PointToScreen(new(b.X, b.Y)); SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y)); await Task.Delay(70);
        mouse_event(4, 0, 0, 0, 0); await Settle();
    }
    private static async Task PasteOnly()
    {
        window.BeginNewText(0, new(80, 100)); await Settle(); await Type("한글 English 😀");
        window.ActiveTextEditor!.CaretPosition = window.ActiveTextEditor.Document.ContentEnd; window.ApplyTextFormat(r => r with { SizePt = 18 }); await Type(" 추가");
        Clipboard.SetText("\n 붙여넣기 😀"); await Key(0x56, control: true); window.FlushTextEdit();
        var box = Session.Current.AllAnnotations.OfType<TextBoxAnnotation>().Single();
        Check("plain paste", box.Text.EndsWith("\n 붙여넣기 😀", StringComparison.Ordinal), JsonSerializer.Serialize(new { box.Text, box.Runs, Error = ModelValidator.ValidateAnnotation(box, _ => false) }));
        await Type(" 뒤"); window.FlushTextEdit();
        Check("typing after paste appends", Session.Current.AllAnnotations.OfType<TextBoxAnnotation>().Single().Text.EndsWith("\n 붙여넣기 😀 뒤", StringComparison.Ordinal));
        await Settle(); Snapshot("paste-editing.png");
    }
    private static async Task CaretOnly()
    {
        window.BeginNewText(0, new(80, 100)); await Settle(); await Type("한글 English");
        var editor = window.ActiveTextEditor!; editor.SelectAll(); window.ApplyTextFormat(r => r with { SizePt = 24 });
        editor.CaretPosition = editor.Document.ContentEnd; window.ApplyTextFormat(r => r with { SizePt = 18 });
        Console.WriteLine($"Caret empty={editor.Selection.IsEmpty}, size={editor.Selection.GetPropertyValue(TextElement.FontSizeProperty)}, focus={Keyboard.FocusedElement?.GetType().Name}");
        await Type(" 추가"); window.FlushTextEdit();
        var box = Session.Current.AllAnnotations.OfType<TextBoxAnnotation>().Single();
        Check("caret style", box.Text == "한글 English 추가" && box.Runs[0].SizePt == 24 && box.Runs[^1].SizePt == 18, JsonSerializer.Serialize(new { box.Text, box.Runs }));
    }
    private static async Task Reopen()
    {
        Check("restart slot persistence", AppSettings.Load().TextSlots[0]?.SizePt == 24 && AppSettings.Load().DefaultText.Bold);
        Check("open PDF", await window.OpenFileAsync(Path.Combine(folder, "editable.pdf")));
        Check("restart exact metadata", Metadata(Session.Current) == File.ReadAllText(Path.Combine(folder, "expected.json")));
        var box = Session.Current.AllAnnotations.OfType<TextBoxAnnotation>().First(); window.BeginTextEdit(box.Id); await Settle();
        Check("one editor only", ((Canvas)window.FindName("TextEditorLayer")).Children.Count == 1);
        window.ActiveTextEditor!.CaretPosition = window.ActiveTextEditor.Document.ContentEnd; await Type(" 재편집");
        Check("reedit and save", await window.SaveToAsync(Path.Combine(folder, "reedited.pdf")));
        var bytes = File.ReadAllBytes(Path.Combine(folder, "reedited.pdf")); var read = EditablePdfReader.Read(bytes, PdfPageGeometryReader.Read(bytes));
        Check("reedit round trip", ((TextBoxAnnotation)read.Model.FindAnnotation(box.Id)!.Annotation).Text.EndsWith(" 재편집", StringComparison.Ordinal));
        window.BeginNewText(0, new(300, 200)); Check("default only affects new box", window.EditingText!.Text == "");
        await Type("기본"); window.FinishTextEditing();
        var created = Session.Current.AllAnnotations.OfType<TextBoxAnnotation>().Single(t => t.Id != box.Id);
        Check("new box uses persisted default", created.Runs[0].SizePt == 24 && created.Runs[0].Bold);
        Check("old box unchanged by new default", ((TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation).Runs[^1].SizePt != 24);
    }
    private static Task Settle() => Task.Delay(150);
    private static async Task Key(byte key, bool control = false)
    {
        if (control) keybd_event(0x11, 0, 0, 0); keybd_event(key, 0, 0, 0); await Task.Delay(30); keybd_event(key, 0, 2, 0); if (control) keybd_event(0x11, 0, 2, 0); await Settle();
    }
    private static async Task Type(string text)
    {
        foreach (char c in text)
        {
            var inputs = new[] { new INPUT { type = 1, data = new UNION { keyboard = new KEYINPUT { scan = c, flags = 4 } } }, new INPUT { type = 1, data = new UNION { keyboard = new KEYINPUT { scan = c, flags = 6 } } } };
            if (SendInput(2, inputs, Marshal.SizeOf<INPUT>()) != 2) throw new InvalidOperationException("SendInput failed"); await Task.Delay(20);
        }
        await Settle();
    }
    private static void Snapshot(string name)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
    }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public UNION data; }
    [StructLayout(LayoutKind.Explicit)] private struct UNION { [FieldOffset(0)] public KEYINPUT keyboard; [FieldOffset(0)] public MOUSEINPUT mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYINPUT { public ushort vk, scan; public uint flags, time; public nuint extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, flags, time; public nuint extra; }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nuint extra);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
