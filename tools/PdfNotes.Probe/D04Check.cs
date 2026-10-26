using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;

namespace PdfNotes.Probe;

/// <summary>
/// D04 acceptance run against the real MainWindow with real OS mouse/keyboard input: pen and free highlighter
/// (A05), preview, dot, cancel, Undo/Redo (A18), minimal selection and style edits, Save As / Ctrl+S / repeated save
/// (A19, A28), save while editing, injected save failures (A23), external change, unsaved prompts (A27).
/// Phase 2 (<c>d04-reopen</c>) runs in a separate process: reopen, compare restored data and on-screen colour
/// (no double appearance), re-edit, save, reopen. Writes d04-result.json / d04-reopen-result.json and screenshots.
/// </summary>
internal static class D04Check
{
    private sealed record Check(string Name, bool Passed, string Detail);

    private static readonly List<Check> Checks = [];
    private static readonly Dictionary<string, object> Facts = new();
    private static readonly List<string> Messages = [];
    private static readonly Queue<MessageBoxResult> Answers = new();
    private static readonly List<string> Prompts = [];
    private static string folder = "";
    private static MainWindow window = null!;
    private static bool realInput;

    private static readonly RgbColor Red = new(0xE5, 0x39, 0x35), Blue = new(0x1E, 0x88, 0xE5), Green = new(0x43, 0xA0, 0x47);
    private static readonly Point2[] PenPath = [new(80, 230), new(140, 250), new(200, 240), new(260, 270), new(320, 250)];
    private static readonly Point2 HighlightOnce = new(100, 330), HighlightSelf = new(200, 330), HighlightCross = new(250, 330), HighlightSingle = new(250, 365);

    private static DocumentView View => (DocumentView)window.FindName("View");
    private static DocumentSession Session => window.Document.Session;
    private static string Editable => Path.Combine(folder, "editable.pdf");
    private static string Expected => Path.Combine(folder, "expected-metadata.json");
    private static string PhaseOneColor => Path.Combine(folder, "phase1-highlighter-color.txt");

    public static int Run(string outputFolder, bool reopenPhase, bool penOnly = false, bool dotOnly = false)
    {
        folder = outputFolder;
        Environment.SetEnvironmentVariable("PDFNOTES_SETTINGS_DIR", Path.Combine(folder, reopenPhase ? "settings-reopen" : "settings"));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int exitCode = 1;
        string resultName = dotOnly ? "d04-dot-result.json" : penOnly ? "d04-pen-result.json" : reopenPhase ? "d04-reopen-result.json" : "d04-result.json";
        application.Startup += async (_, _) =>
        {
            try
            {
                window = new MainWindow { Width = 1200, Height = 860, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                window.MessageSink = Messages.Add;
                window.PromptOverride = (text, _) =>
                {
                    Prompts.Add(text);
                    return Answers.Count > 0 ? Answers.Dequeue() : MessageBoxResult.Cancel;
                };
                window.Show();
                await Settle();
                realInput = await Foreground();
                if (!realInput) Record("real input", false, "미검증: 창을 전면으로 가져올 수 없어 실제 입력을 보낼 수 없음");
                else if (dotOnly)
                {
                    string mixed = Path.Combine(folder, "mixed.pdf"); ProbeSamplePdf.CreateMixed(mixed); await window.OpenFileAsync(mixed); await ShowPage0();
                    window.SetTool(ToolKind.Pen); window.SetColor(Red); window.SetWidth(3); await Settle(); await DotAndCancel();
                }
                else if (penOnly)
                {
                    string mixed = Path.Combine(folder, "mixed.pdf");
                    ProbeSamplePdf.CreateMixed(mixed); await window.OpenFileAsync(mixed); await Settle();
                    await ShowPage0(); await PenStroke();
                }
                else if (reopenPhase) await Reopen();
                else await FirstSession();
                exitCode = Checks.All(c => c.Passed) ? 0 : 1;
            }
            catch (Exception e)
            {
                Checks.Add(new("harness", false, e.ToString()));
            }
            finally
            {
                File.WriteAllText(Path.Combine(folder, resultName), JsonSerializer.Serialize(new
                {
                    Passed = Checks.All(c => c.Passed), Machine = Environment.MachineName, Os = Environment.OSVersion.ToString(),
                    Dpi = View.DpiScale, Checks, Facts, Messages, Prompts
                }, new JsonSerializerOptions { WriteIndented = true }));
                foreach (var c in Checks) Console.WriteLine($"{(c.Passed ? "PASS" : "FAIL")} {c.Name}: {c.Detail}");
                window.PromptOverride = (_, _) => MessageBoxResult.No; // never block shutdown
                window.Close();
                application.Shutdown();
            }
        };
        application.Run();
        return exitCode;
    }

    private static void Record(string name, bool passed, string detail) => Checks.Add(new(name, passed, detail));

    // =====================================================================================================
    // Phase 1
    // =====================================================================================================

    private static async Task FirstSession()
    {
        string mixed = Path.Combine(folder, "mixed.pdf");
        ProbeSamplePdf.CreateMixed(mixed);
        await window.OpenFileAsync(mixed);
        await Settle();
        await ShowPage0();

        await PenStroke();
        await Highlighter();
        await DotAndCancel();
        await UndoRedoKeys();
        await RotatedPage();
        await SelectionAndStyle();
        await SaveAs();
        await RepeatedSave();
        await SaveWhileEditing();
        await SaveFailures();
        await ExternalChange();
        await UnsavedPrompts(mixed);
        await RunReopenProcess();
    }

    private static async Task ShowPage0()
    {
        View.SetZoom(1);
        View.ScrollToPagePoint(0, new(240, 300));
        await Settle();
    }

    private static async Task PenStroke()
    {
        PressToolButton("PenToolButton");
        ClickSwatch(Red);
        window.SetWidth(3);
        await Settle();
        int before = Session.Current.Pages[0].Annotations.Count;
        var previewColor = default(Color);
        await Drag(0, PenPath, async () =>
        {
            await Task.Delay(50);
            previewColor = WindowColor(0, Mid(PenPath[0], PenPath[1]));
        });
        await Settle();
        var page = Session.Current.Pages[0];
        var stroke = page.Annotations.Count == before + 1 ? page.Annotations[^1] as InkStroke : null;
        // Ends are kept exactly (cursor rounding ≤ 1.5 device px); gentle turns are smoothed slightly, never visibly (≤ 2 px).
        double pixel = DevicePixelPt(0);
        double endError = stroke is null ? double.NaN : Math.Max(stroke.Points[0].DistanceTo(PenPath[0]), stroke.Points[^1].DistanceTo(PenPath[^1])) / pixel;
        double pathError = stroke is null ? double.NaN : PenPath.Max(p => StrokeGeometry.DistanceToPolyline(stroke.Points, p)) / pixel;
        bool shape = endError <= 1.5 && pathError <= 2;
        Record("A05 pen stroke from real mouse drag", stroke is not null && shape && stroke.Color == Red && stroke.WidthPt == 3 && stroke.Opacity == 1,
            stroke is null ? "no stroke" : $"{stroke.Points.Length} points after smoothing/simplification, end error {endError:0.##}px, " +
            $"max distance of drag vertices {pathError:0.##}px, color {stroke.Color}, width {stroke.WidthPt}");
        Record("A05 live preview follows the pointer", Near(previewColor, Red, 40), $"mid-drag pixel {previewColor} (expected ≈ {Red})");
        var committed = WindowColor(0, Mid(PenPath[0], PenPath[1]));
        Record("A05 committed stroke drawn in place", Near(committed, Red, 40), $"pixel {committed}");
        Record("A18 stroke dirty + undoable", Session.IsDirty && window.Title.Contains(" *") && Session.UndoLabel == "undo.pen", $"title '{window.Title}', undo '{Session.UndoLabel}'");
        Facts["penStrokeId"] = stroke?.Id.ToString() ?? "";
        Screenshot("a05-pen.png");
    }

    private static async Task Highlighter()
    {
        PressToolButton("HighlighterToolButton");
        var style = window.HighlighterStyle;
        Record("highlighter defaults", style.Color == new RgbColor(0xFF, 0xE6, 0x00) && style.WidthPt == 14 && style.TransparencyPercent == 60,
            $"{style}");
        // Right to x=300 and back to x=150: 150..300 is covered twice by the same stroke. Then a crossing stroke.
        await Drag(0, [new(80, 330), new(300, 330), new(150, 330)]);
        await Drag(0, [new(250, 290), new(250, 380)]);
        await Settle();
        var strokes = Session.Current.Pages[0].Annotations.OfType<FreeHighlightStroke>().ToList();
        var once = WindowColor(0, HighlightOnce);
        var self = WindowColor(0, HighlightSelf);
        var cross = WindowColor(0, HighlightCross);
        var single = WindowColor(0, HighlightSingle);
        Record("A05/9.2 highlighter strokes", strokes.Count == 2 && strokes.All(s => Math.Abs(s.Opacity - 0.4) < 1e-12 && s.WidthPt == 14),
            $"{strokes.Count} strokes, opacity {string.Join(",", strokes.Select(s => s.Opacity))}");
        Record("9.2 self-overlap composited once", Near(once, self, 3) && once.B is > 130 and < 175,
            $"single pass {once}, self-overlap {self}");
        Record("9.2 separate strokes darken", cross.B < single.B - 30, $"one stroke {single}, two strokes {cross}");
        Facts["screenOnce"] = once.ToString();
        Facts["screenSelf"] = self.ToString();
        File.WriteAllText(PhaseOneColor, $"{once.R},{once.G},{once.B}");
        Screenshot("a05-highlighter.png");
    }

    private static async Task DotAndCancel()
    {
        PressToolButton("PenToolButton");
        int count = Session.Current.Pages[0].Annotations.Count;
        await Drag(0, [new(420, 240)]);
        await Settle();
        var dot = Session.Current.Pages[0].Annotations[^1] as InkStroke;
        Record("7.2 click makes a dot", Session.Current.Pages[0].Annotations.Count == count + 1 && dot?.Points.Length == 1 &&
            Near(WindowColor(0, new(420, 240)), Red, 40), $"points {dot?.Points.Length}");

        long revision = Session.Revision;
        count = Session.Current.Pages[0].Annotations.Count;
        await Drag(0, [new(80, 420), new(200, 430), new(300, 420)], release: false);
        bool previewShown = Near(WindowColor(0, new(140, 425)), Red, 60);
        Key(VkEscape);
        await Task.Delay(50);
        mouse_event(LeftUp, 0, 0, 0, 0);
        await Settle();
        bool cleared = !Near(WindowColor(0, new(140, 425)), Red, 60);
        Record("4.3 Esc cancels a stroke", previewShown && cleared && Session.Revision == revision && Session.Current.Pages[0].Annotations.Count == count,
            $"preview shown {previewShown}, cleared {cleared}, revision {revision}→{Session.Revision}");

        await Drag(0, [new(80, 450), new(200, 460), new(300, 450)], release: false);
        mouse_event(RightDown, 0, 0, 0, 0);
        mouse_event(RightUp, 0, 0, 0, 0);
        await Task.Delay(50);
        mouse_event(LeftUp, 0, 0, 0, 0);
        await Settle();
        Record("5.6 right button never commits a stroke", Session.Revision == revision && Session.Current.Pages[0].Annotations.Count == count,
            $"revision {Session.Revision}");

        // Space held: the drag pans (temporary hand tool) and the pen draws nothing (spec 4.4).
        double offsetY = View.VerticalOffset;
        keybd_event(VkSpace, 0, 0, 0);
        await Task.Delay(50);
        await Drag(0, [new(150, 480), new(150, 420)]);
        keybd_event(VkSpace, 0, KeyUp, 0);
        await Settle();
        Record("4.4 Space+drag pans instead of drawing", Session.Revision == revision && View.VerticalOffset - offsetY > 20,
            $"offset moved {View.VerticalOffset - offsetY:0.#} DIP, revision {revision}→{Session.Revision}");
        await ShowPage0();
    }

    private static async Task UndoRedoKeys()
    {
        int count = Session.Current.Pages[0].Annotations.Count;
        View.Focus();
        Key(VkZ, control: true);
        await Settle();
        int afterUndo = Session.Current.Pages[0].Annotations.Count;
        Key(VkY, control: true);
        await Settle();
        int afterRedo = Session.Current.Pages[0].Annotations.Count;
        Key(VkZ, control: true, shift: false);
        Key(VkZ, control: true, shift: true);
        await Settle();
        Record("A18 Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z", afterUndo == count - 1 && afterRedo == count && Session.Current.Pages[0].Annotations.Count == count,
            $"{count} → undo {afterUndo} → redo {afterRedo} → undo+redo {Session.Current.Pages[0].Annotations.Count}");
    }

    private static async Task RotatedPage()
    {
        View.ScrollToPagePoint(1, new(140, 120));
        await Settle();
        int count = Session.Current.Pages[1].Annotations.Count;
        Point2[] path = [new(80, 100), new(200, 140)];
        await Drag(1, path);
        await Settle();
        var stroke = Session.Current.Pages[1].Annotations.Count == count + 1 ? Session.Current.Pages[1].Annotations[^1] as InkStroke : null;
        double tolerance = DevicePixelPt(1) * 1.5;
        Record("A25 stroke on a 90° page in canonical points", stroke is not null && stroke.Points[0].DistanceTo(path[0]) <= tolerance &&
            stroke.Points[^1].DistanceTo(path[1]) <= tolerance && Near(WindowColor(1, Mid(path[0], path[1])), Red, 40),
            stroke is null ? "no stroke" : $"start {stroke.Points[0]}, end {stroke.Points[^1]}, page rotation {Session.Current.Pages[1].Setup.Rotation}");
        Facts["rotatedStrokeMid"] = Mid(path[0], path[1]).ToString();
        await ShowPage0();
    }

    private static async Task SelectionAndStyle()
    {
        PressToolButton("SelectToolButton");
        var penId = Guid.Parse((string)Facts["penStrokeId"]);
        await Drag(0, [PenPath[2]]); // click on the pen stroke
        await Settle();
        bool selected = window.Selection.SelectedId == penId;
        var target = window.StyleTarget;
        Record("D04 click selects a stroke", selected && target is { Kind: AnnotationKind.Ink } t && t.Style.WidthPt == 3 && t.Style.Color == Red,
            $"selected {window.Selection.SelectedId}, style bar {target?.Style}");

        int undo = Session.UndoCount;
        ClickSwatch(Blue);
        await Settle();
        var stroke = (InkStroke)Session.Current.FindAnnotation(penId)!.Annotation;
        bool recolored = stroke.Color == Blue && Session.UndoCount == undo + 1 && Near(WindowColor(0, Mid(PenPath[0], PenPath[1])), Blue, 50);

        // A slider drag: many continuous changes, one Undo unit once committed (spec 13.2).
        foreach (double w in new[] { 4.0, 5.0, 6.5, 8.0 }) window.SetWidth(w, continuous: true);
        window.CommitStyleChange();
        stroke = (InkStroke)Session.Current.FindAnnotation(penId)!.Annotation;
        bool oneUnit = stroke.WidthPt == 8 && Session.UndoCount == undo + 2;
        View.Focus();
        Key(VkZ, control: true);
        await Settle();
        double undone = ((InkStroke)Session.Current.FindAnnotation(penId)!.Annotation).WidthPt;
        Key(VkY, control: true);
        await Settle();
        Record("D04 change selected stroke color/width (Undo unit each)", recolored && oneUnit && undone == 3 &&
            ((InkStroke)Session.Current.FindAnnotation(penId)!.Annotation).WidthPt == 8,
            $"color {stroke.Color}, width drag → {stroke.WidthPt} in one unit, undo → {undone}");

        int count = Session.Current.Pages[0].Annotations.Count;
        Key(VkDelete);
        await Settle();
        bool deleted = Session.Current.FindAnnotation(penId) is null && window.Selection.SelectedId is null;
        Key(VkZ, control: true);
        await Settle();
        Record("Delete selected stroke + Undo restores id and order", deleted && Session.Current.Pages[0].Annotations.Count == count &&
            Session.Current.Pages[0].IndexOf(penId) == 0, $"deleted {deleted}, restored index {Session.Current.Pages[0].IndexOf(penId)}");
        await Drag(0, [new(500, 500)]); // empty area clears the selection
        Record("click on empty area clears selection", window.Selection.SelectedId is null, "");
        PressToolButton("PenToolButton");
        Screenshot("d04-selection-edit.png");
    }

    private static async Task SaveAs()
    {
        window.SaveDialogOverride = suggested =>
        {
            Facts["suggestedName"] = suggested;
            return Editable;
        };
        bool saved = await window.SaveAsync(saveAs: true);
        await Settle();
        var model = Session.Current;
        byte[] bytes = File.ReadAllBytes(Editable);
        var read = EditablePdfReader.Read(bytes, PdfPageGeometryReader.Read(bytes));
        bool same = Serialized(read.Model) == Serialized(model);
        Record("A19 Save As writes an editable PDF", saved && !Session.IsDirty && window.Title.StartsWith("editable.pdf - ", StringComparison.Ordinal) &&
            window.Document.FilePath == Path.GetFullPath(Editable) && read.HeaderStatus == HeaderStatus.Valid && read.ReadOnlyCount == 0 && same,
            $"saved {saved}, title '{window.Title}', restored {read.RestoredCount}/{model.AllAnnotations.Count()}, identical metadata {same}");

        // Another viewer's view: PDFium with annotations on draws the saved appearances on rotated pages too.
        using var engine = new PdfiumDocument(bytes);
        var pen = (InkStroke)model.FindAnnotation(Guid.Parse((string)Facts["penStrokeId"]))!.Annotation;
        var penPixel = RenderedColor(engine, model.Pages[0].Setup, 0, Mid(pen.Points[0], pen.Points[1]));
        var rotatedMid = model.Pages[1].Annotations[^1] is InkStroke r ? Mid(r.Points[0], r.Points[^1]) : default;
        var rotatedPixel = RenderedColor(engine, model.Pages[1].Setup, 1, rotatedMid);
        var markerPixel = RenderedColor(engine, model.Pages[0].Setup, 0, HighlightSelf);
        Record("A20 saved strokes visible to a plain PDF renderer", Near(penPixel, Blue, 50) && Near(rotatedPixel, Red, 50) && markerPixel.B is > 130 and < 175,
            $"pen {penPixel}, 90° page {rotatedPixel}, highlighter self-overlap {markerPixel}");
    }

    private static async Task RepeatedSave()
    {
        var sizes = new List<long>();
        var counts = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            View.Focus();
            Key(VkS, control: true); // real Ctrl+S
            await WaitIdle();
            sizes.Add(new FileInfo(Editable).Length);
            counts.Add(OwnAnnotations(File.ReadAllBytes(Editable)));
        }
        int expected = Session.Current.AllAnnotations.Count();
        Record("A28 repeated Ctrl+S keeps one annotation per stroke and a stable size", counts.All(c => c == expected) && sizes.Max() - sizes.Min() <= 64,
            $"annotations {string.Join(",", counts)} (model {expected}), sizes {string.Join(",", sizes)}");
    }

    private static async Task SaveWhileEditing()
    {
        int savedCount = Session.Current.AllAnnotations.Count();
        AddStrokeByCommand(new(100, 600));
        var save = window.SaveAsync(saveAs: false);
        AddStrokeByCommand(new(140, 620)); // drawn while the save runs in the background
        bool ok = await save;
        await Settle();
        int inFile = OwnAnnotations(File.ReadAllBytes(Editable));
        bool dirtyAfter = Session.IsDirty && window.Title.Contains(" *");
        View.Focus();
        Key(VkZ, control: true);
        await Settle();
        Record("14.4 edits made during a save stay unsaved", ok && inFile == savedCount + 1 && dirtyAfter && !Session.IsDirty,
            $"file has {inFile} (snapshot {savedCount + 1}), dirty after save {dirtyAfter}, undo of the later stroke → clean {!Session.IsDirty}");
        Key(VkZ, control: true);
        await Settle();
        await window.SaveAsync(saveAs: false);
        await Settle();
    }

    private static async Task SaveFailures()
    {
        byte[] original = File.ReadAllBytes(Editable);
        AddStrokeByCommand(new(300, 600));
        var model = Session.Current;
        Messages.Clear();
        bool saved;
        using (new FileStream(Editable, FileMode.Open, FileAccess.Read, FileShare.None))
            saved = await window.SaveAsync(saveAs: false);
        await Settle();
        bool lockedOk = !saved && File.ReadAllBytes(Editable).AsSpan().SequenceEqual(original) && Session.IsDirty &&
            ReferenceEquals(Session.Current, model) && Messages.Any(m => m.Contains("다른 프로그램이 파일을 사용 중", StringComparison.Ordinal)) &&
            !Directory.EnumerateFiles(folder, "*.tmp").Any();
        Record("A23 locked target: original bytes and current work kept", lockedOk, $"saved {saved}, message '{Messages.LastOrDefault()?.Replace('\n', ' ')}'");

        Messages.Clear();
        File.SetAttributes(Editable, FileAttributes.ReadOnly);
        try { saved = await window.SaveAsync(saveAs: false); }
        finally { File.SetAttributes(Editable, FileAttributes.Normal); }
        Record("A23 read-only target: original bytes and current work kept", !saved && File.ReadAllBytes(Editable).AsSpan().SequenceEqual(original) &&
            Session.IsDirty && Messages.Any(m => m.Contains("읽기 전용", StringComparison.Ordinal)), $"message '{Messages.LastOrDefault()?.Replace('\n', ' ')}'");
        saved = await window.SaveAsync(saveAs: false);
        Record("save succeeds after the lock is gone", saved && !Session.IsDirty, "");
    }

    private static async Task ExternalChange()
    {
        AddStrokeByCommand(new(320, 640));
        File.SetLastWriteTimeUtc(Editable, DateTime.UtcNow.AddMinutes(5)); // another program touched the file
        byte[] before = File.ReadAllBytes(Editable);
        Prompts.Clear();
        Answers.Enqueue(MessageBoxResult.No);
        bool refused = !await window.SaveAsync(saveAs: false) && File.ReadAllBytes(Editable).AsSpan().SequenceEqual(before) && Session.IsDirty;
        Answers.Enqueue(MessageBoxResult.Yes);
        bool overwritten = await window.SaveAsync(saveAs: false) && !Session.IsDirty;
        Record("4.2 external change asks before overwriting", refused && overwritten && Prompts.Count == 2 && Prompts.All(p => p.Contains("변경되었습니다")),
            $"no → kept {refused}, yes → saved {overwritten}, prompts {Prompts.Count}");
        await Settle();
        File.WriteAllText(Expected, Serialized(Session.Current));
        Facts["expectedAnnotations"] = Session.Current.AllAnnotations.Count();
    }

    private static async Task UnsavedPrompts(string mixed)
    {
        var doc = window.Document;
        AddStrokeByCommand(new(400, 640));
        Prompts.Clear();
        Answers.Enqueue(MessageBoxResult.Cancel);
        bool cancelKeeps = !await window.OpenFileAsync(mixed) && ReferenceEquals(window.Document, doc) && Session.IsDirty;

        // Save chosen but the save fails: the switch is cancelled (spec 18).
        Answers.Enqueue(MessageBoxResult.Yes);
        bool failedSaveKeeps;
        using (new FileStream(Editable, FileMode.Open, FileAccess.Read, FileShare.None))
            failedSaveKeeps = !await window.CreateNoteAsync(NewPageSpec.Default) && ReferenceEquals(window.Document, doc) && Session.IsDirty;

        // Closing the window: Cancel keeps it open.
        Answers.Enqueue(MessageBoxResult.Cancel);
        window.Close();
        await Settle();
        bool closeCancelled = window.IsVisible && ReferenceEquals(window.Document, doc);

        Answers.Enqueue(MessageBoxResult.No);
        bool discarded = await window.OpenFileAsync(mixed) && !ReferenceEquals(window.Document, doc) && !Session.IsDirty;
        await Settle();
        bool fileUntouched = Serialized(EditablePdfReader.Read(File.ReadAllBytes(Editable), PdfPageGeometryReader.Read(Editable)).Model) == File.ReadAllText(Expected);

        // A new note asks for a path on first save; cancelling the dialog cancels the close.
        await window.CreateNoteAsync(NewPageSpec.Default);
        AddStrokeByCommand(new(100, 100));
        window.SaveDialogOverride = _ => null;
        Answers.Enqueue(MessageBoxResult.Yes);
        bool dialogCancelKeeps = !await window.OpenFileAsync(mixed) && Session.IsDirty;
        string newNote = Path.Combine(folder, "new-note.pdf");
        window.SaveDialogOverride = _ => newNote;
        Answers.Enqueue(MessageBoxResult.Yes);
        bool savedThenOpened = await window.OpenFileAsync(mixed) && File.Exists(newNote) && !Session.IsDirty;
        var noteRead = EditablePdfReader.Read(File.ReadAllBytes(newNote), PdfPageGeometryReader.Read(newNote));
        Record("A27 unsaved prompts: cancel / failed save / close cancel / don't save / save new note", cancelKeeps && failedSaveKeeps && closeCancelled &&
            discarded && fileUntouched && dialogCancelKeeps && savedThenOpened && noteRead.RestoredCount == 1,
            $"cancel {cancelKeeps}, failed save {failedSaveKeeps}, close cancel {closeCancelled}, don't save {discarded} (file untouched {fileUntouched}), " +
            $"dialog cancel {dialogCancelKeeps}, save new note then open {savedThenOpened} ({noteRead.RestoredCount} stroke), prompts {Prompts.Count}");
    }

    private static async Task RunReopenProcess()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("d04-reopen");
        start.ArgumentList.Add(folder);
        using var child = Process.Start(start)!;
        await child.WaitForExitAsync();
        string resultPath = Path.Combine(folder, "d04-reopen-result.json");
        string detail = File.Exists(resultPath)
            ? string.Join("; ", JsonDocument.Parse(File.ReadAllText(resultPath)).RootElement.GetProperty("Checks").EnumerateArray()
                .Select(c => $"{(c.GetProperty("Passed").GetBoolean() ? "PASS" : "FAIL")} {c.GetProperty("Name").GetString()}"))
            : "no result file";
        Record("A19 separate process reopen / re-edit / save (d04-reopen)", child.ExitCode == 0, $"exit {child.ExitCode}: {detail}");
    }

    // =====================================================================================================
    // Phase 2: a new process
    // =====================================================================================================

    private static async Task Reopen()
    {
        bool opened = await window.OpenFileAsync(Editable);
        await Settle();
        string expected = File.ReadAllText(Expected);
        var model = Session.Current;
        Record("A19 reopen restores every stroke with ids and values", opened && Serialized(model) == expected && Messages.Count == 0 && !Session.IsDirty,
            $"{model.AllAnnotations.Count()} strokes, identical metadata {Serialized(model) == expected}, notices {Messages.Count}");

        // The background no longer contains our appearances and the overlay draws each stroke once (no double darkening).
        var renderer = window.Document.Renderer;
        var setup = model.Pages[0].Setup;
        var page = renderer.Render(setup, 2, null, default);
        int x = (int)(HighlightSelf.X * 2), y = (int)(HighlightSelf.Y * 2), i = y * page.Stride + x * 4;
        bool backgroundClean = page.Bgra[i] > 245 && page.Bgra[i + 1] > 245 && page.Bgra[i + 2] > 245;
        await ShowPage0();
        var once = WindowColor(0, HighlightOnce);
        var self = WindowColor(0, HighlightSelf);
        byte[] drawn = File.ReadAllText(PhaseOneColor).Split(',').Select(byte.Parse).ToArray();
        var whenDrawn = Color.FromRgb(drawn[0], drawn[1], drawn[2]);
        Record("A19 no double appearance after reopen", backgroundClean && Near(once, whenDrawn, 3) && Near(once, self, 3),
            $"background pixel {(backgroundClean ? "white" : "not white")}, on screen {once} (when drawn {whenDrawn}), self-overlap {self}");
        Screenshot("d04-reopened.png");

        // Re-edit through the UI: select the pen stroke, recolor and widen, Ctrl+S, reopen.
        var pen = model.Pages[0].Annotations.OfType<InkStroke>().First(s => s.Points.Length > 1);
        PressToolButton("SelectToolButton");
        await Drag(0, [pen.Points[pen.Points.Length / 2]]);
        await Settle();
        bool selected = window.Selection.SelectedId == pen.Id;
        ClickSwatch(Green);
        window.SetWidth(10);
        View.Focus();
        Key(VkS, control: true);
        await WaitIdle();
        bool saved = !Session.IsDirty;
        await window.OpenFileAsync(Path.Combine(folder, "mixed.pdf"));
        await window.OpenFileAsync(Editable);
        await Settle();
        var again = Session.Current.FindAnnotation(pen.Id)?.Annotation as InkStroke;
        Record("A19 re-edit after reopen survives save and reopen", selected && saved && again is { WidthPt: 10 } && again.Color == Green &&
            Session.Current.AllAnnotations.Count() == model.AllAnnotations.Count(),
            $"selected {selected}, saved {saved}, reopened color {again?.Color} width {again?.WidthPt}");
    }

    // =====================================================================================================
    // helpers
    // =====================================================================================================

    private static void AddStrokeByCommand(Point2 at)
    {
        var page = Session.Current.Pages[0];
        var stroke = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [at, new(at.X + 30, at.Y + 5)], 2, RgbColor.Black, 1);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, page.Id, stroke, label: "펜"));
    }

    private static string Serialized(DocumentModel model)
    {
        var s = SchemaV1.Serialize(model);
        return JsonSerializer.Serialize(new { header = Convert.ToBase64String(s.Header), annotations = s.Annotations.Select(a => Convert.ToBase64String(a.Envelope)) });
    }

    private static int OwnAnnotations(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf);
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(stream, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        int count = 0;
        foreach (PdfSharp.Pdf.PdfPage page in document.Pages)
            if (page.Elements.GetArray("/Annots") is { } annots)
                for (int i = 0; i < annots.Elements.Count; i++)
                    if ((annots.Elements[i] is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : annots.Elements[i]) is PdfSharp.Pdf.PdfDictionary d &&
                        d.Elements.GetString("/NM").StartsWith(EditablePdfKeys.NamePrefix, StringComparison.Ordinal)) count++;
        return count;
    }

    private static Color RenderedColor(PdfiumDocument engine, PageSetup setup, int pageIndex, Point2 canonical)
    {
        var page = engine.Render(pageIndex, 2);
        var d = setup.Frame.CanonicalToDisplay.Transform(canonical);
        int i = (int)(d.Y * 2) * page.Stride + (int)(d.X * 2) * 4;
        return Color.FromRgb(page.Bgra[i + 2], page.Bgra[i + 1], page.Bgra[i]);
    }

    private static Point2 Mid(Point2 a, Point2 b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private static double DevicePixelPt(int page) => View.PageTransform(page).DevicePixelLengthToPoints(1);

    private static bool Near(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static bool Near(Color a, RgbColor b, int tolerance) => Near(a, Color.FromRgb(b.R, b.G, b.B), tolerance);

    private static void PressToolButton(string name)
    {
        ((System.Windows.Controls.Primitives.ToggleButton)window.FindName(name)).IsChecked = true;
    }

    private static void ClickSwatch(RgbColor color)
    {
        var panel = (Panel)window.FindName("PalettePanel");
        var swatch = panel.Children.OfType<Button>().First(b => Equals(b.DataContext, color));
        swatch.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    /// <summary>Colour of the window pixel showing a canonical point of a page (device pixels of a render of the window).</summary>
    private static Color WindowColor(int pageIndex, Point2 pagePoint)
    {
        var dip = View.PageTransform(pageIndex).PageToDip.Transform(pagePoint);
        var inWindow = View.TranslatePoint(new Point(dip.X, dip.Y), window);
        var bitmap = Capture();
        double dpi = View.DpiScale;
        int x = (int)Math.Floor(inWindow.X * dpi), y = (int)Math.Floor(inWindow.Y * dpi);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    /// <summary>Real OS mouse: press at the first page point, move through the path in ~6px steps, release.</summary>
    private static async Task Drag(int pageIndex, IReadOnlyList<Point2> path, Func<Task>? midway = null, bool release = true)
    {
        var screen = path.Select(p => ScreenOf(pageIndex, p)).ToList();
        SetCursorPos((int)Math.Round(screen[0].X), (int)Math.Round(screen[0].Y));
        await Task.Delay(40);
        mouse_event(LeftDown, 0, 0, 0, 0);
        await Task.Delay(30);
        // Do not start moving until WPF has consumed the button-down at the initial position.
        // On a busy render thread a fixed 30ms sleep can deliver the down after the cursor has already moved.
        var captureWatch = Stopwatch.StartNew();
        while (!View.IsMouseCaptured && captureWatch.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
        for (int s = 1; s < screen.Count; s++)
        {
            var a = screen[s - 1];
            var b = screen[s];
            int steps = Math.Max(1, (int)((b - a).Length / 6));
            for (int k = 1; k <= steps; k++)
            {
                var p = a + (b - a) * k / steps;
                SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y));
                await Task.Delay(6);
            }
            await Task.Delay(25); // dwell on the vertex so a coalesced mouse move cannot skip the corner sample
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (midway is not null && s == 1) await midway();
        }
        if (release)
        {
            await Task.Delay(100);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            mouse_event(LeftUp, 0, 0, 0, 0);
        }
        await Task.Delay(60);
    }

    private static Point ScreenOf(int pageIndex, Point2 pagePoint)
    {
        var dip = View.PageTransform(pageIndex).PageToDip.Transform(pagePoint);
        return View.PointToScreen(new Point(dip.X, dip.Y));
    }

    private static async Task<bool> Foreground()
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        for (int attempt = 0; attempt < 10 && GetForegroundWindow() != hwnd; attempt++)
        {
            // Windows only lets a process take the foreground after input; a released Alt key counts as such input.
            keybd_event(VkAlt, 0, 0, 0);
            keybd_event(VkAlt, 0, KeyUp, 0);
            window.Activate();
            SetForegroundWindow(hwnd);
            await Task.Delay(200);
        }
        return GetForegroundWindow() == hwnd;
    }

    private static void Key(byte vk, bool control = false, bool shift = false)
    {
        if (control) keybd_event(VkControl, 0, 0, 0);
        if (shift) keybd_event(VkShift, 0, 0, 0);
        keybd_event(vk, 0, 0, 0);
        keybd_event(vk, 0, KeyUp, 0);
        if (shift) keybd_event(VkShift, 0, KeyUp, 0);
        if (control) keybd_event(VkControl, 0, KeyUp, 0);
    }

    private static async Task WaitIdle()
    {
        var watch = Stopwatch.StartNew();
        await Task.Delay(100);
        while (window.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(50);
        await Settle();
    }

    /// <summary>Waits until layout, the debounced render plan and all renders have finished.</summary>
    private static async Task Settle()
    {
        var watch = Stopwatch.StartNew();
        do
        {
            await Task.Delay(220);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        while (View.Scheduler is { IsIdle: false } && watch.Elapsed < TimeSpan.FromSeconds(20));
        await Task.Delay(60);
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static RenderTargetBitmap Capture()
    {
        double dpi = View.DpiScale;
        var bitmap = new RenderTargetBitmap((int)Math.Round(window.ActualWidth * dpi), (int)Math.Round(window.ActualHeight * dpi),
            96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        return bitmap;
    }

    private static void Screenshot(string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Capture()));
        using var file = File.Create(Path.Combine(folder, name));
        encoder.Save(file);
    }

    private const byte VkAlt = 0x12, VkSpace = 0x20, VkControl = 0x11, VkShift = 0x10, VkEscape = 0x1B, VkDelete = 0x2E, VkZ = 0x5A, VkY = 0x59, VkS = 0x53;
    private const uint KeyUp = 0x2, LeftDown = 0x2, LeftUp = 0x4, RightDown = 0x8, RightUp = 0x10;
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nuint extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
