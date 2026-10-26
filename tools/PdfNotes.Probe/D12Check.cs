using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdfNotes.App;
using PdfNotes.App.Canvas;
using PdfNotes.App.Services;
using PdfNotes.App.Tools;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using DashStyle = PdfNotes.Core.Models.DashStyle;

namespace PdfNotes.Probe;

/// <summary>
/// D12 checks in the real app. <c>d12-logic</c> never uses the OS mouse or keyboard and never takes the foreground: the
/// window is shown without activation outside the screen and tools are driven through their pointer methods (Flatten
/// export, automatic backup, a real kill + recovery, shape/text tool click selection). <c>d12</c> uses real input for what
/// only the OS can drive (re-click, double-click and typing into a selected text box, settings window, toolbar button).
/// </summary>
internal static class D12Check
{
    private static MainWindow window = null!;
    private static string folder = "", settingsPath = "";
    private static readonly List<object> checks = [];
    private static readonly List<string> messages = [], prompts = [];
    private static readonly Queue<MessageBoxResult> answers = new();
    private static bool osInput;
    private static DocumentSession Session => window.Document.Session;
    private static DocumentView View => (DocumentView)window.FindName("View");
    private static string Status => ((System.Windows.Controls.TextBlock)window.FindName("StatusText")).Text;

    private static void Check(string name, bool passed, string detail = "")
    {
        checks.Add(new { Name = name, Passed = passed, Detail = detail });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        if (!passed) throw new InvalidOperationException(name + ": " + detail);
    }

    public static int Run(string output, string part)
    {
        folder = output;
        Directory.CreateDirectory(folder);
        settingsPath = Path.Combine(folder, "settings", "settings.json");
        Environment.SetEnvironmentVariable("PDFNOTES_SETTINGS_DIR", Path.GetDirectoryName(settingsPath));
        osInput = part == "d12";
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                if (part == "d12-crash-child") { await CrashChild(); return; }
                if (part == "d12-logic") { await Flatten(); await Backup(); await Crash(); await Tools(); }
                else await RealInput();
                result = 0;
            }
            catch (Exception e) { checks.Add(new { Name = "harness", Passed = false, Detail = e.ToString() }); Console.WriteLine(e); }
            finally
            {
                if (part != "d12-crash-child")
                    File.WriteAllText(Path.Combine(folder, part + "-result.json"), JsonSerializer.Serialize(new
                    { Passed = result == 0, Input = osInput ? "os-mouse-keyboard" : "none (pointer methods, window not activated, off screen)", Checks = checks, Messages = messages, Prompts = prompts },
                        new JsonSerializerOptions { WriteIndented = true }));
                if (window is not null) { window.PromptOverride = (_, _) => MessageBoxResult.No; window.Close(); }
                app.Shutdown();
            }
        };
        app.Run();
        return result;
    }

    // ----- window -----

    private static MessageBoxResult Answer(string text, MessageBoxButton buttons)
    {
        prompts.Add(text);
        return answers.Count > 0 ? answers.Dequeue() : MessageBoxResult.No;
    }

    /// <summary>A new main window with <paramref name="configure"/> applied to the stored settings first (the old one is closed, changes discarded).</summary>
    private static async Task NewWindow(Action<AppSettings>? configure = null)
    {
        if (window is not null)
        {
            window.PromptOverride = (_, _) => MessageBoxResult.No;
            window.Close();
            await Settle(400);
        }
        var settings = AppSettings.Load(settingsPath);
        configure?.Invoke(settings);
        settings.Save(settingsPath);
        window = osInput
            ? new MainWindow { Width = 1420, Height = 900, WindowStartupLocation = WindowStartupLocation.CenterScreen }
            : new MainWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft - 3000, Top = SystemParameters.VirtualScreenTop, Width = 1420, Height = 900 };
        window.MessageSink = messages.Add;
        window.PromptOverride = Answer;
        window.Show();
        await Settle(300);
    }

    private static Task Settle(int ms = 150) => Task.Delay(ms);

    private static async Task<bool> Until(Func<bool> condition, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.ElapsedMilliseconds > timeoutMs) return false; await Task.Delay(50); }
        return true;
    }

    private static Annotation Add(Annotation annotation, int page = 0)
    {
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[page].Id, annotation));
        return annotation;
    }

    private static InkStroke Ink(double x, double y, int points = 3) =>
        new(Guid.NewGuid(), false, Affine.Identity, [.. Enumerable.Range(0, points).Select(i => new Point2(x + i * 4, y + 10 * Math.Sin(i / 3.0)))], 3, new(0xD0, 0x20, 0x20), 1);

    private static ShapeAnnotation Rectangle(double x, double y) =>
        new(Guid.NewGuid(), false, Affine.Identity, ShapeKind.Rectangle, new(x, y, 120, 70), [], ShapeStyle.Default);

    private static LineAnnotation Line(double x1, double y1, double x2, double y2) =>
        new(Guid.NewGuid(), false, Affine.Identity, LineKind.Straight, new(x1, y1), new(x2, y2), [], 3, DashStyle.Solid, false, false, new(0x20, 0x40, 0xD0), 1);

    private static TextBoxAnnotation Text(double x, double y, string text) =>
        new(Guid.NewGuid(), false, Affine.Translation(x, y), text, [new TextRun(0, text.Length, "Malgun Gothic", 14, RgbColor.Black, false, false, false, false)],
            PdfNotes.Core.Models.TextAlignment.Left, 180, TextBoxStyle.Default, []);

    /// <summary>A 60×40 PNG: opaque green left half, 50% transparent blue right half.</summary>
    private static byte[] TranslucentPng()
    {
        var pixels = new byte[60 * 40 * 4];
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 60; x++)
            {
                int i = (y * 60 + x) * 4;
                if (x < 30) { pixels[i + 1] = 160; pixels[i + 3] = 255; }
                else { pixels[i] = 200; pixels[i + 3] = 128; }
            }
        var bitmap = BitmapSource.Create(60, 40, 96, 96, PixelFormats.Bgra32, null, pixels, 60 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    private static Point Dip(Point2 pagePoint, int page = 0)
    {
        var p = View.PageTransform(page).PageToDip.Transform(pagePoint);
        return new(p.X, p.Y);
    }

    /// <summary>Press, move and release through the tool methods, as the view routes them (the press may hand over to another tool).</summary>
    private static async Task ToolDrag(Point2 from, Point2 to, int steps = 6)
    {
        Point a = Dip(from), b = Dip(to);
        View.Tool!.PointerDown(Ptr(a, ModifierKeys.None, 1));
        var tool = View.Tool!;
        if (tool.IsActive)
        {
            for (int i = 1; i <= steps; i++) tool.PointerMove(Ptr(new(a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps), ModifierKeys.None));
            tool.PointerUp(Ptr(b, ModifierKeys.None));
        }
        await Settle();
    }

    private static Task ToolClick(Point2 at) => ToolDrag(at, at, steps: 0);

    private static void Snapshot(string name)
    {
        double dpi = View.DpiScale;
        var bitmap = new RenderTargetBitmap((int)Math.Round(window.ActualWidth * dpi), (int)Math.Round(window.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
    }

    private static double Difference(byte[] a, byte[] b)
    {
        using var x = new PdfiumDocument(a); using var y = new PdfiumDocument(b);
        double worst = 0;
        for (int i = 0; i < x.PageCount; i++)
        {
            var p = x.Render(i, 1, annotations: true); var q = y.Render(i, 1, annotations: true);
            worst = Math.Max(worst, p.Bgra.Zip(q.Bgra, (m, n) => Math.Abs(m - n)).Average());
        }
        return worst;
    }

    // ----- Flatten export (spec 14.5) -----

    private static async Task Flatten()
    {
        await NewWindow(s => s.AutoBackup = false);
        string source = Path.Combine(folder, "lecture.pdf");
        ForeignAnnotationSample.Create(source);
        using (var pdf = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
        {
            // A foreign annotation without an appearance: it cannot be merged.
            var bare = new PdfDictionary(pdf);
            bare.Elements.SetName("/Type", "/Annot"); bare.Elements.SetName("/Subtype", "/Square"); bare.Elements.SetInteger("/F", 4);
            bare.Elements["/Rect"] = new PdfArray(pdf, new PdfReal(300), new PdfReal(60), new PdfReal(360), new PdfReal(100));
            pdf.Internals.AddObject(bare); pdf.Pages[0].Elements.GetArray("/Annots")!.Elements.Add(bare.Reference!);
            pdf.Save(source);
        }
        Check("open a PDF with other apps' annotations", await window.OpenFileAsync(source));
        Add(Ink(80, 700, 30)); Add(Rectangle(300, 600)); Add(Text(70, 520, "Flatten 한글 텍스트"));
        Add(new LineAnnotation(Guid.NewGuid(), false, Affine.Identity, LineKind.Cubic, new(80, 420), new(300, 430), [new(140, 340), new(240, 500)], 3,
            DashStyle.Dash, false, true, new(0x1E, 0x6F, 0xD9), .7));
        Add(new FreeHighlightStroke(Guid.NewGuid(), false, Affine.Identity, [new(330, 430), new(420, 440), new(520, 430)], 16, new(0xFF, 0xE6, 0x00), .4));
        Add(new ShapeAnnotation(Guid.NewGuid(), false, Affine.Rigid(.3, 420, 560), ShapeKind.Ellipse, new(0, 0, 110, 60), [],
            ShapeStyle.Default with { FillColor = new(0xE5, 0x39, 0x35), FillOpacity = .5, BorderDash = DashStyle.Dot }));
        var png = Resource.Create(Guid.NewGuid(), "image/png", TranslucentPng());
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id,
            new ImageAnnotation(Guid.NewGuid(), false, Affine.Rigid(-.2, 440, 650), png.Id, new(0, 0, 90, 60)), png));
        Session.Execute(PageTransforms.Shrink(Session.Current, [Session.Current.Pages[1].Id], 80));
        string editable = Path.Combine(folder, "lecture-editable.pdf");
        Check("save the editable PDF", await window.SaveToAsync(editable) && !Session.IsDirty);
        Add(Ink(200, 300, 20)); // unsaved change: Flatten uses the in-memory state
        int undo = Session.UndoCount; long revision = Session.Revision;

        string? suggested = null;
        string flat = Path.Combine(folder, "lecture-editable_flattened.pdf");
        window.SaveDialogOverride = name => { suggested = name; return flat; };
        answers.Enqueue(MessageBoxResult.No);
        Check("declining the kept foreign annotation notice writes nothing", !await window.ExportFlattenedAsync() && !File.Exists(flat) &&
            prompts[^1].Contains("다른 프로그램에서 만든 주석 1개", StringComparison.Ordinal), prompts[^1]);
        Check("the default name is name_flattened.pdf", suggested == "lecture-editable_flattened.pdf", suggested ?? "");
        answers.Enqueue(MessageBoxResult.Yes);
        Check("Flatten export writes the file", await window.ExportFlattenedAsync() && File.Exists(flat));
        byte[] flatBytes = File.ReadAllBytes(flat);
        FlattenWriter.Verify(flatBytes, Session.Current.Pages.Count);
        Check("the document keeps its file, unsaved mark and Undo", window.Document.FilePath == editable && Session.IsDirty && window.Title.Contains('*') &&
            Session.UndoCount == undo && Session.Revision == revision, window.Title);
        string copy = Path.Combine(folder, "lecture-current.pdf");
        await window.Document.WriteCopyAsync(copy);
        double difference = Difference(File.ReadAllBytes(copy), flatBytes);
        Check("the flattened pages look like the editable state (unsaved change included)", difference < 1, $"mean channel difference {difference:0.###}");
        using (var engine = new PdfiumDocument(flatBytes))
            Check("source text and the text box stay searchable text", string.Concat(engine.ExtractText(0).Select(c => c.Text)) is var t &&
                t.Contains("Original PDF", StringComparison.Ordinal) && t.Contains("Flatten 한글 텍스트", StringComparison.Ordinal));

        // The document's own file is never a target of a merged export (spec 14.5, D14g): refused with a short notice.
        byte[] before = File.ReadAllBytes(editable);
        window.SaveDialogOverride = _ => editable;
        int asked = prompts.Count;
        Check("choosing the document's own file is refused and keeps it", !await window.ExportFlattenedAsync() && File.ReadAllBytes(editable).SequenceEqual(before) &&
            prompts.Count == asked && messages[^1].Contains("열려 있는 문서 파일", StringComparison.Ordinal), messages.LastOrDefault() ?? "");

        // Opened in the app the flattened file is a plain PDF: nothing to edit, new annotations can be added and saved.
        answers.Enqueue(MessageBoxResult.No); // discard the unsaved change
        Check("open the flattened file", await window.OpenFileAsync(flat));
        Check("its merged annotations are not editable", !Session.Current.AllAnnotations.Any() && !messages.Any(m => m.Contains("보기 전용", StringComparison.Ordinal)));
        Add(Ink(100, 100, 10));
        string again = Path.Combine(folder, "flattened-plus-new.pdf");
        Check("a new annotation on the flattened file saves and reopens", await window.SaveToAsync(again) &&
            EditablePdfReader.Read(File.ReadAllBytes(again), PdfPageGeometryReader.Read(File.ReadAllBytes(again))).Model.AllAnnotations.Count() == 1);
        Snapshot("d12-flattened-reopened.png");
        window.SaveDialogOverride = null;
    }

    // ----- automatic backup (spec 18.2) -----

    private static string BackupFolder => Path.Combine(folder, "settings", "Backups");

    private static async Task Backup()
    {
        await NewWindow(s => { s.AutoBackup = true; s.BackupMode = BackupMode.Folder; s.BackupKeep = 2; s.BackupFolder = null; });
        window.BackupIntervalOverride = TimeSpan.FromSeconds(1.5);
        Check("nothing is backed up while the note is unchanged", !await Until(() => window.LastBackupFile is not null, 2500));
        Add(Ink(100, 100));
        Check("a changed note is backed up after the interval", await Until(() => window.LastBackupFile is not null, 6000), window.LastBackupFile ?? "");
        string first = window.LastBackupFile!;
        Check("backup file name and folder", Path.GetDirectoryName(first) == BackupFolder && Path.GetFileName(first).StartsWith("새 노트_", StringComparison.Ordinal), first);
        Check("folder backup keeps the document unsaved and without a file", Session.IsDirty && window.Document.FilePath is null && window.Title.Contains('*'));
        Check("the backup is an editable PDF of the note", EditablePdfReader.Read(File.ReadAllBytes(first), PdfPageGeometryReader.Read(File.ReadAllBytes(first))).Model.AllAnnotations.Count() == 1);
        Check("the status bar shows the backup time briefly", Status.StartsWith("자동 백업", StringComparison.Ordinal), Status);
        var stamp = window.LastBackupTime;
        Check("the same state is not backed up again", !await Until(() => window.LastBackupTime != stamp, 3000));

        // A stroke in progress defers the backup until it ends.
        window.SetTool(ToolKind.Pen);
        Add(Ink(150, 160));
        View.Tool!.PointerDown(Ptr(Dip(new(300, 300)), ModifierKeys.None, 1));
        View.Tool!.PointerMove(Ptr(Dip(new(320, 310)), ModifierKeys.None));
        Check("no backup while a stroke is drawn", !await Until(() => window.LastBackupTime != stamp, 3500) && View.IsToolActive);
        View.Tool!.PointerUp(Ptr(Dip(new(340, 320)), ModifierKeys.None));
        Check("the deferred backup follows when the stroke ends", await Until(() => window.LastBackupTime != stamp, 4000));

        // Keep 2 per document: a third backup removes the oldest.
        await Until(() => !window.IsBackingUp, 3000);
        stamp = window.LastBackupTime; Add(Ink(400, 200));
        await Task.Delay(1100); // a new second for the file name
        Check("a third backup", await Until(() => window.LastBackupTime != stamp, 6000));
        var kept = Directory.GetFiles(BackupFolder, "새 노트_*.pdf");
        Check("only the newest 2 backups of the note remain", kept.Length == 2 && !File.Exists(first), string.Join(", ", kept.Select(Path.GetFileName)));

        // Input keeps going during a backup of a heavy document.
        for (int i = 0; i < 400; i++) Add(Ink(20 + i % 20 * 25, 40 + i / 20 * 30, 120));
        window.BackupIntervalOverride = TimeSpan.FromHours(1);
        var run = window.RunBackupAsync();
        double worst = 0; int samples = 0;
        while (!run.IsCompleted)
        {
            var watch = Stopwatch.StartNew();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            worst = Math.Max(worst, watch.Elapsed.TotalMilliseconds); samples++;
            await Task.Delay(5);
        }
        Check("a heavy backup runs in the background (input stays responsive)", await run && samples > 3 && worst < 100, $"{samples} samples while writing, worst input latency {worst:0.0} ms");

        // Original-file mode: the same safe save as Ctrl+S.
        string original = Path.Combine(folder, "original-mode.pdf");
        ProbeSamplePdf.Create(original, 1);
        await NewWindow(s => { s.BackupMode = BackupMode.Original; s.BackupKeep = 10; });
        Check("open for original mode", await window.OpenFileAsync(original));
        window.BackupIntervalOverride = TimeSpan.FromSeconds(1.5);
        Add(Ink(120, 400));
        int undo = Session.UndoCount;
        Check("original mode saves the file itself", await Until(() => !Session.IsDirty, 6000) && window.LastBackupFile is null);
        Check("Undo stays and the file holds the change", Session.UndoCount == undo && Session.CanUndo &&
            EditablePdfReader.Read(File.ReadAllBytes(original), PdfPageGeometryReader.Read(File.ReadAllBytes(original))).Model.AllAnnotations.Count() == 1);
        // A read-only original is not touched: the backup goes to the folder.
        File.SetAttributes(original, FileAttributes.ReadOnly);
        byte[] locked = File.ReadAllBytes(original);
        Add(Ink(200, 450));
        Check("a read-only original is backed up into the folder instead", await Until(() => window.LastBackupFile is not null, 6000) &&
            File.ReadAllBytes(original).SequenceEqual(locked) && Session.IsDirty && Path.GetFileName(window.LastBackupFile!).StartsWith("original-mode_", StringComparison.Ordinal),
            window.LastBackupFile ?? "");
        File.SetAttributes(original, FileAttributes.Normal);

        // A failure is a short status, never a modal message, and is tried again later.
        string blocker = Path.Combine(folder, "not-a-folder");
        File.WriteAllText(blocker, "file");
        await NewWindow(s => { s.BackupMode = BackupMode.Folder; s.BackupFolder = blocker; });
        window.BackupIntervalOverride = TimeSpan.FromSeconds(1);
        int messageCount = messages.Count, promptCount = prompts.Count;
        Add(Ink(100, 100));
        Check("a failed backup shows a status message only", await Until(() => Status.StartsWith("자동 백업 실패", StringComparison.Ordinal), 5000) &&
            messages.Count == messageCount && prompts.Count == promptCount, Status);
        await NewWindow(s => s.BackupFolder = null);
    }

    // ----- abnormal end and recovery (spec 18.2) -----

    private static async Task Crash()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        start.ArgumentList.Add("d12-crash-child"); start.ArgumentList.Add(folder);
        using var child = Process.Start(start)!;
        string? line = null;
        var reading = Task.Run(() => { while ((line = child.StandardOutput.ReadLine()) is not null && !line.StartsWith("BACKUP ", StringComparison.Ordinal)) { } return line; });
        bool ready = await Task.WhenAny(reading, Task.Delay(30000)) == reading && reading.Result is not null;
        Check("a second app process backs up its unsaved note", ready, line ?? "timeout");
        child.Kill(entireProcessTree: true); child.WaitForExit();
        string backup = line!["BACKUP ".Length..];
        Check("the process was killed (no normal end)", child.HasExited && Directory.GetFiles(Path.Combine(folder, "settings", "sessions"), "*.json").Length >= 1);

        answers.Enqueue(MessageBoxResult.No);
        await NewWindow();
        Check("restart offers the backup; ignoring keeps the file", !await window.OfferRecoveryAsync() && prompts[^1].Contains("정상적으로 종료되지 않았습니다", StringComparison.Ordinal) &&
            File.Exists(backup), prompts[^1]);
        Check("it is asked once", !await window.OfferRecoveryAsync());

        // Again, and this time open it.
        using var second = Process.Start(start)!;
        line = null;
        reading = Task.Run(() => { while ((line = second.StandardOutput.ReadLine()) is not null && !line.StartsWith("BACKUP ", StringComparison.Ordinal)) { } return line; });
        ready = await Task.WhenAny(reading, Task.Delay(30000)) == reading && reading.Result is not null;
        second.Kill(entireProcessTree: true); second.WaitForExit();
        answers.Enqueue(MessageBoxResult.Yes);
        Check("opening the offered backup restores the note", ready && await window.OfferRecoveryAsync() && window.Document.FilePath == Path.GetFullPath(line!["BACKUP ".Length..]) &&
            Session.Current.AllAnnotations.Count() == 1, window.Document.FilePath ?? "");

        // A normal end leaves nothing to recover.
        await NewWindow();
        Check("after a normal end nothing is offered", !await window.OfferRecoveryAsync());
    }

    /// <summary>Child process: an unsaved note, one backup, then it waits to be killed.</summary>
    private static async Task CrashChild()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        await NewWindow(s => { s.AutoBackup = true; s.BackupMode = BackupMode.Folder; s.BackupKeep = 10; });
        window.BackupIntervalOverride = TimeSpan.FromSeconds(1);
        Add(Ink(150, 150));
        if (await Until(() => window.LastBackupFile is not null, 20000)) Console.WriteLine("BACKUP " + window.LastBackupFile);
        Console.Out.Flush();
        await Task.Delay(Timeout.Infinite);
    }

    // ----- shape and text tool click selection (spec 5.4, D12 tasks 9-10) -----

    private static async Task Tools()
    {
        await NewWindow(s => { s.AutoBackup = false; s.TextBox = AppSettings.TextBoxSettings.From(TextBoxStyle.Default); });
        View.SetZoom(1); await Settle(); View.GoToPage(0); await Settle(300);

        var shape = (ShapeAnnotation)Add(Rectangle(100, 100));
        window.SetShapeKind(ShapeKind.Rectangle); await Settle();
        Check("shape tool chosen", window.CurrentTool == ToolKind.Shape);
        int count = Session.Current.AllAnnotations.Count();
        await ToolClick(new(160, 135));
        Check("a click on a shape selects it with the selection tool", window.CurrentTool == ToolKind.Select && window.Selection.SelectedIds.SequenceEqual([shape.Id]) &&
            window.ReturnTool == ToolKind.Shape && Session.Current.AllAnnotations.Count() == count, $"tool {window.CurrentTool}, return {window.ReturnTool}");
        int undo = Session.UndoCount;
        await ToolDrag(new(160, 135), new(200, 175));
        var moved = (ShapeAnnotation)Session.Current.FindAnnotation(shape.Id)!.Annotation;
        Check("dragging it moves it (one Undo) and keeps it selected (spec 5.4, D14g)", Session.UndoCount == undo + 1 && Math.Abs(moved.Transform.OffsetX - 40) < 1 &&
            window.CurrentTool == ToolKind.Select && window.ReturnTool == ToolKind.Shape, $"offset {moved.Transform.OffsetX:0.0}, tool {window.CurrentTool}");
        await ToolClick(new(500, 700));
        Check("an empty click goes back to the shape tool", window.CurrentTool == ToolKind.Shape && Session.UndoCount == undo + 1, $"tool {window.CurrentTool}");
        await ToolDrag(new(210, 180), new(330, 260));
        Check("a drag that starts on a shape draws a new one", Session.Current.AllAnnotations.Count() == count + 1 && window.CurrentTool == ToolKind.Shape);
        await ToolClick(new(500, 500));
        Check("a click on empty space draws nothing and selects nothing", Session.Current.AllAnnotations.Count() == count + 1 && window.CurrentTool == ToolKind.Shape);
        window.SetShapeKind(ShapeKind.Polygon); await Settle();
        await ToolClick(new(60, 400));
        await ToolClick(new(200, 175)); // on the moved rectangle while the polygon is being built
        var building = View.Tool as ShapeTool;
        Check("clicks of a polygon in progress stay corners", window.CurrentTool == ToolKind.Shape && building is { IsBuildingPolygon: true });
        View.CancelTool();
        await ToolClick(new(200, 175));
        Check("the first click of the polygon tool on a shape selects it", window.CurrentTool == ToolKind.Select && window.ReturnTool == ToolKind.Shape);
        window.Selection.Select(null); window.SetTool(ToolKind.Pen); await Settle();

        // Line and curve tools select like the shape tool; every shape group tool picks lines and shapes (2026-10-13 user request).
        var line = (LineAnnotation)Add(Line(100, 620, 300, 620));
        window.SetTool(ToolKind.Line); await Settle();
        count = Session.Current.AllAnnotations.Count();
        await ToolClick(new(200, 620));
        Check("a line tool click on a line selects it with the selection tool", window.CurrentTool == ToolKind.Select && window.Selection.SelectedIds.SequenceEqual([line.Id]) &&
            window.ReturnTool == ToolKind.Line && Session.Current.AllAnnotations.Count() == count, $"tool {window.CurrentTool}, return {window.ReturnTool}");
        await ToolClick(new(500, 760));
        Check("emptying that selection goes back to the line tool", window.CurrentTool == ToolKind.Line, $"tool {window.CurrentTool}");
        await ToolDrag(new(200, 620), new(260, 700));
        Check("a line tool drag that starts on a line draws a new one", Session.Current.AllAnnotations.Count() == count + 1 && window.CurrentTool == ToolKind.Line);
        window.SetTool(ToolKind.Curve); await Settle();
        await ToolClick(new(200, 175)); // on the moved rectangle
        Check("a curve tool click on a shape selects it", window.CurrentTool == ToolKind.Select && window.Selection.SelectedIds.SequenceEqual([shape.Id]) &&
            window.ReturnTool == ToolKind.Curve, $"tool {window.CurrentTool}, return {window.ReturnTool}");
        undo = Session.UndoCount;
        await ToolDrag(new(200, 175), new(210, 185));
        Check("moving it (one Undo) keeps it selected (spec 5.4, D14g)", Session.UndoCount == undo + 1 && window.CurrentTool == ToolKind.Select && window.ReturnTool == ToolKind.Curve, $"tool {window.CurrentTool}");
        await ToolClick(new(500, 760));
        Check("an empty click goes back to the curve tool", window.CurrentTool == ToolKind.Curve, $"tool {window.CurrentTool}");
        window.SetShapeKind(ShapeKind.Rectangle); await Settle();
        await ToolClick(new(120, 620)); // away from the line drawn above, which starts at (200, 620)
        Check("a shape tool click on a line selects it", window.CurrentTool == ToolKind.Select && window.Selection.SelectedIds.SequenceEqual([line.Id]) &&
            window.ReturnTool == ToolKind.Shape, $"tool {window.CurrentTool}, return {window.ReturnTool}");
        window.Selection.Select(null); window.SetTool(ToolKind.Pen); await Settle();

        var box = (TextBoxAnnotation)Add(Text(100, 450, "텍스트 상자"));
        window.SetTool(ToolKind.Text); await Settle();
        await ToolClick(new(130, 460));
        Check("a text tool click on a box selects it (no editing yet)", window.CurrentTool == ToolKind.Select && window.Selection.SelectedIds.SequenceEqual([box.Id]) &&
            window.ReturnTool == ToolKind.Text && window.ActiveTextEditor is null, $"tool {window.CurrentTool}, editor {window.ActiveTextEditor is not null}");
        undo = Session.UndoCount;
        window.SetTool(ToolKind.Text); await Settle();
        await ToolDrag(new(130, 460), new(160, 490));
        var movedBox = (TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation;
        Check("a drag from the text tool moves the box (one Undo) and keeps it selected (spec 5.4, D14g)", Session.UndoCount == undo + 1 &&
            Math.Abs(movedBox.Transform.OffsetX - 130) < 1 && Math.Abs(movedBox.Transform.OffsetY - 480) < 1 && window.CurrentTool == ToolKind.Select && window.ReturnTool == ToolKind.Text,
            $"({movedBox.Transform.OffsetX:0.0}, {movedBox.Transform.OffsetY:0.0}), tool {window.CurrentTool}");
        await ToolClick(new(170, 500));
        window.BeginTextEdit(box.Id); await Settle(300);
        var editor = window.ActiveTextEditor;
        Check("editing starts on the selected box", editor is not null && window.CurrentTool == ToolKind.Select);
        editor!.CaretPosition = editor.Document.ContentEnd;
        editor.CaretPosition.InsertTextInRun(" 수정");
        window.FlushTextEdit();
        window.FinishTextEditing(); await Settle(300);
        var edited = (TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation;
        Check("finishing the edit keeps the text and the box selected (spec 5.4, D14g)", edited.Text == "텍스트 상자 수정" && window.CurrentTool == ToolKind.Select &&
            window.Selection.SelectedIds.SequenceEqual([box.Id]) && window.ReturnTool == ToolKind.Text, $"'{edited.Text}', tool {window.CurrentTool}");
        await ToolClick(new(300, 650));
        Check("an empty click clears it and goes back to the text tool without a new box", window.CurrentTool == ToolKind.Text && window.ActiveTextEditor is null,
            $"tool {window.CurrentTool}");
        await ToolClick(new(300, 650));
        Check("a click on empty space still creates a new box", window.ActiveTextEditor is not null && window.EditingText?.Id != box.Id && window.CurrentTool == ToolKind.Text);
        window.FinishTextEditing(); await Settle();
        window.SetTool(ToolKind.Select); await Settle();
        await ToolClick(new(170, 500));
        Check("a selection the user chose stays after its work", window.CurrentTool == ToolKind.Select && window.ReturnTool is null);

        // A new text box starts with the last box fill/border (2026-10-13 user request), also after a restart.
        var boxStyle = new TextBoxStyle(new(0xFF, 0xEB, 0x3B), .6, true, new(0xE5, 0x39, 0x35), .8);
        window.BeginNewText(0, new(320, 700)); await Settle(300);
        window.ApplyTextBoxStyle(boxStyle);
        window.ActiveTextEditor!.CaretPosition.InsertTextInRun("첫 상자"); window.FlushTextEdit();
        var first = window.EditingText!.Id;
        window.FinishTextEditing(); await Settle();
        Check("the box style is applied to the box being edited",
            Session.Current.FindAnnotation(first)?.Annotation is TextBoxAnnotation { BoxStyle: var applied } && applied == boxStyle);
        window.BeginNewText(0, new(320, 760)); await Settle(300);
        Check("the next new box starts with the last box style", window.EditingText?.BoxStyle == boxStyle, $"{window.EditingText?.BoxStyle}");
        window.ActiveTextEditor!.CaretPosition.InsertTextInRun("둘째 상자"); window.FlushTextEdit();
        var second = window.EditingText!.Id;
        window.FinishTextEditing(); await Settle();
        Check("the second box is saved with that style", Session.Current.FindAnnotation(second)?.Annotation is TextBoxAnnotation { BoxStyle: var kept } && kept == boxStyle);
        Check("the last box style is kept in the app settings", AppSettings.Load(settingsPath).TextBox.ToStyle() == boxStyle);
        Snapshot("d12-tools.png");
    }

    // ----- real input: what only OS input can drive -----

    private static async Task RealInput()
    {
        await NewWindow(s => s.AutoBackup = false);
        await Foreground();
        View.SetZoom(1); await Settle(); View.GoToPage(0); await Settle(300);
        var box = (TextBoxAnnotation)Add(Text(100, 200, "가나다 abc"));
        window.SetTool(ToolKind.Text); await Settle();

        await Click(new(110, 208));
        Check("text tool click selects the box", window.CurrentTool == ToolKind.Select && window.Selection.SelectedIds.SequenceEqual([box.Id]) && window.ActiveTextEditor is null,
            $"tool {window.CurrentTool}, selected {window.Selection.SelectedIds.Count}, editor {window.ActiveTextEditor is not null}, editing {window.EditingText?.Id == box.Id}");
        await Task.Delay(600);
        await Click(new(118, 208));
        await Settle(400);
        Check("clicking the selected box again starts editing there", window.ActiveTextEditor is not null && window.EditingText?.Id == box.Id);
        await TypeUnicode("X");
        await Key(0x1B); await Settle(400); // Esc ends editing
        var after = (TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation;
        Check("the caret was at the clicked character and Esc leaves the box selected (spec 5.4)", after.Text.Length == box.Text.Length + 1 && !after.Text.EndsWith('X') &&
            window.CurrentTool == ToolKind.Select && window.Selection.SelectedIds.SequenceEqual([box.Id]), $"'{after.Text}', tool {window.CurrentTool}");
        await Key(0x1B); await Settle(400);
        Check("a second Esc clears it and returns to the text tool", window.CurrentTool == ToolKind.Text, $"tool {window.CurrentTool}");

        await Click(new(110, 208));
        await Task.Delay(600);
        await DoubleClick(new(150, 208));
        await Settle(400);
        Check("double-clicking the selected box edits it", window.ActiveTextEditor is not null);
        await Key(0x1B); await Settle(400);
        await Key(0x1B); await Settle(400); // the first Esc leaves the box selected (spec 5.4); the second goes back to the text tool

        await Click(new(110, 208));
        Check("selected again", window.CurrentTool == ToolKind.Select && window.ActiveTextEditor is null);
        await Key(0x41); // a real 'A' key (English input mode)
        await Settle(400);
        string EditorText() => window.ActiveTextEditor is { } live ? new System.Windows.Documents.TextRange(live.Document.ContentStart, live.Document.ContentEnd).Text.TrimEnd('\r', '\n') : "";
        string editorText = EditorText();
        Check("typing with the box selected starts editing at the end", editorText.EndsWith('a') && editorText.StartsWith(after.Text, StringComparison.Ordinal), editorText);
        await Key(0x1B); await Settle(400);
        Check("Esc ends the typing; the box stays selected (spec 5.4)", window.CurrentTool == ToolKind.Select && window.ActiveTextEditor is null);
        await Key(0x1B); await Settle(400);
        Check("back to the text tool", window.CurrentTool == ToolKind.Text);

        // Korean IME: the first key typed on a selected box must compose Hangul too (ㅎ+ㅏ+ㄴ = 한), not land as a Latin letter.
        var before = ((TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation).Text;
        await Click(new(110, 208)); await Task.Delay(600);
        await Click(new(118, 208)); await Settle(400); // editing: switch the IME to Hangul inside the editor
        var imeEditor = window.ActiveTextEditor!;
        System.Windows.Input.InputMethod.SetPreferredImeState(imeEditor, System.Windows.Input.InputMethodState.On);
        System.Windows.Input.InputMethod.Current.ImeState = System.Windows.Input.InputMethodState.On;
        System.Windows.Input.InputMethod.Current.ImeConversionMode = System.Windows.Input.ImeConversionModeValues.Native;
        await Settle(300);
        var mode = System.Windows.Input.InputMethod.Current.ImeConversionMode;
        imeEditor.CaretPosition = imeEditor.Document.ContentEnd;
        await Key(0x47); await Key(0x4B); await Key(0x53); // g k s
        window.FinishTextEditing(); await Settle(400);
        string control = ((TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation).Text;
        messages.Add($"IME control (editor focused): mode {mode}, '{before}' -> '{control}'");
        bool imeWorks = control == before + "한";
        Check("the probe can drive the Korean IME in the editor", imeWorks, $"mode {mode}, '{control}'");
        window.SetTool(ToolKind.Text); await Settle();
        await Click(new(110, 208));
        Check("selected for IME typing", window.CurrentTool == ToolKind.Select && window.ActiveTextEditor is null);
        await Key(0x47); await Key(0x4B); await Key(0x53);
        string imeLive = EditorText();
        window.FinishTextEditing(); await Settle(400);
        string imeText = ((TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation).Text;
        Check("Korean typed on a selected box composes Hangul from the first key", imeText == control + "한", $"'{control}' -> '{imeText}' (live '{imeLive}')");
        // Leave the user's IME in English mode as before.
        if (window.ActiveTextEditor is null) { window.BeginTextEdit(box.Id); await Settle(300); }
        System.Windows.Input.InputMethod.Current.ImeConversionMode = System.Windows.Input.ImeConversionModeValues.Alphanumeric;
        window.FinishTextEditing(); await Settle(300);
        window.SetTool(ToolKind.Text); await Settle();

        // Fast typing in English mode: keys queued together keep their order (the first one is replayed after the editor opens).
        string beforeFast = ((TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation).Text;
        await Click(new(110, 208));
        var burst = new List<Input>();
        foreach (ushort k in new ushort[] { 0x42, 0x43, 0x44 })
        {
            burst.Add(new Input { Type = 1, Keyboard = new() { Key = k } });
            burst.Add(new Input { Type = 1, Keyboard = new() { Key = k, Flags = 2 } });
        }
        SendInput((uint)burst.Count, [.. burst], Marshal.SizeOf<Input>());
        await Settle(600);
        window.FinishTextEditing(); await Settle(300);
        string fast = ((TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation).Text;
        Check("keys typed at once on a selected box keep their order", fast == beforeFast + "bcd", $"'{beforeFast}' -> '{fast}'");
        window.SetTool(ToolKind.Text); await Settle();

        var shape = (ShapeAnnotation)Add(Rectangle(300, 300));
        window.SetShapeKind(ShapeKind.Ellipse); await Settle();
        await Click(new(350, 330));
        Check("real click on a shape selects it", window.CurrentTool == ToolKind.Select && window.Selection.SelectedIds.SequenceEqual([shape.Id]));
        await Key(0x2E); await Settle(300); // Delete
        Check("Delete removes it and goes back to the shape tool", window.CurrentTool == ToolKind.Shape && Session.Current.FindAnnotation(shape.Id) is null);

        // Settings window: the backup items are active and OK stores them.
        await Key(0xBC, control: true);
        var dialog = await Modal<PdfNotes.App.Views.SettingsDialog>();
        var categories = Field<System.Windows.Controls.ListBox>(dialog, "CategoryList");
        await ClickElement((FrameworkElement)categories.ItemContainerGenerator.ContainerFromIndex(1)); // 저장 (호환 is gone, D14g)
        var intervalBox = Field<System.Windows.Controls.ComboBox>(dialog, "BackupIntervalBox");
        Check("backup items are shown; with backup off only the switch is enabled", Field<System.Windows.Controls.CheckBox>(dialog, "AutoBackupBox").IsEnabled &&
            !intervalBox.IsEnabled && Field<System.Windows.Controls.TextBox>(dialog, "BackupFolderBox").Text.Length > 0);
        await ClickElement(Field<System.Windows.Controls.CheckBox>(dialog, "AutoBackupBox")); // on
        Check("turning backup on enables its items", intervalBox.IsEnabled);
        await ClickElement(Field<System.Windows.Controls.RadioButton>(dialog, "BackupOriginalMode"));
        answers.Enqueue(MessageBoxResult.No);
        await ClickElement(Field<System.Windows.Controls.Button>(dialog, "OkButton"));
        Check("declining the original-file confirmation keeps the window and the folder mode", dialog.IsVisible &&
            Field<System.Windows.Controls.RadioButton>(dialog, "BackupFolderMode").IsChecked == true && prompts[^1].Contains("원본 파일", StringComparison.Ordinal));
        var keep = Field<System.Windows.Controls.TextBox>(dialog, "BackupKeepBox");
        keep.Text = "3";
        await ClickElement(Field<System.Windows.Controls.Button>(dialog, "OkButton"));
        var stored = AppSettings.Load(settingsPath);
        Check("OK stores the backup settings", !dialog.IsVisible && stored.AutoBackup && stored.BackupMode == BackupMode.Folder && stored.BackupKeep == 3);

        // The toolbar button exports Flatten.
        string flat = Path.Combine(folder, "toolbar_flattened.pdf");
        window.SaveDialogOverride = _ => flat;
        window.ExportDialogOverride = _ => true; // D14g: the export window opens preset to 필기 합친 PDF, every page
        window.ShowRibbonTab(RibbonTab.File); // D13b: Flatten is on the file tab
        await Settle();
        var button = Descendants(window).OfType<System.Windows.Controls.Button>().First(b => b.Command == AppCommands.ExportFlattened);
        Check("the Flatten button is enabled", button.IsEnabled);
        await ClickElement(button);
        Check("the Flatten button exports", await Until(() => File.Exists(flat), 10000));
    }

    // ----- OS input helpers (d12 only) -----

    private static async Task Click(Point2 pagePoint)
    {
        Cursor(Dip(pagePoint)); await Task.Delay(60);
        mouse_event(0x2, 0, 0, 0, 0); await Task.Delay(40); mouse_event(0x4, 0, 0, 0, 0);
        await Settle(250);
    }

    private static async Task DoubleClick(Point2 pagePoint)
    {
        Cursor(Dip(pagePoint)); await Task.Delay(60);
        mouse_event(0x2, 0, 0, 0, 0); mouse_event(0x4, 0, 0, 0, 0); await Task.Delay(60);
        mouse_event(0x2, 0, 0, 0, 0); mouse_event(0x4, 0, 0, 0, 0);
        await Settle(300);
    }

    private static async Task ClickElement(FrameworkElement element)
    {
        var p = element.PointToScreen(new(element.ActualWidth / 2, element.ActualHeight / 2));
        SetCursorPos((int)p.X, (int)p.Y); await Task.Delay(60);
        mouse_event(0x2, 0, 0, 0, 0); await Task.Delay(40); mouse_event(0x4, 0, 0, 0, 0);
        await Settle(300);
    }

    private static void Cursor(Point dip)
    {
        if (dip.X < 0 || dip.Y < 0 || dip.X > View.ActualWidth || dip.Y > View.ActualHeight) throw new InvalidOperationException("Pointer outside the document area.");
        var p = View.PointToScreen(dip);
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    private static async Task Key(byte key, bool control = false)
    {
        if (control) keybd_event(0x11, 0, 0, 0);
        keybd_event(key, 0, 0, 0); keybd_event(key, 0, 2, 0);
        if (control) keybd_event(0x11, 0, 2, 0);
        await Settle(200);
    }

    /// <summary>Unicode characters through SendInput (not an IME composition).</summary>
    private static async Task TypeUnicode(string text)
    {
        foreach (char c in text)
        {
            var down = new Input { Type = 1, Keyboard = new() { Scan = c, Flags = 4 } };
            var up = new Input { Type = 1, Keyboard = new() { Scan = c, Flags = 4 | 2 } };
            SendInput(2, [down, up], Marshal.SizeOf<Input>());
            await Task.Delay(40);
        }
        await Settle(250);
    }

    private static async Task Foreground()
    {
        var handle = new WindowInteropHelper(window).Handle;
        for (int i = 0; i < 20 && GetForegroundWindow() != handle; i++)
        { if (i % 4 == 3) { keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); } window.Activate(); SetForegroundWindow(handle); await Task.Delay(100); }
        if (GetForegroundWindow() != handle) { window.Topmost = true; window.Activate(); SetForegroundWindow(handle); await Settle(200); window.Topmost = false; }
        Check("foreground", GetForegroundWindow() == handle, $"target={handle}, actual={GetForegroundWindow()}");
        View.Focus();
    }

    private static async Task<T> Modal<T>() where T : Window
    {
        for (int i = 0; i < 50; i++) { if (Application.Current.Windows.OfType<T>().FirstOrDefault(w => w.IsVisible) is { } found) { await Settle(300); return found; } await Task.Delay(100); }
        throw new InvalidOperationException(typeof(T).Name + " did not open.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
    }

    private static T Field<T>(Window dialog, string name) where T : FrameworkElement => Descendants(dialog).OfType<T>().Single(c => c.Name == name);

    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key; public ushort Scan; public uint Flags; public uint Time; public nint Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeyboardInput Keyboard; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
