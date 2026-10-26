using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PdfNotes.Editor.Services;

/// <summary>Where automatic backups go (spec 18.2): a separate backup folder (default) or the document's own file.</summary>
public enum BackupMode { Folder, Original }

/// <summary>
/// When an automatic backup is due (spec 18.2). The interval counts from the first change after the last save or
/// backup; a state equal to the saved or the backed-up revision needs nothing. A failure waits a full interval again.
/// </summary>
public sealed class BackupSchedule
{
    private long backedUp = -1;
    private DateTime? pendingSince;

    public DateTime? PendingSince => pendingSince;

    /// <summary>Called on every check and after document changes with the session's current and saved revisions.</summary>
    public void Observe(long revision, long savedRevision, DateTime now)
    {
        if (revision == savedRevision || revision == backedUp) pendingSince = null;
        else pendingSince ??= now;
    }

    public bool Due(DateTime now, TimeSpan interval) => pendingSince is { } since && now - since >= interval;

    public void Done(long revision) { backedUp = revision; pendingSince = null; }

    public void Failed(DateTime now) => pendingSince = now;

    /// <summary>Another document: nothing is pending and nothing was backed up.</summary>
    public void Reset() { backedUp = -1; pendingSince = null; }
}

/// <summary>
/// Backup files and crash recovery data (spec 18.2) in the app settings folder. The index remembers which group each
/// backup belongs to — a saved document by its full path, all never-saved notes together, all crash backups together
/// (D14g) — so the number kept per group is enforced without touching other files of the folder. Every running window
/// owns a session marker naming its newest backup that is newer than the last save, its open file and its window (so
/// another window can bring it forward instead of opening the same file twice, spec 4.6); a marker of a process that is
/// gone means it did not end normally.
/// </summary>
public sealed class BackupStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public sealed record Entry(string Document, string File, DateTime Created);
    public sealed record Recoverable(string DocumentName, string File, DateTime Created);
    private sealed record Marker(int ProcessId, DateTime ProcessStart, Recoverable? Recoverable, string? DocumentPath = null, long Window = 0);

    private Recoverable? recoverable;
    private string? documentPath;
    private long window;

    private readonly string appFolder;
    private readonly string markerPath;
    private readonly Func<DateTime> clock;

    public BackupStore(string appFolder, Func<DateTime>? clock = null)
    {
        this.appFolder = appFolder;
        this.clock = clock ?? (() => DateTime.Now);
        markerPath = Path.Combine(appFolder, "sessions", Guid.NewGuid().ToString("N") + ".json");
    }

    private string IndexPath => Path.Combine(appFolder, "backups.json");

    /// <summary>
    /// A new backup file name in <paramref name="folder"/>: "name_yyyyMMdd-HHmmss.pdf" ("새 노트" for a never-saved note),
    /// with "_2", "_3" … when a file of that second exists.
    /// </summary>
    public string NewFile(string folder, string? documentPath)
    {
        string name = documentPath is null ? "새 노트" : Path.GetFileNameWithoutExtension(documentPath); // backup file name: stored value, not UI text (D14e)
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        string stamp = clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string file = Path.Combine(folder, $"{name}_{stamp}.pdf");
        for (int n = 2; File.Exists(file); n++) file = Path.Combine(folder, $"{name}_{stamp}_{n}.pdf");
        return file;
    }

    /// <summary>The group of all never-saved notes' backups, and of all crash backups (spec 18.2, D14g).</summary>
    public const string NewNotesKey = "new", CrashKey = "crash";

    /// <summary>The group of a document's backups: its full path, or <see cref="NewNotesKey"/> for a note without a file.</summary>
    public static string DocumentKey(string? path) => path is null ? NewNotesKey : "file:" + Path.GetFullPath(path).ToUpperInvariant();

    /// <summary>Before D14g each never-saved note had its own group ("session:…"); they all count as the new notes now.</summary>
    private static string Group(string key) => key.StartsWith("session:", StringComparison.Ordinal) ? NewNotesKey : key;

    /// <summary>Records a written backup and deletes the oldest backups of its group beyond <paramref name="keep"/> (at least the newest stays).</summary>
    public void Added(string group, string file, int keep)
    {
        var entries = ReadIndex().Where(e => File.Exists(e.File) && !string.Equals(e.File, file, StringComparison.OrdinalIgnoreCase)).ToList();
        entries.Add(new(group, Path.GetFullPath(file), clock()));
        var own = entries.Where(e => Group(e.Document) == group).OrderByDescending(e => e.Created).ToList();
        foreach (var old in own.Skip(Math.Max(1, keep)))
        {
            try { Delete(old.File); entries.Remove(old); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // retried with the next backup
        }
        WriteJson(IndexPath, entries);
    }

    public IReadOnlyList<Entry> Backups(string group) => ReadIndex().Where(e => Group(e.Document) == group && File.Exists(e.File)).OrderBy(e => e.Created).ToList();

    /// <summary>The backups the app made that still exist: how many and their total size in bytes (settings 저장, D14g).</summary>
    public (int Count, long Bytes) Usage()
    {
        int count = 0; long bytes = 0;
        foreach (var e in ReadIndex())
        {
            try { var info = new FileInfo(e.File); if (!info.Exists) continue; count++; bytes += info.Length; }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return (count, bytes);
    }

    /// <summary>
    /// 백업 비우기 (spec 18.2, D14g): deletes every backup the app made (a crash backup with its error text). A file another
    /// window is writing or holding open is skipped and stays listed. Files the app did not make are never touched.
    /// Returns how many were deleted.
    /// </summary>
    public int Clear()
    {
        var kept = new List<Entry>();
        int deleted = 0;
        foreach (var e in ReadIndex())
        {
            try { if (File.Exists(e.File)) { Delete(e.File); deleted++; } }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { kept.Add(e); }
        }
        WriteJson(IndexPath, kept);
        return deleted;
    }

    /// <summary>Deletes a backup; a crash backup takes its error text (same name, .txt) along.</summary>
    private static void Delete(string file)
    {
        File.Delete(file);
        if (!Path.GetFileName(file).StartsWith("crash_", StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(Path.ChangeExtension(file, ".txt")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private List<Entry> ReadIndex()
    {
        try { return File.Exists(IndexPath) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(IndexPath), Json) ?? [] : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    // ----- crash recovery -----

    /// <summary>Marks this window as running (no recoverable backup yet).</summary>
    public void Start() => SetRecoverable(null);

    /// <summary>The newest backup newer than the last save of the open document, or null after a save or a document change.</summary>
    public void SetRecoverable(Recoverable? backup)
    {
        recoverable = backup;
        WriteMarker();
    }

    /// <summary>The file this window shows (null: a note without one) and the window's handle, for <see cref="WindowShowing"/>.</summary>
    public void SetOpenDocument(string? path, long windowHandle)
    {
        documentPath = path is null ? null : Path.GetFullPath(path);
        window = windowHandle;
        if (started) WriteMarker();
    }

    private bool started;

    private void WriteMarker()
    {
        started = true;
        using var self = Process.GetCurrentProcess();
        WriteJson(markerPath, new Marker(Environment.ProcessId, self.StartTime, recoverable, documentPath, window));
    }

    /// <summary>
    /// The window of another running PdfNotes that shows <paramref name="path"/> (spec 4.6, D14g), or null. Reads the
    /// session markers in <paramref name="appFolder"/>; <paramref name="except"/> is the asking window's own store.
    /// </summary>
    public static long? WindowShowing(string appFolder, string path, BackupStore? except = null)
    {
        string folder = Path.Combine(appFolder, "sessions");
        if (!Directory.Exists(folder)) return null;
        string wanted = Path.GetFullPath(path);
        foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (except is not null && string.Equals(file, except.markerPath, StringComparison.OrdinalIgnoreCase)) continue;
            Marker? marker;
            try { marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(file), Json); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { continue; }
            if (marker is { DocumentPath: { } shown, Window: not 0 } && string.Equals(shown, wanted, StringComparison.OrdinalIgnoreCase) && Running(marker))
                return marker.Window;
        }
        return null;
    }

    /// <summary>Normal end: the marker is removed.</summary>
    public void End()
    {
        try { File.Delete(markerPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Backups left by windows that did not end normally, newest first. Their markers are removed (asked once); the
    /// backup files stay.
    /// </summary>
    public IReadOnlyList<Recoverable> TakeAbandoned()
    {
        string folder = Path.Combine(appFolder, "sessions");
        var result = new List<Recoverable>();
        if (!Directory.Exists(folder)) return result;
        foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (string.Equals(file, markerPath, StringComparison.OrdinalIgnoreCase)) continue;
            Marker? marker;
            try { marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(file), Json); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { continue; }
            if (marker is null || Running(marker)) continue;
            if (marker.Recoverable is { } r && File.Exists(r.File)) result.Add(r);
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result.OrderByDescending(r => r.Created).ToList();
    }

    private static bool Running(Marker marker)
    {
        try
        {
            using var process = Process.GetProcessById(marker.ProcessId);
            return Math.Abs((process.StartTime - marker.ProcessStart).TotalSeconds) < 1;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static void WriteJson<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // recovery data is a convenience
    }
}
