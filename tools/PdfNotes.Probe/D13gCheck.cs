using System.IO;
using System.Windows;
using System.Windows.Controls;
using PdfNotes.App;
using PdfNotes.App.Views;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Probe;

/// <summary>
/// D13g <c>d13-import</c> (spec 11.9, A33) without OS input: windows are not activated and stay off screen, the file
/// dialog is replaced and the range dialog is driven through its controls. Checks the range dialog (range text ↔
/// thumbnail selection, errors that disable 삽입, preview), insertion after the active page in file order as one Undo
/// unit, search on imported pages, save and reopen, and that a refused or cancelled import leaves the document unchanged.
/// </summary>
internal static class D13gCheck
{
    public static async Task Run(MainWindow window, string folder, Func<string, bool, string, bool> checkWith, Func<string, Task> snapshot,
        Func<Window, string, Task> snapshotWindow, Func<Window, Window> offScreen)
    {
        void check(string name, bool passed, string detail = "") => checkWith(name, passed, detail);
        string a = Path.Combine(folder, "document.pdf"), b = Path.Combine(folder, "other.pdf"), locked = Path.Combine(folder, "locked.pdf"), junk = Path.Combine(folder, "junk.pdf");
        ProbeSamplePdf.Create(a, 3);
        ProbeSamplePdf.CreateMixed(b);
        ProbeSamplePdf.CreatePasswordProtected(a, locked);
        File.WriteAllText(junk, "not a pdf");
        var messages = new List<string>();
        window.MessageSink = messages.Add;
        if (!await window.OpenFileAsync(a)) throw new InvalidOperationException("document did not open");
        await Task.Delay(600);
        var session = window.Document.Session;
        var own = session.Current.Pages.Select(p => p.Id).ToArray();
        int undoBefore = session.UndoCount;

        // Refused files and a cancelled dialog change nothing.
        foreach (var (file, name) in new[] { (locked, "password"), (junk, "not a PDF") })
        {
            messages.Clear();
            window.ImportFileDialogOverride = () => file;
            window.ImportRangeOverride = _ => throw new InvalidOperationException("the range dialog must not open");
            bool done = await window.ImportPdfPagesAsync();
            check($"{name}: refused with a message, document unchanged", !done && messages.Count == 1 && session.Current.Pages.Select(p => p.Id).SequenceEqual(own) && session.UndoCount == undoBefore,
                messages.FirstOrDefault()?.Replace('\n', ' ') ?? "no message");
        }
        window.ImportFileDialogOverride = () => null;
        check("no file chosen: nothing happens", !await window.ImportPdfPagesAsync() && session.UndoCount == undoBefore);
        window.ImportFileDialogOverride = () => b;
        window.ImportRangeOverride = _ => Task.FromResult<int[]?>(null);
        check("dialog cancelled: nothing happens", !await window.ImportPdfPagesAsync() && session.Current.Pages.Count == 3 && session.UndoCount == undoBefore);

        // The range dialog, shown off screen without activation.
        window.ImportRangeOverride = async dialog =>
        {
            offScreen(dialog);
            dialog.Show();
            await Task.Delay(1200);
            var list = dialog.PageList;
            int pages = list.Items.Count;
            check("dialog lists every page, all chosen at first", pages == 6 && dialog.RangeBox.Text == PageRanges.All && list.SelectedItems.Count == 6 && dialog.CanInsert, $"{pages} pages, '{dialog.RangeBox.Text}'");
            var shown = list.Items.Cast<ImportThumb>().Where(t => t.Image is not null).Select(t => t.Index).ToArray();
            check("thumbnails of the visible pages are drawn", shown.Length >= 2 && shown.Contains(0), string.Join(",", shown));
            check("preview shows page 1", dialog.PreviewIndex == 0);
            await snapshotWindow(dialog, "import-dialog-all.png");

            dialog.RangeBox.Text = "2, 4-5";
            await Task.Delay(300);
            var selected = list.SelectedItems.Cast<ImportThumb>().Select(t => t.Index).Order().ToArray();
            check("range text selects the thumbnails", selected.SequenceEqual([1, 3, 4]) && dialog.CanInsert && dialog.ErrorText == "", string.Join(",", selected));
            check("preview moves into the range", dialog.PreviewIndex == 1, dialog.PreviewIndex.ToString());
            foreach (var (text, expected) in new[] { ("9", "1~6"), ("3-1", "예"), ("", "고르") })
            {
                dialog.RangeBox.Text = text;
                await Task.Delay(100);
                check($"invalid range '{text}' shows a short message and disables 삽입", !dialog.CanInsert && dialog.ErrorText.Contains(expected) && !dialog.Accept(), dialog.ErrorText);
            }
            await snapshotWindow(dialog, "import-dialog-error.png");
            list.SelectedItems.Clear();
            list.SelectedItems.Add(list.Items[1]);
            list.SelectedItems.Add(list.Items[3]);
            list.SelectedItems.Add(list.Items[4]);
            await Task.Delay(300);
            check("thumbnail selection writes the range", dialog.RangeBox.Text == "2, 4-5" && dialog.CanInsert, dialog.RangeBox.Text);
            await snapshotWindow(dialog, "import-dialog-range.png");
            bool accepted = dialog.Accept();
            dialog.Close();
            return accepted ? dialog.Result : null;
        };
        bool imported = await window.ImportPdfPagesAsync();
        await Task.Delay(800);
        var model = session.Current;
        var ids = model.Pages.Skip(1).Take(3).Select(p => p.Id).ToArray();
        check("pages inserted after the active page in file order", imported && model.Pages.Count == 6 && model.Pages[0].Id == own[0] && model.Pages.Skip(4).Select(p => p.Id).SequenceEqual(own[1..]) &&
            model.Pages.Skip(1).Take(3).Select(p => p.Setup.Source.PdfPageIndex).SequenceEqual([3, 4, 5]), string.Join(",", model.Pages.Select(p => p.Setup.Source.PdfPageIndex)));
        check("one Undo unit named for the import", session.UndoCount == undoBefore + 1 && session.UndoLabel == PageImports.Label, session.UndoLabel ?? "");
        check("imported pages are selected", window.SelectedPageIds.SequenceEqual(ids), string.Join(",", window.SelectedPageIds));
        check("document marked unsaved", session.IsDirty);
        check("page list shows them", window.PageThumbnails.Count == 6);
        await snapshot("import-main.png");

        window.OpenSearch();
        ((TextBox)window.FindName("SearchBox")).Text = "Page 4";
        await Task.Delay(900);
        check("imported text is searchable", window.SearchHits.Any(h => h.PageId == ids[1]), string.Join(",", window.SearchHits.Select(h => model.IndexOfPage(h.PageId))));
        window.CloseSearch();

        check("undo removes all imported pages", session.Undo() && session.Current.Pages.Select(p => p.Id).SequenceEqual(own));
        check("redo brings them back", session.Redo() && session.Current.Pages.Count == 6);

        string saved = Path.Combine(folder, "saved.pdf");
        check("saves", await window.SaveToAsync(saved));
        using (var engine = new PdfiumDocument(saved))
        {
            string Text(int i) => string.Concat(engine.ExtractText(i).Select(c => c.Text));
            check("saved file has the pages in order", engine.PageCount == 6 && Text(1).Contains("Page 2") && Text(2).Contains("Page 4") && Text(3).Contains("Page 5") && Text(4).Contains("Page 2"),
                string.Join(" | ", Enumerable.Range(0, engine.PageCount).Select(i => Text(i).Split('\n')[0])));
        }
        var setups = session.Current.Pages.Select(p => p.Setup).ToArray();
        if (!await window.OpenFileAsync(saved)) throw new InvalidOperationException("saved file did not open");
        await Task.Delay(600);
        check("reopened file has the same pages", window.Document.Session.Current.Pages.Select(p => p.Setup).SequenceEqual(setups) && window.Document.Sources.Imported.Count == 0);
        await snapshot("import-reopened.png");
    }
}
