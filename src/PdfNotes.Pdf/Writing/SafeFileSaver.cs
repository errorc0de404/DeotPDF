namespace PdfNotes.Pdf.Writing;

/// <summary>Why a save failed. The UI maps each reason to a message (spec 14.4, 18).</summary>
public enum SaveFailure { FolderMissing, AccessDenied, Locked, DiskFull, VerificationFailed, Unknown }

public sealed class SaveFailedException(SaveFailure failure, string message, Exception? inner = null) : IOException(message, inner)
{
    public SaveFailure Failure { get; } = failure;
}

/// <summary>
/// Safe file replacement (spec 14.4): write a complete temporary file next to the target, verify it by reading it
/// back, then replace the target in one step. Any failure leaves the target bytes untouched and removes the
/// temporary file.
/// </summary>
public static class SafeFileSaver
{
    public static void Save(string targetPath, byte[] content, Action<byte[]> verifyWritten)
    {
        string target = Path.GetFullPath(targetPath);
        // A rename would put a plain file in place of a symbolic link: replace the file the link points at instead.
        if (!OperatingSystem.IsWindows() && new FileInfo(target).LinkTarget is not null)
            target = new FileInfo(target).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? target;
        string folder = Path.GetDirectoryName(target) ?? throw new SaveFailedException(SaveFailure.FolderMissing, "Invalid target path.");
        if (!Directory.Exists(folder)) throw new SaveFailedException(SaveFailure.FolderMissing, "The target folder does not exist.");
        // macOS and Linux replace by renaming, which a read-only file does not stop (Windows refuses): refuse it first.
        if (File.Exists(target) && new FileInfo(target).IsReadOnly) throw new SaveFailedException(SaveFailure.AccessDenied, "The file is read-only.");
        // Short names: with the file's own name in them, a long Korean name went past the 255 bytes a name may have.
        string id = Guid.NewGuid().ToString("N");
        string temporary = Path.Combine(folder, $"~{id}.tmp"), backup = Path.Combine(folder, $"~{id}.bak");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            byte[] written = File.ReadAllBytes(temporary); // a read error is a write error (classified below), not a failed check
            try { verifyWritten(written); }
            catch (Exception e) when (e is not (OutOfMemoryException or SaveFailedException)) // any reader failure (PDFsharp's own too) is a failed check
            {
                throw new SaveFailedException(SaveFailure.VerificationFailed, "The written file failed verification.", e);
            }
            // The temporary file got the default mode; the saved file keeps the one it replaces (a private 0600 stays private).
            // Only a courtesy: a file system that refuses chmod (FUSE, SMB, Android shared storage) still saves.
            if (!OperatingSystem.IsWindows() && File.Exists(target))
            {
                try { File.SetUnixFileMode(temporary, File.GetUnixFileMode(target)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            if (File.Exists(target))
            {
                // With a backup name the original is never gone: without one, a replace that fails half way (network
                // shares, a file a sync tool holds) can leave neither the original nor the new file under the name.
                try { File.Replace(temporary, target, backup, ignoreMetadataErrors: true); }
                catch (IOException) when (!File.Exists(target) && File.Exists(backup))
                {
                    File.Move(backup, target); // the original back under its name; the save failed
                    throw;
                }
            }
            else File.Move(temporary, target);
        }
        catch (SaveFailedException) { throw; }
        catch (UnauthorizedAccessException e) { throw new SaveFailedException(SaveFailure.AccessDenied, "Access denied.", e); }
        catch (DirectoryNotFoundException e) { throw new SaveFailedException(SaveFailure.FolderMissing, "The target folder does not exist.", e); }
        catch (IOException e) { throw new SaveFailedException(Classify(e), "The file could not be written.", e); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            // The backup goes only while the file is there under its name.
            try { if (File.Exists(backup) && File.Exists(target)) File.Delete(backup); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static SaveFailure Classify(IOException e) => (e.HResult & 0xFFFF) switch
    {
        28 when !OperatingSystem.IsWindows() => SaveFailure.DiskFull, // ENOSPC (macOS and Linux report errno)
        32 or 33 or 1175 or 1176 => SaveFailure.Locked,          // sharing/lock violation, replace could not move files
        39 or 112 => SaveFailure.DiskFull,                       // ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL
        5 => SaveFailure.AccessDenied,
        3 => SaveFailure.FolderMissing,
        _ => SaveFailure.Unknown
    };
}
