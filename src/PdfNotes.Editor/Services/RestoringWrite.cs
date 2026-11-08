using System.IO;
using PdfNotes.Pdf.Writing;

namespace PdfNotes.Editor.Services;

/// <summary>
/// Saving where there is no temporary file to write and rename (an Android content URI, roadmap P5b): the document is
/// overwritten in place, so its old content is kept in a file first and written back when anything on the way fails —
/// the write itself (a provider gone, a full disk), reading it back, or the check (spec 14.4).
/// </summary>
public static class RestoringWrite
{
    /// <summary>
    /// Writes <paramref name="content"/> with <paramref name="write"/>, reads it back with <paramref name="read"/> and
    /// checks it with <paramref name="verifyWritten"/>. A non-empty <paramref name="old"/> is kept at
    /// <paramref name="keepPath"/> until the new content is checked, and written back on any failure; the copy is deleted
    /// only when the document holds the old or the new content again. A check that fails throws
    /// <see cref="SaveFailedException"/> (<see cref="SaveFailure.VerificationFailed"/>); other failures are rethrown.
    /// </summary>
    public static void Run(byte[] content, byte[]? old, Action<byte[]> write, Func<byte[]> read, Action<byte[]> verifyWritten, string keepPath)
    {
        bool kept = old is { Length: > 0 };
        if (kept)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keepPath)!);
            File.WriteAllBytes(keepPath, old!); // the old content survives a crash in the middle of the write
        }
        try
        {
            write(content);
            byte[] back = read();
            try { verifyWritten(back); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                throw new SaveFailedException(SaveFailure.VerificationFailed, "What was written did not verify; the old content was put back.", e);
            }
        }
        catch (Exception) when (kept)
        {
            // In the catch block, not its filter: the failed write's stream is closed by now, so a provider that commits
            // on close cannot put the half-written content over the old one.
            if (PutBack(old!, write)) Delete(keepPath);
            throw;
        }
        if (kept) Delete(keepPath);
    }

    /// <summary>Writes the old content back; false (the kept copy stays) when that fails too.</summary>
    private static bool PutBack(byte[] old, Action<byte[]> write)
    {
        try { write(old); return true; }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
