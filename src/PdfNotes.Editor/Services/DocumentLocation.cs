using System.IO;
using PdfNotes.Pdf.Writing;

namespace PdfNotes.Editor.Services;

/// <summary>
/// Where a document lives (roadmap P4 A6): something whose bytes can be read and safely replaced. On the desktop it is a
/// file (<see cref="FileLocation"/>); Android (P5) gives a content URI instead of a path, which a location of its own
/// will wrap. <see cref="OpenDocument"/> reads, saves and checks for outside changes only through this. What needs a
/// real path — the recent list, the backup group, "already open in another window", Save As next to the file — asks
/// <see cref="FilePath"/> and does without when there is none.
/// </summary>
public abstract class DocumentLocation
{
    /// <summary>The name the user sees (title bar, backup file names), with its extension.</summary>
    public abstract string Name { get; }

    /// <summary>The local file, or null for a location that is not one.</summary>
    public virtual string? FilePath => null;

    /// <summary>Two locations with the same key are the same document.</summary>
    public abstract string Key { get; }

    /// <summary>The whole content. Throws <see cref="IOException"/>, <see cref="FileNotFoundException"/> or <see cref="UnauthorizedAccessException"/>.</summary>
    public abstract byte[] ReadAll();

    /// <summary>
    /// Replaces the content in one step after <paramref name="verifyWritten"/> accepted what was written (spec 14.4);
    /// on any failure the old content stays. Throws <see cref="SaveFailedException"/>.
    /// </summary>
    public abstract void Replace(byte[] content, Action<byte[]> verifyWritten);

    /// <summary>Length and change time now, to see changes made outside the app; null when not known or not there.</summary>
    public abstract FileBaseline? Stamp();

    public static FileLocation File(string path) => new(path);
}

/// <summary>A document in a local file: read whole, saved by <see cref="SafeFileSaver"/> (temporary file, verify, replace).</summary>
public sealed class FileLocation(string path) : DocumentLocation
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public override string Name => System.IO.Path.GetFileName(Path);
    public override string? FilePath => Path;
    public override string Key => "file:" + (FilePaths.Comparison == StringComparison.Ordinal ? Path : Path.ToUpperInvariant());
    public override byte[] ReadAll() => System.IO.File.ReadAllBytes(Path);
    public override void Replace(byte[] content, Action<byte[]> verifyWritten) => SafeFileSaver.Save(Path, content, verifyWritten);

    public override FileBaseline? Stamp()
    {
        var info = new FileInfo(Path);
        return info.Exists ? new(info.Length, info.LastWriteTimeUtc) : null;
    }
}
