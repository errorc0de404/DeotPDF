using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfNotes.Tests.Core;
using PdfNotes.Editor.Localization;

namespace PdfNotes.Tests.App;

/// <summary>
/// P4 A6 document storage: <see cref="OpenDocument"/> reads, saves and notices outside changes through a
/// <see cref="DocumentLocation"/>, so a location that is not a file (Android's content URI in P5) works the same way.
/// </summary>
public sealed class DocumentLocationTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));

    public DocumentLocationTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    /// <summary>A document kept in memory, as a content provider would hand it over: bytes and a change counter, no path.</summary>
    private sealed class MemoryLocation(string name, byte[] content) : DocumentLocation
    {
        public byte[] Content = content;
        public int Version;
        public int Replaced;
        public bool FailNextWrite;

        public override string Name => name;
        public override string Key => "memory:" + name;
        public override byte[] ReadAll() => [.. Content];

        public override void Replace(byte[] content, Action<byte[]> verifyWritten)
        {
            if (FailNextWrite) { FailNextWrite = false; throw new SaveFailedException(SaveFailure.AccessDenied, "No write permission."); }
            try { verifyWritten([.. content]); }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException) { throw new SaveFailedException(SaveFailure.VerificationFailed, "Bad.", e); }
            Content = [.. content];
            Version++;
            Replaced++;
        }

        public override FileBaseline? Stamp() => new(Content.LongLength, new DateTime(2026, 1, 1).AddSeconds(Version));

        public void ChangedOutside() { Content = [.. Content, .. "\n%"u8.ToArray()]; Version++; }
    }

    private byte[] SampleBytes(int pages = 2)
    {
        string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".pdf");
        ProbeSamplePdf.Create(path, pages);
        return File.ReadAllBytes(path);
    }

    private static void AddStroke(OpenDocument doc)
    {
        var page = doc.Session.Current.Pages[0];
        var stroke = TestModels.Ink(100);
        Assert.True(doc.Session.Execute(new EditCommand("undo.pen", [new InsertAnnotationOperation(page.Id, page.Annotations.Count, stroke)])));
    }

    [Fact]
    public async Task ADocumentThatIsNotAFileOpensSavesAndReopens()
    {
        var location = new MemoryLocation("lecture.pdf", SampleBytes());
        using (var doc = await OpenDocument.OpenPdfAsync(location))
        {
            Assert.Null(doc.FilePath);
            Assert.Same(location, doc.Location);
            Assert.Equal("lecture.pdf", doc.DisplayNameIn(Texts.Korean));
            Assert.Equal(2, doc.Session.Current.Pages.Count);
            AddStroke(doc);
            await doc.SaveAsync(location);
            Assert.False(doc.Session.IsDirty);
            Assert.Equal(1, location.Replaced);
            Assert.False(doc.FileChangedExternally());
        }
        using var again = await OpenDocument.OpenPdfAsync(location);
        Assert.Single(again.Session.Current.Pages[0].Annotations.OfType<InkStroke>());
    }

    [Fact]
    public async Task AChangeMadeOutsideIsNoticedAndAFailedWriteKeepsTheContent()
    {
        var location = new MemoryLocation("notes.pdf", SampleBytes());
        using var doc = await OpenDocument.OpenPdfAsync(location);
        Assert.False(doc.FileChangedExternally());
        location.ChangedOutside();
        Assert.True(doc.FileChangedExternally());

        byte[] before = location.Content;
        AddStroke(doc);
        location.FailNextWrite = true;
        var error = await Assert.ThrowsAsync<SaveFailedException>(() => doc.SaveAsync(location));
        Assert.Equal(SaveFailure.AccessDenied, error.Failure);
        Assert.Equal(before, location.Content);
        Assert.True(doc.Session.IsDirty);
    }

    [Fact]
    public async Task AFlattenedExportNeverReplacesTheDocumentItself()
    {
        var location = new MemoryLocation("same.pdf", SampleBytes());
        using var doc = await OpenDocument.OpenPdfAsync(location);
        await Assert.ThrowsAsync<InvalidOperationException>(() => doc.ExportFlattenedAsync(new MemoryLocation("same.pdf", []), _ => true));
        var other = new MemoryLocation("flat.pdf", []);
        Assert.True(await doc.ExportFlattenedAsync(other, _ => true));
        Assert.Equal(1, other.Replaced);
    }

    [Fact]
    public async Task ReadErrorsAreReportedAsForAFile()
    {
        var missing = new MissingLocation();
        var error = await Assert.ThrowsAsync<PdfOpenException>(() => OpenDocument.OpenPdfAsync(missing));
        Assert.Equal(PdfOpenFailure.NotFound, error.Failure);
        var notPdf = new MemoryLocation("text.pdf", "hello"u8.ToArray());
        Assert.Equal(PdfOpenFailure.NotPdf, (await Assert.ThrowsAsync<PdfOpenException>(() => OpenDocument.OpenPdfAsync(notPdf))).Failure);
    }

    private sealed class MissingLocation : DocumentLocation
    {
        public override string Name => "gone.pdf";
        public override string Key => "memory:gone";
        public override byte[] ReadAll() => throw new FileNotFoundException();
        public override void Replace(byte[] content, Action<byte[]> verifyWritten) => throw new NotSupportedException();
        public override FileBaseline? Stamp() => null;
    }

    /// <summary>A file keeps its 1.x behavior: the path, the name and the key that ignores case where the system does.</summary>
    [Fact]
    public async Task AFileLocationIsTheFile()
    {
        string path = Path.Combine(folder, "Doc.pdf");
        File.WriteAllBytes(path, SampleBytes(1));
        using var doc = await OpenDocument.OpenPdfAsync(path);
        var file = Assert.IsType<FileLocation>(doc.Location);
        Assert.Equal(path, doc.FilePath);
        Assert.Equal("Doc.pdf", doc.DisplayNameIn(Texts.Korean));
        Assert.Equal(OperatingSystem.IsLinux(), DocumentLocation.File(Path.Combine(folder, "doc.pdf")).Key != file.Key);
        File.AppendAllText(path, "\n%");
        Assert.True(doc.FileChangedExternally());
    }
}
