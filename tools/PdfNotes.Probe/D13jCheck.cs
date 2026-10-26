using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.App;
using PdfNotes.App.Themes;
using PdfNotes.App.Views;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.Probe;

/// <summary>
/// D13j <c>d13j-logic</c> (spec 11.9–11.11) without OS input: windows are not activated and stay off screen, file and
/// save dialogs are replaced, the merge and export dialogs are driven through their controls. Checks several PDFs in one
/// insert (order, ranges, position, one Undo), an insert at a page-list position (drop), 파일 › PDF 합치기 (new unsaved
/// document without Undo), page export and split with <c>;</c> (editable files reopen with their notes, Flatten, refusals,
/// overwrite question), and clearing the notes of pages (confirmation, locked notes, one Undo, menu and ribbon entries).
/// </summary>
internal static class D13jCheck
{
    private static MainWindow window = null!;
    private static string folder = "";
    private static readonly List<string> written = [];
    private static readonly List<object> checks = [];
    private static int failed;
    private static DocumentSession Session => window.Document.Session;

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
            catch (Exception e) { Console.WriteLine(e); File.WriteAllText(Path.Combine(folder, "d13j-error.txt"), e.ToString()); }
            finally
            {
                File.WriteAllText(Path.Combine(folder, "d13j-logic-result.json"), JsonSerializer.Serialize(new
                { Passed = result == 0, Input = "none (windows not activated, off screen)", Checks = checks, Files = written }, new JsonSerializerOptions { WriteIndented = true }));
                if (window is not null) { window.PromptOverride = (_, _) => MessageBoxResult.No; window.Close(); }
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

    private static Guid[] Ids => [.. Session.Current.Pages.Select(p => p.Id)];

    private static void SelectPage(int index)
    {
        var list = (ListBox)window.FindName("PageList");
        list.SelectedItems.Clear();
        list.SelectedItems.Add(window.PageThumbnails[index]);
    }

    private static string Text(PdfiumDocument d, int i) => string.Concat(d.ExtractText(i).Select(c => c.Text));

    private static async Task Steps()
    {
        string a = Path.Combine(folder, "document.pdf"), b = Path.Combine(folder, "b.pdf"), c = Path.Combine(folder, "c.pdf"), locked = Path.Combine(folder, "locked.pdf");
        ProbeSamplePdf.Create(a, 3);
        ProbeSamplePdf.Create(b, 4);
        ProbeSamplePdf.CreateMixed(c);
        ProbeSamplePdf.CreatePasswordProtected(a, locked);
        int cPages; using (var engine = new PdfiumDocument(c)) cPages = engine.PageCount;
        var messages = new List<string>();
        var prompts = new List<string>();
        MessageBoxResult answer = MessageBoxResult.No;
        window = OffScreen(new MainWindow());
        window.Width = 1200; window.Height = 860;
        window.MessageSink = messages.Add;
        window.PromptOverride = (text, _) => { prompts.Add(text); return answer; };
        window.Show();
        await Settle(300);
        if (!await window.OpenFileAsync(a)) throw new InvalidOperationException("document did not open");
        await Settle(600);
        var own = Ids;
        int undo0 = Session.UndoCount;

        // ----- several PDFs in one insert (spec 11.9, D13j) -----
        window.ImportFilesDialogOverride = () => [b, c];
        window.MergeDialogOverride = async dialog =>
        {
            OffScreen(dialog);
            dialog.Show();
            await Settle(500);
            Check("merge dialog lists the files in the chosen order, all pages", dialog.Files.Select(f => f.Name).SequenceEqual(["b.pdf", "c.pdf"]) &&
                dialog.Files.All(f => f.Range == PageRanges.All) && dialog.CanAccept && dialog.TotalText == $"파일 2개 · {4 + cPages}쪽", dialog.TotalText);
            dialog.Move(1, -1);
            Check("▲ moves a file up", dialog.Files.Select(f => f.Name).SequenceEqual(["c.pdf", "b.pdf"]));
            dialog.SetRange(0, "1-2");
            dialog.SetRange(1, "9");
            await Settle(100);
            Check("a bad range names its file and blocks 삽입", !dialog.CanAccept && dialog.ErrorText.StartsWith("2번 파일") && dialog.ErrorText.Contains("1~4") && !dialog.Accept(), dialog.ErrorText);
            dialog.SetRange(1, "2, 4");
            dialog.Position = MergePosition.Start;
            await Settle(200);
            Check("fixed range: 삽입 again, total updated", dialog.CanAccept && dialog.TotalText == "파일 2개 · 4쪽", dialog.TotalText);
            Save(Capture(dialog), "merge-dialog.png");
            dialog.Close();
            return true;
        };
        bool inserted = await window.ImportPdfPagesAsync();
        await Settle(600);
        var model = Session.Current;
        int next = own.Length; // source numbering: the document's 3 pages, then c's two, then b's two
        Check("several files inserted at the start in dialog order with their ranges", inserted && model.Pages.Count == 7 &&
            model.Pages.Skip(4).Select(p => p.Id).SequenceEqual(own) &&
            model.Pages.Take(4).Select(p => p.Setup.Source.PdfPageIndex).SequenceEqual([next, next + 1, next + 2, next + 3]),
            string.Join(",", model.Pages.Select(p => p.Setup.Source.PdfPageIndex)));
        Check("one Undo unit for all files", Session.UndoCount == undo0 + 1 && Session.UndoLabel == PageImports.Label, Session.UndoLabel ?? "");
        string mergedSave = Path.Combine(folder, "inserted.pdf");
        Check("saves with the inserted files", await window.SaveToAsync(mergedSave));
        using (var engine = new PdfiumDocument(mergedSave))
        using (var cShown = new PdfiumDocument(c))
        using (var bShown = new PdfiumDocument(b))
            Check("saved pages: c 1-2, b 2 and 4, then the document", engine.PageCount == 7 && Text(engine, 0) == Text(cShown, 0) && Text(engine, 1) == Text(cShown, 1) &&
                Text(engine, 2) == Text(bShown, 1) && Text(engine, 3) == Text(bShown, 3), engine.PageCount.ToString());
        Check("undo takes every inserted page out", Session.Undo() && Ids.SequenceEqual(own));
        Check("redo brings them back", Session.Redo() && Session.Current.Pages.Count == 7);

        // A refused file among several: reported, the others go on.
        messages.Clear();
        window.ImportFilesDialogOverride = () => [locked, b];
        window.ImportRangeOverride = _ => Task.FromResult<int[]?>([0]);
        int before = Session.Current.Pages.Count;
        bool partly = await window.ImportPdfPagesAsync();
        Check("a password PDF among the files is reported and left out", partly && messages.Count == 1 && messages[0].Contains("locked.pdf") && Session.Current.Pages.Count == before + 1,
            string.Join(" | ", messages).Replace('\n', ' '));

        // ----- insert at a page-list position (what a drop on the list does) -----
        window.ImportRangeOverride = _ => Task.FromResult<int[]?>([3]);
        int undoAt = Session.UndoCount;
        bool dropped = await window.ImportPdfPagesAsync([b], at: 1);
        Check("one PDF at a list position goes in right there", dropped && Session.Current.Pages[1].Setup.Source.PdfPageIndex is int && Session.UndoCount == undoAt + 1, Session.Current.Pages.Count.ToString());
        window.ImportRangeOverride = null;

        // ----- clearing the notes of pages (spec 11.11) -----
        var page0 = Session.Current.Pages[0];
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, page0.Id, new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(60, 200), new(200, 260)], 3, new(0xD0, 0, 0), 1)));
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, page0.Id, new InkStroke(Guid.NewGuid(), true, Affine.Identity, [new(60, 300), new(200, 360)], 3, new(0, 0, 0xD0), 1)));
        await Settle(300);
        SelectPage(0);
        await Settle(200);
        var menu = (ContextMenu)typeof(MainWindow).GetMethod("PageMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
        var headers = menu.Items.OfType<MenuItem>().Select(m => (string)m.Header).ToList();
        Check("page menu: 이 페이지의 필기 삭제 (enabled) and 페이지 내보내기…", headers.Contains("이 페이지의 필기 삭제") && headers.Contains("페이지 내보내기…") &&
            menu.Items.OfType<MenuItem>().First(m => (string)m.Header == "이 페이지의 필기 삭제").IsEnabled, string.Join(",", headers));
        window.ShowRibbonTab(RibbonTab.Page);
        await Settle(200);
        Check("page tab has 필기 삭제 and 내보내기", ((Button)window.FindName("ClearPageNotesButton")).IsVisible && ((Button)window.FindName("ExportPagesButton")).IsVisible);
        await Snapshot("page-tab.png");
        window.ShowRibbonTab(RibbonTab.File);
        await Settle(200);
        Check("file tab has PDF 합치기 and 페이지 내보내기", ((Button)window.FindName("MergePdfsButton")).IsVisible && ((Button)window.FindName("ExportPagesFileButton")).IsVisible);
        await Snapshot("file-tab.png");
        window.ShowRibbonTab(RibbonTab.Home);

        int undoClear = Session.UndoCount;
        prompts.Clear(); answer = MessageBoxResult.No;
        Check("No keeps the notes", !window.ClearSelectedPageNotes() && Session.Current.Pages[0].Annotations.Count == 2 && Session.UndoCount == undoClear &&
            prompts.Count == 1 && prompts[0] == "이 페이지의 필기 2개를 삭제할까요? (잠긴 필기 1개 포함)", prompts.FirstOrDefault() ?? "");
        answer = MessageBoxResult.Yes;
        Check("Yes deletes every note of the page, locked too, as one Undo", window.ClearSelectedPageNotes() && Session.Current.Pages[0].Annotations.Count == 0 &&
            Session.UndoCount == undoClear + 1 && Session.UndoLabel == DocumentEdits.ClearPagesLabel, Session.UndoLabel ?? "");
        Check("other pages keep their notes and the page stays", Session.Current.Pages.Count == before + 2 && Session.Current.Pages[0].Id == page0.Id);
        prompts.Clear();
        Check("a page without notes: no question, nothing done", !window.ClearSelectedPageNotes() && prompts.Count == 0 && Session.UndoCount == undoClear + 1);
        Check("undo restores the notes", Session.Undo() && Session.Current.Pages[0].Annotations.Count == 2);
        answer = MessageBoxResult.No;

        // ----- page export and split (spec 11.10) -----
        var current = Session.Current;
        bool dirty = Session.IsDirty;
        string? docPath = window.Document.FilePath;
        SelectPage(1);
        await Settle(200);
        string outBase = Path.Combine(folder, "out.pdf");
        window.SaveDialogOverride = _ => outBase;
        window.ExportDialogOverride = dialog =>
        {
            Check("export dialog starts with the selected page", dialog.RangeBox.Text == "2" && dialog.CanAccept && dialog.SummaryText == "파일 1개 · 1쪽", $"'{dialog.RangeBox.Text}' {dialog.SummaryText}");
            dialog.RangeBox.Text = "1, 3; 99";
            Check("a bad group names its file", !dialog.CanAccept && dialog.ErrorText.StartsWith("2번째 파일") && !dialog.Accept(), dialog.ErrorText);
            dialog.RangeBox.Text = "1,3; 2-4；5";
            Check("; splits into files (full-width too)", dialog.CanAccept && dialog.SummaryText == "파일 3개 · 6쪽", dialog.SummaryText);
            Save(Capture(Show(dialog)), "export-dialog.png");
            dialog.Close();
            return true;
        };
        int files = await window.ExportPagesAsync();
        string[] outs = [.. Enumerable.Range(1, 3).Select(i => Path.Combine(folder, $"out_{i}.pdf"))];
        Check("three files out_1..3", files == 3 && outs.All(File.Exists), files.ToString());
        if (outs.All(File.Exists))
        {
            using var o1 = PdfSourceDocument.Open(outs[0]);
            using var o2 = PdfSourceDocument.Open(outs[1]);
            using var o3 = PdfSourceDocument.Open(outs[2]);
            var m1 = o1.CreateModel();
            Check("file 1 = pages 1 and 3, the notes of page 1 editable again", m1.Pages.Count == 2 && m1.Pages[0].Annotations.Count == 2 && m1.Pages[0].Annotations.Any(x => x.Locked), m1.Pages.Count.ToString());
            Check("file 2 = pages 2-4, file 3 = page 5", o2.CreateModel().Pages.Count == 3 && o3.CreateModel().Pages.Count == 1);
            using var shown = new PdfiumDocument(outs[1]);
            using var bShown = new PdfiumDocument(b);
            Check("exported page shows the same content (page 2 = the page dropped in from b, its page 4)", Text(shown, 0) == Text(bShown, 3));
            Check("a file carries only its own source pages", o3.Pages.Count == 1 && o1.Pages.Count == 2, $"{o1.Pages.Count}/{o3.Pages.Count}");
        }
        Check("the open document is unchanged: pages, unsaved mark, path", Session.Current == current && Session.IsDirty == dirty && window.Document.FilePath == docPath);

        // Several files whose names exist: one question; No writes nothing.
        prompts.Clear();
        var stamp = File.GetLastWriteTimeUtc(outs[0]);
        window.ExportDialogOverride = dialog => { dialog.RangeBox.Text = "1; 2"; return true; };
        Check("existing files: asked once, No writes nothing", await window.ExportPagesAsync() == 0 && prompts.Count == 1 && prompts[0].Contains("2개") && File.GetLastWriteTimeUtc(outs[0]) == stamp,
            prompts.FirstOrDefault() ?? "");

        // One Flatten file.
        string flat = Path.Combine(folder, "flat.pdf");
        window.SaveDialogOverride = _ => flat;
        window.ExportDialogOverride = dialog => { dialog.RangeBox.Text = "1"; dialog.Flatten = true; return true; };
        Check("Flatten export of one page", await window.ExportPagesAsync() == 1 && File.Exists(flat));
        using (var engine = new PdfiumDocument(flat))
            Check("flattened file: one page without app data", engine.PageCount == 1 && !File.ReadAllText(flat).Contains("/PdfNotesOriginal"));

        // Never onto the open document's own file.
        messages.Clear();
        window.SaveDialogOverride = _ => window.Document.FilePath;
        window.ExportDialogOverride = dialog => { dialog.RangeBox.Text = "1"; return true; };
        Check("export onto the open file is refused", await window.ExportPagesAsync() == 0 && messages.Count == 1 && messages[0].Contains("열려 있는 문서"), messages.FirstOrDefault() ?? "");

        // ----- 파일 › PDF 합치기 → new document -----
        answer = MessageBoxResult.No; // do not save the current document
        window.MergeDialogOverride = async dialog =>
        {
            OffScreen(dialog);
            dialog.Show();
            await Settle(300);
            Check("new merge dialog", dialog.Title == "PDF 합치기" && dialog.Files.Count == 2);
            dialog.SetRange(0, "1");
            dialog.Close();
            return true;
        };
        bool merged = await window.MergePdfsAsync([b, c]);
        await Settle(600);
        Check("merged document: b page 1 + all of c, unsaved, nothing to undo, no path", merged && Session.Current.Pages.Count == 1 + cPages && Session.IsDirty && !Session.CanUndo &&
            window.Document.FilePath is null, $"{Session.Current.Pages.Count} pages, dirty {Session.IsDirty}, undo {Session.CanUndo}");
        string mergedFile = Path.Combine(folder, "merged.pdf");
        Check("merged document saves and reopens", await window.SaveToAsync(mergedFile) && !Session.IsDirty);
        using (var reopened = PdfSourceDocument.Open(mergedFile))
            Check("reopened merge has every page", reopened.CreateModel().Pages.Count == 1 + cPages);
        await Snapshot("merged.png");
    }

    private static Window Show(Window dialog)
    {
        OffScreen(dialog);
        dialog.Show();
        dialog.UpdateLayout();
        return dialog;
    }

    private static void Check(string name, bool passed, string detail = "")
    {
        checks.Add(new { Name = name, Passed = passed, Detail = detail });
        if (!passed) failed++;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
    }

    private static Task Settle(int ms = 250) => Task.Delay(ms);

    private static async Task Snapshot(string name)
    {
        await Settle(450);
        Save(Capture(window), name);
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
        written.Add(name);
    }
}
