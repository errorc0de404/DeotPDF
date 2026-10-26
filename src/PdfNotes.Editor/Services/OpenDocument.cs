using System.Collections.Immutable;
using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Rendering;
using PdfNotes.Pdf.Writing;
using PdfNotes.Editor.Localization;

namespace PdfNotes.Editor.Services;

/// <summary>
/// Everything that belongs to one open document: edit session (model + history), source PDF and renderer. The
/// platform keeps its own render cache next to it. Building a new instance never touches the current one, so a failed open keeps the current document.
/// </summary>
public sealed class OpenDocument : IDisposable
{
    private OpenDocument(DocumentSession session, PdfSourceDocument? source, string? filePath)
    {
        Session = session;
        Source = source;
        Sources = new DocumentSources(source);
        FilePath = filePath;
        if (source is not null) Baseline = new(source.FileLength, source.LastWriteTimeUtc);
        Renderer = new PageRenderer(Sources);
    }

    public DocumentSession Session { get; }
    public PdfSourceDocument? Source { get; }

    /// <summary>The opened PDF followed by pages imported from other PDFs (spec 11.9); owns <see cref="Source"/>.</summary>
    public DocumentSources Sources { get; }
    public PageRenderer Renderer { get; }

    /// <summary>Current file path; null for a new note that was never saved. Save As changes it.</summary>
    public string? FilePath { get; private set; }

    /// <summary>Length and write time of <see cref="FilePath"/> when this app last read or wrote it.</summary>
    public FileBaseline? Baseline { get; private set; }

    /// <summary>True when the file at <see cref="FilePath"/> was changed by someone else since it was opened or saved.</summary>
    public bool FileChangedExternally()
    {
        if (FilePath is null || Baseline is not { } baseline) return false;
        var info = new FileInfo(FilePath);
        return info.Exists && (info.Length != baseline.Length || info.LastWriteTimeUtc != baseline.LastWriteTimeUtc);
    }

    /// <summary>
    /// Saves an editable PDF (spec 14.1, 14.4): snapshot on the calling (UI) thread, write + verify + replace in the
    /// background, then mark the snapshot saved and adopt the path. Edits made meanwhile stay unsaved. On failure
    /// nothing changes and <see cref="SaveFailedException"/> (or the writer's exception) propagates.
    /// </summary>
    public async Task SaveAsync(string path)
    {
        Session.Execute(DocumentEdits.RemoveBlankTextBoxes(Session.Current));
        var snapshot = Session.CreateSnapshot();
        string target = Path.GetFullPath(path);
        var sourcePages = Source?.Pages;
        var (sourceBytes, model, imported) = Sources.ForSave(snapshot.Model);
        bool written = await WriteSerializedAsync(target, () => Task.Run(() =>
        {
            byte[] pdf = EditablePdfWriter.Write(sourceBytes, sourcePages, model, imported);
            SafeFileSaver.Save(target, pdf, written => EditablePdfWriter.Verify(written, model));
        }));
        if (!written) return; // a newer save of this document reached the file first: never mark an older state saved
        Session.MarkSaved(snapshot);
        FilePath = target;
        var info = new FileInfo(target);
        Baseline = new(info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>
    /// Writes an editable copy of the current state to <paramref name="path"/> (automatic backup, spec 18.2). The
    /// document's path, save point and unsaved mark do not change; blank text boxes are left out without editing the
    /// document. Returns the revision written.
    /// </summary>
    /// <summary>
    /// The crash copy (2026-10-15 user request): the current model written at once on the calling thread, without the
    /// write queue or a verification pass — the app is about to end. Throws when it cannot be written.
    /// </summary>
    public void WriteCrashCopy(string path)
    {
        var snapshot = Session.CreateSnapshot();
        var (sourceBytes, model, imported) = Sources.ForSave(snapshot.Model);
        File.WriteAllBytes(path, EditablePdfWriter.Write(sourceBytes, Source?.Pages, model, imported));
    }

    public async Task<long> WriteCopyAsync(string path)
    {
        var snapshot = Session.CreateSnapshot();
        string target = Path.GetFullPath(path);
        var sourcePages = Source?.Pages;
        var (sourceBytes, model, imported) = Sources.ForSave(snapshot.Model);
        await WriteSerializedAsync(target, () => Task.Run(() =>
        {
            byte[] pdf = EditablePdfWriter.Write(sourceBytes, sourcePages, model, imported);
            SafeFileSaver.Save(target, pdf, written => EditablePdfWriter.Verify(written, model));
        }));
        return snapshot.Revision;
    }

    /// <summary>
    /// Flatten PDF export (spec 14.5) of the current in-memory state, unsaved changes included. The document's path,
    /// save point and unsaved mark do not change. When annotations of other apps cannot be merged, <paramref name="keepForeign"/>
    /// is asked with their number before anything is written; false cancels. Returns false when cancelled.
    /// </summary>
    public async Task<bool> ExportFlattenedAsync(string path, Func<int, bool> keepForeign)
    {
        var snapshot = Session.CreateSnapshot();
        string target = Path.GetFullPath(path);
        var sourcePages = Source?.Pages;
        var (sourceBytes, model, imported) = Sources.ForSave(snapshot.Model);
        var flat = await Task.Run(() => FlattenWriter.Write(sourceBytes, sourcePages, model, imported));
        if (flat.KeptForeign > 0 && !keepForeign(flat.KeptForeign)) return false;
        if (FilePath is { } current && string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A merged export never replaces the document's own file (spec 14.5, D14g).");
        await WriteSerializedAsync(target, () => Task.Run(() =>
            SafeFileSaver.Save(target, flat.Pdf, written => FlattenWriter.Verify(written, snapshot.Model.Pages.Count))));
        return true;
    }

    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly Dictionary<string, long> lastWrite = new(StringComparer.OrdinalIgnoreCase);
    private long writeTickets;

    /// <summary>
    /// Writes of this document (save, automatic backup, Flatten) run one at a time (spec 14.4, 18.2). A request that
    /// waited while a newer request for the same file finished is skipped so an older state never replaces a newer one.
    /// Returns false when skipped.
    /// </summary>
    private async Task<bool> WriteSerializedAsync(string target, Func<Task> write)
    {
        long ticket = Interlocked.Increment(ref writeTickets);
        await writeGate.WaitAsync();
        try
        {
            if (lastWrite.TryGetValue(target, out long newer) && newer > ticket) return false;
            await write();
            lastWrite[target] = ticket;
            return true;
        }
        finally { writeGate.Release(); }
    }

    /// <summary>True while a write of this document is in progress or waiting.</summary>
    public bool IsWriting => writeGate.CurrentCount == 0;

    public string DisplayName => FilePath is null ? Strings.Get("file.newNoteName") : Path.GetFileName(FilePath);

    /// <summary>A permission-restricted original (spec 24.6) whose first save has not yet told that the restriction is dropped.</summary>
    public bool RestrictionNoticePending { get; set; }

    /// <summary>
    /// Opens and validates the PDF off the UI thread. <paramref name="prepare"/> may adjust the opened model before the
    /// session starts (the CLI gives a fresh model stable page IDs).
    /// </summary>
    public static async Task<OpenDocument> OpenPdfAsync(string path, CancellationToken cancellationToken = default,
        Func<PdfSourceDocument, DocumentModel, DocumentModel>? prepare = null)
    {
        var (source, model) = await Task.Run(() =>
        {
            var opened = PdfSourceDocument.Open(path, cancellationToken, allowPermissionRestricted: true);
            try { return (opened, prepare is null ? opened.CreateModel() : prepare(opened, opened.CreateModel())); }
            catch { opened.Dispose(); throw; }
        }, cancellationToken);
        try { return new OpenDocument(new DocumentSession(model), source, source.Path) { RestrictionNoticePending = source.PermissionRestricted }; }
        catch { source.Dispose(); throw; }
    }

    /// <summary>
    /// How many annotations of other apps the source PDFs hold that 다른 프로그램 주석 편집 can convert (spec 14.6, D14g).
    /// Converts each source once in the background; later calls are quick.
    /// </summary>
    public Task<int> ForeignCountAsync() => Task.Run(Sources.ForeignCount);

    /// <summary>
    /// 다른 프로그램 주석 편집 (spec 14.6, D14g): every page showing a source with convertible annotations of other apps gets
    /// them as app annotations and shows its source without them, as one Undo unit that marks the document unsaved. A save
    /// writes the sources without the originals (authors, comments and replies are not kept). Returns how many
    /// annotations were added (0: nothing to convert).
    /// </summary>
    public async Task<int> ConvertForeignAsync()
    {
        await ForeignCountAsync();
        var model = Session.Current;
        if (Sources.ForeignConversion(model) is not { } command || !Session.Execute(command)) return 0;
        return command.Operations.Count(o => o is InsertAnnotationOperation);
    }

    public static OpenDocument CreateNote(NewPageSpec spec) =>
        new(new DocumentSession(spec.CreateDocument()), null, null);

    /// <summary>
    /// Builds pages taken from <paramref name="file"/> (spec 11.9) numbered after this document's sources. Runs off the UI
    /// thread; changes nothing until <see cref="InsertImported"/>.
    /// </summary>
    public PageImport PrepareImport(PdfSourceDocument file, IReadOnlyList<int> pageIndices, CancellationToken cancellationToken = default) =>
        PdfPageImport.Build(file, pageIndices, Sources.NextIndex, cancellationToken);

    /// <summary>
    /// Several files for one insert (D13j): each file's pages numbered after the previous file's. Runs off the UI thread;
    /// on a failure the parts built so far are disposed and nothing changes.
    /// </summary>
    public PageImport[] PrepareImports(IReadOnlyList<(PdfSourceDocument File, int[] Pages)> files, CancellationToken cancellationToken = default) =>
        Prepare(files, Sources.NextIndex, cancellationToken);

    /// <summary>The files of a merge into a new note (<see cref="CreateMerged"/>): numbered from 0.</summary>
    public static PageImport[] PrepareForNew(IReadOnlyList<(PdfSourceDocument File, int[] Pages)> files, CancellationToken cancellationToken = default) =>
        Prepare(files, 0, cancellationToken);

    private static PageImport[] Prepare(IReadOnlyList<(PdfSourceDocument File, int[] Pages)> files, int first, CancellationToken cancellationToken)
    {
        var built = new List<PageImport>();
        try
        {
            int next = first;
            foreach (var (file, pages) in files)
            {
                var part = PdfPageImport.Build(file, pages, next, cancellationToken);
                built.Add(part);
                next += part.Pdf?.Pages.Count ?? 0;
            }
            return [.. built];
        }
        catch
        {
            foreach (var part in built) part.Pdf?.Dispose();
            throw;
        }
    }

    /// <summary>Inserts prepared pages at <paramref name="index"/> as one Undo unit and returns their page ids.</summary>
    public Guid[] InsertImported(PageImport import, int index) => InsertImported([import], index);

    /// <summary>Inserts the pages of several prepared files at <paramref name="index"/>, in order, as one Undo unit (D13j).</summary>
    public Guid[] InsertImported(IReadOnlyList<PageImport> imports, int index)
    {
        var (command, ids) = PageImports.Insert(Session.Current, index, [.. imports.Select(i => ((IReadOnlyList<Page>)i.Pages, (IEnumerable<Resource>)i.Resources))]);
        int expected = Sources.NextIndex;
        foreach (var import in imports)
        {
            if (import.Pdf is not { } pdf) continue;
            if (import.Pages.Select(p => p.Setup.Source.PdfPageIndex).OfType<int>().Min() != expected)
            {
                foreach (var other in imports) other.Pdf?.Dispose();
                throw new InvalidOperationException("The document's sources changed while importing.");
            }
            expected += pdf.Pages.Count;
        }
        foreach (var import in imports) if (import.Pdf is { } pdf) Sources.Append(pdf);
        Session.Execute(command);
        return ids;
    }

    /// <summary>
    /// "PDF 합치기" (D13j): a new unsaved note made only of the prepared pages, with nothing to undo. The parts must be
    /// numbered from 0 (prepared for an empty document).
    /// </summary>
    public static OpenDocument CreateMerged(IReadOnlyList<PageImport> imports)
    {
        // A document always has a page: build on a placeholder page, then drop it.
        var building = new DocumentSession(NewPageSpec.Default.CreateDocument());
        var placeholder = building.Current.Pages[0].Id;
        var (command, _) = PageImports.Insert(building.Current, 1, [.. imports.Select(i => ((IReadOnlyList<Page>)i.Pages, (IEnumerable<Resource>)i.Resources))]);
        building.Execute(command);
        building.Execute(DocumentEdits.DeletePages(building.Current, [placeholder]));
        var document = new OpenDocument(new DocumentSession(building.Current, unsaved: true), null, null); // nothing to undo, unsaved
        foreach (var import in imports) if (import.Pdf is { } pdf) document.Sources.Append(pdf);
        return document;
    }

    /// <summary>
    /// Page export (spec 11.10, D13j): writes <paramref name="pageIds"/> of the current state (unsaved changes included) as
    /// a PDF of their own — editable (this app's format, reopened with the same annotations) or flattened. Only the source
    /// pages they show go into the file. The document's path, save point and unsaved mark do not change. For a flattened
    /// export, foreign annotations that cannot be merged ask <paramref name="keepForeign"/> first; false cancels (returns false).
    /// </summary>
    public async Task<bool> ExportPagesAsync(string path, IReadOnlyList<Guid> pageIds, bool flatten, Func<int, bool>? keepForeign = null)
    {
        Session.Execute(DocumentEdits.RemoveBlankTextBoxes(Session.Current));
        var snapshot = Session.CreateSnapshot();
        string target = Path.GetFullPath(path);
        var (model, pdf) = await Task.Run(() => Sources.Subset(snapshot.Model, pageIds));
        try
        {
            IReadOnlyList<ImportedPdf> parts = pdf is null ? [] : [pdf];
            if (flatten)
            {
                var flat = await Task.Run(() => FlattenWriter.Write(null, null, model, parts));
                if (flat.KeptForeign > 0 && keepForeign?.Invoke(flat.KeptForeign) == false) return false;
                await WriteSerializedAsync(target, () => Task.Run(() =>
                    SafeFileSaver.Save(target, flat.Pdf, written => FlattenWriter.Verify(written, model.Pages.Count))));
            }
            else
            {
                await WriteSerializedAsync(target, () => Task.Run(() =>
                {
                    byte[] bytes = EditablePdfWriter.Write(null, null, model, parts);
                    SafeFileSaver.Save(target, bytes, written => EditablePdfWriter.Verify(written, model));
                }));
            }
            return true;
        }
        finally { pdf?.Dispose(); }
    }

    /// <summary>
    /// Pages of <paramref name="model"/> (a snapshot taken when they were copied) as an editable PDF of their own, for the
    /// page clipboard of other windows (spec 11.4, D14g). <paramref name="withNotes"/> false leaves this app's annotations
    /// out (페이지만 복사). Runs off the UI thread; the document does not change.
    /// </summary>
    public async Task<byte[]> PagesPdfAsync(DocumentModel model, IReadOnlyList<Guid> pageIds, bool withNotes)
    {
        var (subset, pdf) = await Task.Run(() => Sources.Subset(model, pageIds));
        try
        {
            if (!withNotes)
                subset = subset with { Pages = subset.Pages.Select(p => p with { Annotations = [] }).ToImmutableList(), Resources = ImmutableDictionary<Guid, Resource>.Empty };
            IReadOnlyList<ImportedPdf> parts = pdf is null ? [] : [pdf];
            return await Task.Run(() => EditablePdfWriter.Write(null, null, subset, parts));
        }
        finally { pdf?.Dispose(); }
    }

    public void Dispose()
    {
        Renderer.Dispose();
        Sources.Dispose();
    }
}

public readonly record struct FileBaseline(long Length, DateTime LastWriteTimeUtc);

/// <summary>User-facing messages for save failures (spec 14.4, 18). Never includes document content.</summary>
public static class SaveFailureMessages
{
    public static string For(Exception error) => error switch
    {
        SaveFailedException { Failure: SaveFailure.FolderMissing } => Strings.Get("saveError.folderMissing"),
        SaveFailedException { Failure: SaveFailure.AccessDenied } => Strings.Get("saveError.accessDenied"),
        SaveFailedException { Failure: SaveFailure.Locked } => Strings.Get("saveError.inUse"),
        SaveFailedException { Failure: SaveFailure.DiskFull } => Strings.Get("saveError.diskFull"),
        SaveFailedException { Failure: SaveFailure.VerificationFailed } => Strings.Get("saveError.verifyFailed"),
        _ => Strings.Get("saveError.generic")
    };
}

/// <summary>User-facing messages for open failures (spec 4.2). Never includes document content.</summary>
public static class OpenFailureMessages
{
    public static string For(PdfOpenFailure failure) => failure switch
    {
        PdfOpenFailure.NotFound => Strings.Get("openError.notFound"),
        PdfOpenFailure.AccessDenied => Strings.Get("openError.unreadable"),
        PdfOpenFailure.NotPdf => Strings.Get("openError.notPdf"),
        PdfOpenFailure.Corrupt => Strings.Get("openError.damaged"),
        PdfOpenFailure.PasswordProtected => Strings.Get("openError.password"),
        PdfOpenFailure.Encrypted => Strings.Get("openError.encrypted"),
        PdfOpenFailure.Signed => Strings.Get("openError.signed"),
        PdfOpenFailure.Unsupported => Strings.Get("openError.structure"),
        _ => Strings.Get("openError.generic")
    };
}
