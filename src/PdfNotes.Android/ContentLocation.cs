using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.Provider;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Writing;
using Uri = Android.Net.Uri;

namespace PdfNotes.Android;

/// <summary>
/// A document Android hands the app as a content URI (roadmap P5b): from the file picker, "open with" or a share. It is
/// read whole; saving cannot write a temporary file next to it and rename, so the old content is kept in the app's cache
/// until the new one is written, read back and verified (spec 14.4), and put back when that fails.
/// </summary>
public sealed class ContentLocation(Context context, Uri uri) : DocumentLocation
{
    private string? name;

    public Uri Uri => uri;

    public override string Name => name ??= Query(IOpenableColumns.DisplayName, c => c.GetString(0)) ?? uri.LastPathSegment ?? "document.pdf";
    public override string Key => "content:" + uri;
    public override string Address => uri.ToString()!;

    /// <summary>Only with a write grant: a PDF another app shared for viewing is saved elsewhere.</summary>
    public override bool CanReplace =>
        context.CheckUriPermission(uri, global::Android.OS.Process.MyPid(), global::Android.OS.Process.MyUid(), ActivityFlags.GrantWriteUriPermission) == Permission.Granted;

    public override byte[] ReadAll()
    {
        try
        {
            using var input = context.ContentResolver!.OpenInputStream(uri) ?? throw new FileNotFoundException(Name);
            using var copy = new MemoryStream();
            input.CopyTo(copy);
            return copy.ToArray();
        }
        catch (Java.IO.FileNotFoundException e) { throw new FileNotFoundException(Name, e); }
        catch (Java.Lang.SecurityException e) { throw new UnauthorizedAccessException(Name, e); }
    }

    public override void Replace(byte[] content, Action<byte[]> verifyWritten)
    {
        try { verifyWritten(content); } // what goes out reads back as the document (the same check a file save makes)
        catch (Exception e) { throw new SaveFailedException(SaveFailure.VerificationFailed, "The PDF to write did not verify.", e); }
        byte[]? old = null;
        try { old = ReadAll(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // a new document of the picker is empty or unreadable
        string keep = Path.Combine(context.CacheDir!.AbsolutePath, "saving", Guid.NewGuid().ToString("N") + ".pdf");
        if (old is { Length: > 0 })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
            System.IO.File.WriteAllBytes(keep, old); // the old content survives a crash in the middle of the write
        }
        try
        {
            Write(content);
            byte[] back = ReadAll();
            try { verifyWritten(back); }
            catch (Exception e)
            {
                if (old is not null) Write(old);
                throw new SaveFailedException(SaveFailure.VerificationFailed, "What was written did not verify; the old content was put back.", e);
            }
            System.IO.File.Delete(keep);
        }
        catch (Java.Lang.SecurityException e) { throw new SaveFailedException(SaveFailure.AccessDenied, "Writing is not allowed.", e); }
        catch (Java.IO.IOException e) { throw new SaveFailedException(SaveFailure.Unknown, e.Message ?? "Writing failed.", e); }
        catch (IOException e) when (e is not SaveFailedException) { throw new SaveFailedException(SaveFailure.Unknown, e.Message, e); }
    }

    /// <summary>Truncates and writes ("wt"); providers without truncation take "w".</summary>
    private void Write(byte[] content)
    {
        Stream? output;
        try { output = context.ContentResolver!.OpenOutputStream(uri, "wt"); }
        catch (Java.Lang.IllegalArgumentException) { output = context.ContentResolver!.OpenOutputStream(uri, "w"); }
        using (output ?? throw new IOException("No output stream for " + Name))
        {
            output.Write(content);
            output.Flush();
        }
    }

    public override FileBaseline? Stamp()
    {
        long? size = Query(IOpenableColumns.Size, c => c.IsNull(0) ? (long?)null : c.GetLong(0));
        long? modified = Query(DocumentsContract.Document.ColumnLastModified, c => c.IsNull(0) ? (long?)null : c.GetLong(0));
        return size is { } length ? new(length, DateTimeOffset.FromUnixTimeMilliseconds(modified ?? 0).UtcDateTime) : null;
    }

    private T? Query<T>(string column, Func<ICursor, T> read)
    {
        try
        {
            using var cursor = context.ContentResolver!.Query(uri, [column], null, null, null);
            return cursor is not null && cursor.MoveToFirst() && cursor.ColumnCount > 0 ? read(cursor) : default;
        }
        catch (Exception e) when (e is Java.Lang.Exception or ArgumentException) { return default; } // a provider without the column
    }
}

/// <summary>Recent documents on Android: content URIs the app still has a lasting grant for, or files of its own folder.</summary>
public sealed class AndroidLocations(Context context) : IDocumentLocations
{
    public DocumentLocation? Find(string address)
    {
        if (!address.StartsWith("content:", StringComparison.Ordinal)) return FileLocations.Instance.Find(address);
        var uri = Uri.Parse(address)!;
        bool granted = context.ContentResolver!.PersistedUriPermissions.Any(p => p.Uri?.Equals(uri) == true && p.IsReadPermission);
        return granted ? new ContentLocation(context, uri) : null;
    }

    public string NameOf(string address) =>
        address.StartsWith("content:", StringComparison.Ordinal) && Find(address) is { } found ? found.Name
            : Path.GetFileName(global::System.Uri.UnescapeDataString(address));

    /// <summary>Keeps the read (and write) grant of a picked document across restarts, for the recent list.</summary>
    public static void Keep(Context context, Uri uri)
    {
        try { context.ContentResolver!.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission); }
        catch (Java.Lang.SecurityException)
        {
            try { context.ContentResolver!.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission); }
            catch (Java.Lang.SecurityException) { } // a grant that cannot last (a shared file): it opens now, not from the recent list
        }
    }
}
