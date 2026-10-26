using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PdfNotes.App.Services;

namespace PdfNotes.Tests.App;

/// <summary>D12 (spec 18.2): automatic backup timing, file names, files kept per document, recovery after an abnormal end.</summary>
public sealed class AutoBackupTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    private DateTime now = new(2026, 10, 13, 14, 5, 9);

    public AutoBackupTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void TheIntervalCountsFromTheFirstUnsavedChangeAndOnlyChangesAreBackedUp()
    {
        var schedule = new BackupSchedule();
        var interval = TimeSpan.FromMinutes(5);
        schedule.Observe(1, 1, now);
        Assert.False(schedule.Due(now.AddHours(1), interval)); // clean: nothing to back up
        schedule.Observe(2, 1, now);                           // first change
        schedule.Observe(3, 1, now.AddMinutes(4));             // later changes do not restart the count
        Assert.False(schedule.Due(now.AddMinutes(4.9), interval));
        Assert.True(schedule.Due(now.AddMinutes(5), interval));
        schedule.Done(3);
        schedule.Observe(3, 1, now.AddMinutes(20));
        Assert.False(schedule.Due(now.AddMinutes(20), interval)); // the backed-up state is not written again
        schedule.Observe(4, 1, now.AddMinutes(21));
        Assert.True(schedule.Due(now.AddMinutes(26), interval));
        schedule.Observe(4, 4, now.AddMinutes(26));                // saved meanwhile
        Assert.False(schedule.Due(now.AddMinutes(40), interval));
        schedule.Observe(5, 4, now.AddMinutes(41));
        schedule.Failed(now.AddMinutes(46));                       // a failure waits a full interval again
        Assert.False(schedule.Due(now.AddMinutes(50), interval));
        Assert.True(schedule.Due(now.AddMinutes(51), interval));
        schedule.Reset();
        schedule.Observe(1, 1, now);
        Assert.Null(schedule.PendingSince);
    }

    [Fact]
    public void FileNamesFollowTheDocumentAndTheTime()
    {
        var store = new BackupStore(folder, () => now);
        Assert.Equal(Path.Combine(folder, "강의노트_20261013-140509.pdf"), store.NewFile(folder, @"C:\docs\강의노트.pdf"));
        Assert.Equal(Path.Combine(folder, "새 노트_20261013-140509.pdf"), store.NewFile(folder, null));
        File.WriteAllText(Path.Combine(folder, "새 노트_20261013-140509.pdf"), "");
        Assert.Equal(Path.Combine(folder, "새 노트_20261013-140509_2.pdf"), store.NewFile(folder, null));
    }

    [Fact]
    public void OnlyTheNewestBackupsOfEachDocumentAreKept()
    {
        var store = new BackupStore(folder, () => now);
        string a = BackupStore.DocumentKey(@"C:\docs\a.pdf");
        string b = BackupStore.DocumentKey(@"D:\other\a.pdf"); // same name, another document
        string unrelated = Path.Combine(folder, "a_user-file.pdf"); File.WriteAllText(unrelated, "keep");
        var written = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            now = now.AddMinutes(5);
            string file = store.NewFile(folder, @"C:\docs\a.pdf"); File.WriteAllText(file, "pdf"); store.Added(a, file, keep: 3); written.Add(file);
        }
        now = now.AddMinutes(1);
        string other = store.NewFile(folder, @"D:\other\a.pdf"); File.WriteAllText(other, "pdf"); store.Added(b, other, keep: 3);
        Assert.Equal(written.Skip(2), store.Backups(a).Select(e => e.File));
        Assert.All(written.Take(2), f => Assert.False(File.Exists(f)));
        Assert.Equal([other], store.Backups(b).Select(e => e.File));
        Assert.True(File.Exists(unrelated)); // files the app did not write are never removed
        Assert.Equal(BackupStore.NewNotesKey, BackupStore.DocumentKey(null)); // every never-saved note is one group (D14g)
        Assert.Equal(BackupStore.DocumentKey(@"c:\DOCS\a.pdf"), BackupStore.DocumentKey(@"C:\docs\a.pdf"));
    }

    [Fact]
    public void NewNoteAndCrashBackupsAreKeptPerGroupAndAnOldSessionGroupJoinsTheNewNotes()
    {
        var store = new BackupStore(folder, () => now);
        // A backup of a never-saved note from before D14g, listed under its own session.
        string old = Path.Combine(folder, "새 노트_20261001-100000.pdf"); File.WriteAllText(old, "pdf");
        File.WriteAllText(Path.Combine(folder, "backups.json"), JsonSerializer.Serialize(new[] { new { document = "session:" + Guid.NewGuid().ToString("N"), file = old, created = now.AddDays(-20) } }));
        var notes = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            now = now.AddMinutes(5);
            string file = store.NewFile(folder, null); File.WriteAllText(file, "pdf"); store.Added(BackupStore.DocumentKey(null), file, keep: 2); notes.Add(file);
        }
        Assert.False(File.Exists(old)); // the old session's note counted with the new notes and was the oldest
        Assert.Equal(notes.Skip(1), store.Backups(BackupStore.NewNotesKey).Select(e => e.File));
        var crashes = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            now = now.AddMinutes(1);
            string file = Path.Combine(folder, $"crash_{i}.pdf"); File.WriteAllText(file, "pdf"); File.WriteAllText(Path.ChangeExtension(file, ".txt"), "error");
            store.Added(BackupStore.CrashKey, file, keep: 2); crashes.Add(file);
        }
        Assert.Equal(crashes.Skip(1), store.Backups(BackupStore.CrashKey).Select(e => e.File));
        Assert.False(File.Exists(Path.ChangeExtension(crashes[0], ".txt"))); // the error text went with its crash backup
        Assert.Equal(notes.Skip(1), store.Backups(BackupStore.NewNotesKey).Select(e => e.File)); // groups do not count each other
    }

    [Fact]
    public void AnotherWindowShowingAFileIsFoundByItsSessionMarker()
    {
        var other = new BackupStore(folder, () => now);
        other.SetOpenDocument(@"C:\docs\Lecture.pdf", 1234);
        other.Start();
        var me = new BackupStore(folder, () => now);
        me.SetOpenDocument(@"C:\docs\mine.pdf", 99);
        me.Start();
        Assert.Equal(1234, BackupStore.WindowShowing(folder, @"c:\DOCS\lecture.pdf", me));
        Assert.Null(BackupStore.WindowShowing(folder, @"C:\docs\mine.pdf", me)); // the asking window itself does not count
        Assert.Equal(99, BackupStore.WindowShowing(folder, @"C:\docs\mine.pdf"));
        other.End();
        Assert.Null(BackupStore.WindowShowing(folder, @"C:\docs\Lecture.pdf", me));
        me.End();
    }

    [Fact]
    public void ClearingDeletesOnlyTheBackupsTheAppMadeAndReportsTheirUsage()
    {
        var store = new BackupStore(folder, () => now);
        string mine = store.NewFile(folder, @"C:\docs\a.pdf"); File.WriteAllBytes(mine, new byte[3000]); store.Added(BackupStore.DocumentKey(@"C:\docs\a.pdf"), mine, 10);
        string crash = Path.Combine(folder, "crash_1.pdf"); File.WriteAllBytes(crash, new byte[1000]); File.WriteAllText(Path.ChangeExtension(crash, ".txt"), "e");
        store.Added(BackupStore.CrashKey, crash, 10);
        string user = Path.Combine(folder, "user.pdf"); File.WriteAllText(user, "keep");
        Assert.Equal((2, 4000L), store.Usage());
        using (new FileStream(mine, FileMode.Open, FileAccess.Read, FileShare.None)) // another window still holds it
            Assert.Equal(1, store.Clear());
        Assert.True(File.Exists(mine) && File.Exists(user));
        Assert.False(File.Exists(crash) || File.Exists(Path.ChangeExtension(crash, ".txt")));
        Assert.Equal((1, 3000L), store.Usage());
        Assert.Equal(1, store.Clear());
        Assert.Equal((0, 0L), store.Usage());
        Assert.True(File.Exists(user));
    }

    [Fact]
    public void ABackupLeftByAnEndedProcessIsOfferedOnceAndItsFileKept()
    {
        string backup = Path.Combine(folder, "lecture_20261013-140509.pdf"); File.WriteAllText(backup, "pdf");
        // A window of a process that is gone (no process has this start time) left a recoverable backup.
        Directory.CreateDirectory(Path.Combine(folder, "sessions"));
        File.WriteAllText(Path.Combine(folder, "sessions", "crashed.json"), JsonSerializer.Serialize(new
        {
            processId = Environment.ProcessId, processStart = new DateTime(2001, 1, 1),
            recoverable = new { documentName = "lecture.pdf", file = backup, created = now }
        }));
        // This process's own marker (running) and a normal end leave nothing.
        var running = new BackupStore(folder, () => now);
        running.Start();
        running.SetRecoverable(new("open.pdf", backup, now));
        var ended = new BackupStore(folder, () => now);
        ended.Start(); ended.End();

        var next = new BackupStore(folder, () => now);
        var offered = next.TakeAbandoned();
        Assert.Equal([new BackupStore.Recoverable("lecture.pdf", backup, now)], offered);
        Assert.Empty(next.TakeAbandoned()); // asked once
        Assert.True(File.Exists(backup));    // ignoring keeps the file
        running.End();
    }

    [Fact]
    public void BackupSettingsHaveTheSpecDefaultsAndRejectDamagedValues()
    {
        var settings = new AppSettings();
        Assert.True(settings.AutoBackup);
        Assert.Equal(5, settings.BackupIntervalMinutes);
        Assert.Equal(BackupMode.Folder, settings.BackupMode);
        Assert.Equal(10, settings.BackupKeep);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "Backups"), settings.EffectiveBackupFolder);

        string path = Path.Combine(folder, "settings.json");
        new AppSettings { AutoBackup = false, BackupIntervalMinutes = 15, BackupMode = BackupMode.Original, BackupFolder = folder, BackupKeep = 50 }.Save(path);
        var read = AppSettings.Load(path);
        Assert.Equal((false, 15, BackupMode.Original, folder, 50), (read.AutoBackup, read.BackupIntervalMinutes, read.BackupMode, read.EffectiveBackupFolder, read.BackupKeep));
        Assert.DoesNotContain("effectiveBackupFolder", File.ReadAllText(path));

        File.WriteAllText(path, """{ "backupIntervalMinutes": 7, "backupMode": "folder", "backupKeep": 400, "backupFolder": "relative\\dir" }""");
        read = AppSettings.Load(path);
        Assert.Equal((5, 50, (string?)null), (read.BackupIntervalMinutes, read.BackupKeep, read.BackupFolder));
    }
}
