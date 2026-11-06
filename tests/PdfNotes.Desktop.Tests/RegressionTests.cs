using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.UI.Views;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Desktop.Tests;

/// <summary>
/// A7 regression: what the WPF Probes check through the window (d03, d04, d05, d06, d11f, d12, d13h, d13i, d14g) and the
/// earlier headless tests did not, starting with what could lose work — closing with unsaved changes, a save that cannot
/// write, files that must not open, a save while a box is typed in, the flattened export, crash backups, other apps'
/// annotations and locked annotations.
/// </summary>
public sealed class RegressionTests
{
    private static MainWindow Window(string? folder = null)
    {
        folder ??= Headless.TempFolder();
        var settings = new AppSettings { BackupFolder = Path.Combine(folder, "Backups") };
        var window = new MainWindow(TestWindows.App(settings, folder), TestWindows.Dialogs) { Width = 1200, Height = 1000 };
        window.Script.PromptOverride = (_, _) => Answer.No;
        window.Script.MessageSink = _ => { };
        window.Show();
        window.View.DocumentCanvas.SetZoom(1);
        window.View.DocumentCanvas.GoToPage(0);
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static string Sample(string folder, string name = "doc.pdf", int pages = 2)
    {
        string path = Path.Combine(folder, name);
        ProbeSamplePdf.Create(path, pages);
        return path;
    }

    private static IReadOnlyList<Annotation> Annotations(MainWindow w, int page = 0) => w.View.Document!.Session.Current.Pages[page].Annotations;

    private static Point At(MainWindow w, Point2 page)
    {
        var dip = w.View.DocumentCanvas.PageTransform(0).PageToDip.Transform(page);
        return w.View.DocumentCanvas.TranslatePoint(new Point(dip.X, dip.Y), w)!.Value;
    }

    private static void Drag(MainWindow w, Point2 a, Point2 b)
    {
        w.MouseMove(At(w, a));
        w.MouseDown(At(w, a), MouseButton.Left);
        for (int i = 1; i <= 8; i++) w.MouseMove(At(w, new(a.X + (b.X - a.X) * i / 8, a.Y + (b.Y - a.Y) * i / 8)));
        w.MouseUp(At(w, b), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(MainWindow w, Point2 p)
    {
        w.MouseDown(At(w, p), MouseButton.Left);
        w.MouseUp(At(w, p), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(MainWindow w, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        w.KeyPress(key, modifiers, PhysicalKey.None, null);
        w.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Stroke(MainWindow w, double y = 100)
    {
        w.View.SetTool(ToolKind.Pen);
        Drag(w, new(80, y), new(260, y + 30));
    }

    /// <summary>d04 A27: closing with unsaved changes asks; Cancel and a cancelled Save As keep the window, Don't save closes it.</summary>
    [Fact]
    public void ClosingWithUnsavedChangesAsksFirst() => Headless.Run(() =>
    {
        var w = Window();
        Stroke(w);
        bool closed = false;
        w.Closed += (_, _) => closed = true;
        int asked = 0;
        w.Script.PromptOverride = (_, _) => { asked++; return Answer.Cancel; };
        w.Close();
        Headless.Pump(() => asked == 1);
        Assert.False(closed);
        Assert.True(w.View.Document!.Session.IsDirty);

        // Save, but the Save As dialog of the new note is cancelled: still open, nothing lost.
        w.Script.PromptOverride = (_, _) => { asked++; return Answer.Yes; };
        w.Script.SaveDialogOverride = _ => null;
        w.Close();
        Headless.Pump(() => asked == 2);
        Assert.False(closed);

        w.Script.PromptOverride = (_, _) => Answer.No;
        w.Close();
        Headless.Pump(() => closed);
    });

    /// <summary>
    /// d04 A23, 14.4: a file that cannot be written keeps its bytes, the document stays unsaved (the title says so) and a
    /// message tells why; the next save that can write clears the mark.
    /// </summary>
    [Fact]
    public void ASaveThatCannotWriteKeepsTheFileAndTheChanges() => Headless.Run(async () =>
    {
        string folder = Headless.TempFolder();
        string pdf = Sample(folder);
        var w = Window(folder);
        Assert.True(await w.View.OpenFileAsync(pdf));
        Assert.DoesNotContain("*", w.Title);
        Stroke(w);
        Assert.Contains(" *", w.Title);
        byte[] before = File.ReadAllBytes(pdf);
        File.SetAttributes(pdf, FileAttributes.ReadOnly);
        var messages = new List<string>();
        w.Script.MessageSink = messages.Add;
        try
        {
            Assert.False(await w.View.SaveAsync(saveAs: false));
            Assert.Single(messages);
            Assert.Equal(before, File.ReadAllBytes(pdf));
            Assert.True(w.View.Document!.Session.IsDirty);
            Assert.Contains(" *", w.Title);
        }
        finally { File.SetAttributes(pdf, FileAttributes.Normal); }
        Assert.True(await w.View.SaveAsync(saveAs: false));
        Assert.DoesNotContain("*", w.Title);
        long size = new FileInfo(pdf).Length;
        Stroke(w, 300);
        Press(w, Avalonia.Input.Key.Z, RawInputModifiers.Control); // back to the saved state, then save again: the file does not grow
        Assert.True(await w.View.SaveAsync(saveAs: false));
        Assert.Equal(size, new FileInfo(pdf).Length, size / 50.0);
        w.Close();
    });

    /// <summary>d03 4.2, d13h: damaged, password, signed and missing files are refused and the open document stays; a
    /// permission-restricted one opens and its first save says once that the copy has no restriction.</summary>
    [Fact]
    public void FilesThatCannotBeEditedAreRefused() => Headless.Run(async () =>
    {
        string folder = Headless.TempFolder();
        string plain = Sample(folder, "plain.pdf", 1);
        string damaged = Path.Combine(folder, "damaged.pdf");
        File.WriteAllBytes(damaged, [.. "%PDF-1.7\n"u8.ToArray(), .. new byte[400]]);
        string password = Path.Combine(folder, "password.pdf");
        ProbeSamplePdf.CreatePasswordProtected(plain, password);
        string signed = Path.Combine(folder, "signed.pdf");
        ProbeSamplePdf.CreateSigned(plain, signed);
        var w = Window(folder);
        var messages = new List<string>();
        w.Script.MessageSink = messages.Add;
        var shown = w.View.Document;
        foreach (string path in new[] { damaged, password, signed, Path.Combine(folder, "missing.pdf") })
        {
            int count = messages.Count;
            Assert.False(await w.View.OpenFileAsync(path));
            Assert.Same(shown, w.View.Document);
            Assert.Equal(count + 1, messages.Count);
        }

        string restricted = Path.Combine(folder, "restricted.pdf");
        ProbeSamplePdf.CreateOwnerEncrypted(plain, restricted);
        messages.Clear();
        Assert.True(await w.View.OpenFileAsync(restricted));
        Assert.Contains(Strings.Get("status.restricted").Trim(), w.View.ViewModel.Status);
        Stroke(w);
        string copy = Path.Combine(folder, "copy.pdf");
        Assert.True(await w.View.SaveToAsync(copy));
        Assert.Single(messages);
        Stroke(w, 300);
        Assert.True(await w.View.SaveToAsync(copy));
        Assert.Single(messages); // once
        w.Close();
    });

    /// <summary>d06: saving while a text box is being typed in writes what was typed.</summary>
    [Fact]
    public void ASaveWhileTypingKeepsTheText() => Headless.Run(async () =>
    {
        string folder = Headless.TempFolder();
        var w = Window(folder);
        w.View.SetTool(ToolKind.Text);
        Click(w, new(100, 400));
        Assert.True(w.View.IsEditingText);
        w.KeyTextInput("저장 중 글");
        Dispatcher.UIThread.RunJobs();
        string path = Path.Combine(folder, "typed.pdf");
        Assert.True(await w.View.SaveToAsync(path));
        Assert.False(w.View.Document!.Session.IsDirty);
        w.Close();

        var again = Window(folder);
        Assert.True(await again.View.OpenFileAsync(path));
        Assert.Equal("저장 중 글", Assert.IsType<TextBoxAnnotation>(Assert.Single(Annotations(again))).Text);
        again.Close();
    });

    /// <summary>d12: the flattened export writes a plain PDF with the notes drawn in and never replaces the document itself.</summary>
    [Fact]
    public void TheFlattenedExportWritesAPlainPdf() => Headless.Run(async () =>
    {
        string folder = Headless.TempFolder();
        string pdf = Sample(folder);
        var w = Window(folder);
        Assert.True(await w.View.OpenFileAsync(pdf));
        Stroke(w);
        string flat = Path.Combine(folder, "flat.pdf");
        SavePathRequest? asked = null;
        w.Script.SaveDialogOverride = request => { asked = request; return flat; };
        w.Script.WindowOverride = _ => true;
        Assert.Equal(1, await w.View.ExportPagesAsync(allPages: true, flatten: true));
        Assert.True(File.Exists(flat));
        Assert.True(w.View.Document!.Session.IsDirty); // the export does not save the document

        var reopened = Window(folder);
        Assert.True(await reopened.View.OpenFileAsync(flat));
        Assert.Empty(Annotations(reopened)); // merged into the page: nothing to edit
        reopened.Close();

        // The document's own file is refused.
        byte[] before = File.ReadAllBytes(pdf);
        var messages = new List<string>();
        w.Script.MessageSink = messages.Add;
        w.Script.SaveDialogOverride = _ => pdf;
        Assert.NotEqual(1, await w.View.ExportPagesAsync(allPages: true, flatten: true));
        Assert.Equal(before, File.ReadAllBytes(pdf));
        w.Script.PromptOverride = (_, _) => Answer.No;
        w.Close();
    });

    /// <summary>
    /// d13i, d14g: an unexpected error writes crash_*.pdf with the error text and marks it as the window's recoverable
    /// backup, which the next start offers as a crash backup; 백업 비우기 removes the app's backups after asking.
    /// </summary>
    [Fact]
    public void ACrashBackupIsOfferedNextTimeAndBackupsCanBeCleared() => Headless.Run(async () =>
    {
        string folder = Headless.TempFolder();
        var w = Window(folder);
        Stroke(w);
        string? file = w.View.WriteCrashBackup(new InvalidOperationException("boom"));
        Assert.NotNull(file);
        Assert.StartsWith("crash_", Path.GetFileName(file));
        Assert.Contains("boom", File.ReadAllText(Path.ChangeExtension(file, ".txt")));
        // The window ends without its normal close: its marker stays and names the crash backup. Stand in for the ended
        // process by giving the marker a process number nobody uses.
        string marker = Assert.Single(Directory.GetFiles(Path.Combine(folder, "sessions"), "*.json"));
        var json = JsonNode.Parse(File.ReadAllText(marker))!;
        Assert.Equal(file, (string?)json["recoverable"]!["file"]);
        json["processId"] = DeadProcessId();
        File.WriteAllText(marker, json.ToJsonString());

        var next = Window(folder);
        string? asked = null;
        next.Script.PromptOverride = (text, _) => { asked = text; return Answer.Yes; };
        Assert.True(await next.View.OfferRecoveryAsync());
        string crashQuestion = Strings.Get("backup.recoverCrash");
        Assert.StartsWith(crashQuestion[..crashQuestion.IndexOf('{')], asked); // the crash wording, not the plain abnormal-end one
        Assert.Equal(file, next.View.Document!.FilePath);

        // 백업 비우기: No keeps them, Yes deletes the app's backups (the crash text goes with its PDF).
        await next.View.OpenFileAsync(Sample(folder)); // the crash file is not the open document any more
        Assert.Equal(1, next.View.Backups.Usage().Count);
        next.Script.PromptOverride = (_, _) => Answer.No;
        Assert.False(next.View.ClearBackups());
        Assert.True(File.Exists(file));
        next.Script.PromptOverride = (_, _) => Answer.Yes;
        Assert.True(next.View.ClearBackups());
        Assert.False(File.Exists(file));
        Assert.False(File.Exists(Path.ChangeExtension(file, ".txt")));
        next.Close();
        w.Script.PromptOverride = (_, _) => Answer.No;
        w.Close();
    });

    private static int DeadProcessId()
    {
        for (int id = 999_983; ; id -= 2)
            try { using var p = System.Diagnostics.Process.GetProcessById(id); }
            catch (ArgumentException) { return id; }
    }

    /// <summary>d11f, d14g: other apps' annotations are counted after opening and become app annotations in one Undo step.</summary>
    [Fact]
    public void OtherAppsAnnotationsAreConvertedInOneStep() => Headless.Run(async () =>
    {
        string folder = Headless.TempFolder();
        string pdf = Path.Combine(folder, "foreign.pdf");
        ForeignAnnotationSample.Create(pdf);
        var w = Window(folder);
        Assert.True(await w.View.OpenFileAsync(pdf));
        Headless.Pump(() => w.View.ForeignAnnotationCount > 0);
        int before = w.View.Document!.Session.Current.Pages.Sum(p => p.Annotations.Count);
        int converted = await w.View.ConvertForeignAsync();
        Assert.True(converted > 0);
        Assert.Equal(before + converted, w.View.Document.Session.Current.Pages.Sum(p => p.Annotations.Count));
        Assert.True(w.View.Document.Session.IsDirty);
        w.View.Undo();
        Assert.Equal(before, w.View.Document.Session.Current.Pages.Sum(p => p.Annotations.Count));
        w.Script.PromptOverride = (_, _) => Answer.No;
        w.Close();
    });

    /// <summary>d05: 잠금 from the canvas menu; Delete leaves a locked annotation, 잠금 해제 lets it go.</summary>
    [Fact]
    public void ALockedAnnotationSurvivesDelete() => Headless.Run(() =>
    {
        var w = Window();
        Stroke(w);
        var id = Assert.Single(Annotations(w)).Id;
        w.View.SetTool(ToolKind.Select);
        Click(w, new(170, 115));
        Assert.Contains(id, w.View.Selection.SelectedIds);
        var dip = w.View.DocumentCanvas.PageTransform(0).PageToDip.Transform(new Point2(170, 115));
        RunMenu(w.View.OpenAnnotationMenu(dip)!, "command.lock");
        Assert.True(Assert.Single(Annotations(w)).Locked);
        Click(w, new(170, 115));
        Press(w, Avalonia.Input.Key.Delete);
        Assert.Single(Annotations(w));
        RunMenu(w.View.OpenAnnotationMenu(dip)!, "command.unlock");
        Assert.False(Assert.Single(Annotations(w)).Locked);
        Click(w, new(170, 115));
        Press(w, Avalonia.Input.Key.Delete);
        Assert.Empty(Annotations(w));
        w.Close();
    });

    private static void RunMenu(ContextMenu menu, string key)
    {
        var item = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header! == Strings.Get(key));
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
