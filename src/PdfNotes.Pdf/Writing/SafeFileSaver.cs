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
        string folder = Path.GetDirectoryName(target) ?? throw new SaveFailedException(SaveFailure.FolderMissing, "Invalid target path.");
        if (!Directory.Exists(folder)) throw new SaveFailedException(SaveFailure.FolderMissing, "The target folder does not exist.");
        string temporary = Path.Combine(folder, $"~{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            try { verifyWritten(File.ReadAllBytes(temporary)); }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or Engine.PdfiumOpenException)
            {
                throw new SaveFailedException(SaveFailure.VerificationFailed, "The written file failed verification.", e);
            }
            if (File.Exists(target)) File.Replace(temporary, target, null, ignoreMetadataErrors: true);
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
        }
    }

    private static SaveFailure Classify(IOException e) => (e.HResult & 0xFFFF) switch
    {
        32 or 33 or 1175 or 1176 => SaveFailure.Locked,          // sharing/lock violation, replace could not move files
        39 or 112 => SaveFailure.DiskFull,                       // ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL
        5 => SaveFailure.AccessDenied,
        3 => SaveFailure.FolderMissing,
        _ => SaveFailure.Unknown
    };
}
