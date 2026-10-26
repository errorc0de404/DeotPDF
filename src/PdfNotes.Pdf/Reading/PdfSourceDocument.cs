using System.Collections.Immutable;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Pdf.Reading;

/// <summary>Why a file cannot be opened. The UI maps each reason to a user message (spec 4.2).</summary>
public enum PdfOpenFailure { NotFound, AccessDenied, NotPdf, Corrupt, PasswordProtected, Encrypted, Signed, Unsupported }

public sealed class PdfOpenException(PdfOpenFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public PdfOpenFailure Failure { get; } = failure;
}

/// <summary>
/// An opened source PDF. The file is read once into memory and the file handle is closed immediately, so a
/// later save may replace the file on disk without invalidating the background renderer (D04).
/// </summary>
public sealed class PdfSourceDocument : IDisposable
{
    private readonly Lazy<ForeignVariant?> foreign;

    private PdfSourceDocument(string path, byte[] bytes, byte[] rendered, PdfiumDocument engine, PdfPageGeometry[] pages, EditablePdfReadResult appData,
        long length, DateTime lastWriteUtc, bool permissionRestricted = false, bool signed = false)
    {
        PermissionRestricted = permissionRestricted;
        Signed = signed;
        foreign = new(() => ForeignVariant.Create(bytes, rendered, pages), LazyThreadSafetyMode.ExecutionAndPublication);
        Path = path;
        Bytes = bytes;
        Engine = engine;
        Pages = pages;
        AppData = appData;
        FileLength = length;
        LastWriteTimeUtc = lastWriteUtc;
    }

    public string Path { get; }

    /// <summary>The original file content. Shared and never modified; saving starts from it.</summary>
    public byte[] Bytes { get; }

    /// <summary>Renders the background: the original PDF without the app annotations that were restored for editing.</summary>
    public PdfiumDocument Engine { get; }

    public IReadOnlyList<PdfPageGeometry> Pages { get; }

    /// <summary>Restored app data (schema v1) and how many app annotations stay read-only.</summary>
    public EditablePdfReadResult AppData { get; }

    /// <summary>File length and write time when opened, the baseline for detecting external changes before saving.</summary>
    public long FileLength { get; }
    public DateTime LastWriteTimeUtc { get; }

    /// <summary>
    /// This PDF without the other apps' annotations that can become app annotations, and those annotations (spec 14.6,
    /// D14g); null when there are none. Converted on first use (may take a while): read it off the UI thread.
    /// </summary>
    public ForeignVariant? Foreign => foreign.Value;

    /// <summary>The file is digitally signed; only importing pages opens such a file (spec 11.9: the signature does not carry over).</summary>
    public bool Signed { get; }

    /// <summary>
    /// The file opens without a password but restricts permissions (owner password, spec 24.6). It was opened from a copy
    /// without the security handler (<see cref="Bytes"/>), so a save writes no restrictions.
    /// </summary>
    public bool PermissionRestricted { get; }

    public void Dispose()
    {
        Engine.Dispose();
        if (foreign.IsValueCreated) foreign.Value?.Dispose();
    }

    /// <summary>The editable model: restored app annotations, or a fresh model when the PDF has no usable app data.</summary>
    public DocumentModel CreateModel() => AppData.Model;

    /// <summary>Largest coordinate difference (user units) accepted between the PDFsharp and PDFium page boxes.</summary>
    public const double MaxReaderBoxDifference = 0.01;

    /// <summary>
    /// Opens and validates a PDF. Throws <see cref="PdfOpenException"/> for every expected failure; nothing outside
    /// the returned object is changed, so the caller's current document stays intact on failure.
    /// </summary>
    /// <param name="allowPermissionRestricted">
    /// Spec 24.6: a PDF that opens without a password but restricts permissions is opened from a copy without its
    /// security handler (opening a document and importing pages, 11.9).
    /// </param>
    /// <param name="allowSigned">
    /// Spec 11.9 (D14g): a digitally signed PDF may be read for importing pages (<see cref="Signed"/> tells the caller to
    /// say the signature does not carry over). Opening it as the document is still refused (4.2).
    /// </param>
    public static PdfSourceDocument Open(string path, CancellationToken cancellationToken = default, bool allowPermissionRestricted = false,
        bool allowSigned = false)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        byte[] bytes;
        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
            bytes = File.ReadAllBytes(fullPath);
            info.Refresh();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PdfOpenException(PdfOpenFailure.NotFound, "File not found.", e);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            throw new PdfOpenException(PdfOpenFailure.AccessDenied, "File cannot be read.", e);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasPdfHeader(bytes)) throw new PdfOpenException(PdfOpenFailure.NotPdf, "Missing %PDF header.");

        PdfiumDocument engine;
        try { engine = new PdfiumDocument(bytes); }
        catch (PdfiumOpenException e) when (e.Error == PdfiumError.Password)
        {
            throw new PdfOpenException(PdfOpenFailure.PasswordProtected, "Password required.", e);
        }
        catch (PdfiumOpenException e) when (e.Error == PdfiumError.Security)
        {
            throw new PdfOpenException(PdfOpenFailure.Encrypted, "Unsupported security handler.", e);
        }
        catch (PdfiumOpenException e)
        {
            throw new PdfOpenException(PdfOpenFailure.Corrupt, "The renderer cannot read the file.", e);
        }

        try
        {
            // Saving rewrites the file; we must never silently strip encryption or invalidate a signature (spec 4.2).
            // Only a permission restriction (no password needed to open) may be opened, from an unsecured copy (24.6).
            bool restricted = false;
            if (engine.IsEncrypted)
            {
                if (!allowPermissionRestricted) throw new PdfOpenException(PdfOpenFailure.Encrypted, "Encrypted PDF.");
                PdfiumDocument plain;
                try
                {
                    bytes = engine.SaveCopyWithoutSecurity();
                    plain = new PdfiumDocument(bytes);
                }
                catch (Exception e) when (e is InvalidDataException or PdfiumOpenException)
                {
                    throw new PdfOpenException(PdfOpenFailure.Encrypted, "The security handler cannot be removed.", e);
                }
                engine.Dispose(); engine = plain;
                if (engine.IsEncrypted) throw new PdfOpenException(PdfOpenFailure.Encrypted, "Encrypted PDF.");
                restricted = true;
            }
            if (engine.PageCount <= 0) throw new PdfOpenException(PdfOpenFailure.Corrupt, "The PDF has no pages.");
            cancellationToken.ThrowIfCancellationRequested();

            PdfPageGeometry[] pages;
            bool signed;
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
                signed = HasSignatures(document);
                pages = PdfPageGeometryReader.Read(document);
            }
            catch (Exception e) when (e is not OperationCanceledException and not PdfOpenException)
            {
                // The writer must understand the structure, or a later save could lose content.
                throw new PdfOpenException(PdfOpenFailure.Unsupported, "The PDF structure cannot be read for editing.", e);
            }
            signed |= engine.SignatureCount > 0;
            if (signed && !allowSigned) throw new PdfOpenException(PdfOpenFailure.Signed, "Digitally signed PDF.");
            if (pages.Length != engine.PageCount)
                throw new PdfOpenException(PdfOpenFailure.Unsupported, "PDF readers disagree on the page count.");
            double difference;
            try { difference = PdfPageGeometryReader.CompareWithRenderer(engine, pages); }
            catch (InvalidDataException e) { throw new PdfOpenException(PdfOpenFailure.Unsupported, "PDF readers disagree on page geometry.", e); }
            if (difference > MaxReaderBoxDifference)
                throw new PdfOpenException(PdfOpenFailure.Unsupported, "PDF readers disagree on page geometry.");
            cancellationToken.ThrowIfCancellationRequested();
            EditablePdfReadResult appData;
            try { appData = EditablePdfReader.Read(bytes, pages); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Saving needs the same modify-mode reading; refuse now rather than fail at the first save.
                throw new PdfOpenException(PdfOpenFailure.Unsupported, "The PDF structure cannot be read for editing.", e);
            }
            byte[] rendered = bytes;
            if (appData.SourcePdf is { } original)
            {
                bytes = rendered = original;
                pages = PdfPageGeometryReader.Read(original);
                var background = new PdfiumDocument(original); engine.Dispose(); engine = background;
            }
            else if (!ReferenceEquals(appData.BackgroundPdf, bytes))
            {
                rendered = appData.BackgroundPdf;
                var background = new PdfiumDocument(appData.BackgroundPdf);
                engine.Dispose();
                engine = background;
            }
            return new(fullPath, bytes, rendered, engine, pages, appData, info.Length, info.LastWriteTimeUtc, restricted, signed);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    private static bool HasPdfHeader(byte[] bytes)
    {
        // Readers accept the header within the first 1024 bytes.
        int limit = Math.Min(bytes.Length, 1024);
        return bytes.AsSpan(0, limit).IndexOf("%PDF-"u8) >= 0;
    }

    /// <summary>AcroForm SigFlags "SignaturesExist" or any signature field with a value, or a /Perms (DocMDP) entry.</summary>
    internal static bool HasSignatures(PdfDocument document)
    {
        var catalog = document.Internals.Catalog;
        if (catalog.Elements.GetDictionary("/Perms") is { Elements.Count: > 0 }) return true;
        if (catalog.Elements.GetDictionary("/AcroForm") is not { } form) return false;
        if ((form.Elements.GetInteger("/SigFlags") & 1) != 0) return true;
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<PdfDictionary>();
        PushAll(form.Elements.GetArray("/Fields"), pending);
        while (pending.TryPop(out var field))
        {
            if (!visited.Add(field)) continue;
            if (field.Elements.GetName("/FT") == "/Sig" && field.Elements.ContainsKey("/V")) return true;
            PushAll(field.Elements.GetArray("/Kids"), pending);
        }
        return false;
    }

    private static void PushAll(PdfArray? array, Stack<PdfDictionary> target)
    {
        if (array is null) return;
        for (int i = 0; i < array.Elements.Count; i++)
            if (array.Elements.GetDictionary(i) is { } dictionary) target.Push(dictionary);
    }
}
