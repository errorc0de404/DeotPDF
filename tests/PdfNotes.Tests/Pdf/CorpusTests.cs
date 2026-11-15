using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.Tests.Pdf;

/// <summary>
/// Real PDFs through the app's own path: open, add one stroke, save a copy, open the copy. The files are not in the
/// repository: set <c>DEOT_PDF_CORPUS</c> to folders (searched with their subfolders) or files, separated by <c>;</c>.
/// Without it the test passes with nothing to do.
/// </summary>
public sealed class CorpusTests
{
    private static IEnumerable<string> CorpusFiles()
    {
        if (Environment.GetEnvironmentVariable("DEOT_PDF_CORPUS") is not { Length: > 0 } value) yield break;
        foreach (string entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (File.Exists(entry)) yield return entry;
            else if (Directory.Exists(entry))
                foreach (string file in Directory.EnumerateFiles(entry, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                    yield return file;
        }
    }

    [Fact]
    public async Task EveryCorpusPdfOpensSavesAndReopens()
    {
        string folder = TestFolders.New();
        var failures = new List<string>();
        int count = 0, refused = 0;
        foreach (string file in CorpusFiles())
        {
            count++;
            string copy = Path.Combine(folder, $"{count}.pdf");
            try
            {
                OpenDocument document;
                try { document = await OpenDocument.OpenPdfAsync(file); }
                catch (PdfOpenException e) when (e.Failure is PdfOpenFailure.PasswordProtected or PdfOpenFailure.Encrypted or PdfOpenFailure.Signed or PdfOpenFailure.Unsupported)
                {
                    refused++; // a file the app says it cannot open: told to the user, not a defect
                    continue;
                }
                using (document)
                {
                    var before = document.Session.Current;
                    int foreign = await document.ForeignCountAsync();
                    var page = before.Pages[0];
                    var stroke = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(10, 10), new(60, 40)], 2, new RgbColor(255, 0, 0), 1);
                    document.Session.Execute(DocumentEdits.AddAnnotation(before, page.Id, stroke, label: "undo.pen"));
                    await document.WriteCopyAsync(copy);

                    using var reopened = await OpenDocument.OpenPdfAsync(copy);
                    var after = reopened.Session.Current;
                    if (after.Pages.Count != before.Pages.Count) failures.Add($"{file}: {before.Pages.Count} pages became {after.Pages.Count}");
                    int foreignAfter = await reopened.ForeignCountAsync();
                    if (foreignAfter != foreign) failures.Add($"{file}: {foreign} annotations of other apps became {foreignAfter}");
                    if (after.Pages[0].Annotations.Count(a => a.Id == stroke.Id) != 1) failures.Add($"{file}: the added stroke did not come back");
                    int kept = before.Pages.Sum(p => p.Annotations.Count), back = after.Pages.Sum(p => p.Annotations.Count);
                    if (back != kept + 1) failures.Add($"{file}: {kept} annotations + 1 became {back}");
                }
            }
            catch (Exception e)
            {
                failures.Add($"{file}: {e}");
            }
            finally
            {
                if (File.Exists(copy)) File.Delete(copy);
            }
        }
        Assert.True(failures.Count == 0, $"{count} files, {refused} refused:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
