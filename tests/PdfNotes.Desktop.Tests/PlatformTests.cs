using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using PdfNotes.Core.Models;
using PdfNotes.Desktop.Platform;
using PdfNotes.Desktop.Themes;
using PdfNotes.Desktop.Views;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Desktop.Tests;

/// <summary>
/// A6 platform services: the settings folder of each system, the settings and backups the 1.x WPF app wrote, the
/// automatic backup and recovery from the Avalonia window, the running window's marker and "already open in another
/// window" (spec 4.6, 18.2).
/// </summary>
public sealed class PlatformTests
{
    private static MainWindow Window(string folder, AppSettings? settings = null)
    {
        var window = new MainWindow(settings ?? new AppSettings(), _ => { }, folder) { Width = 1200, Height = 900 };
        window.PromptOverride = (_, buttons) => buttons == AskButtons.YesNo ? Answer.Yes : Answer.No;
        window.MessageSink = _ => { };
        window.EditorClipboard.InMemory = true;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static string SamplePdf(string folder, string name = "lecture.pdf", int pages = 2)
    {
        string path = Path.Combine(folder, name);
        ProbeSamplePdf.Create(path, pages);
        return path;
    }

    /// <summary>A pen stroke across the middle of the canvas (the document has unsaved changes).</summary>
    private static void AddStroke(MainWindow w)
    {
        w.SetTool(ToolKind.Pen);
        var c = w.DocumentCanvas;
        Avalonia.Point At(double dx) => c.TranslatePoint(new Avalonia.Point(c.Bounds.Width / 2 + dx, c.Bounds.Height / 2), w)!.Value;
        w.MouseMove(At(-60));
        w.MouseDown(At(-60), MouseButton.Left);
        for (int dx = -40; dx <= 60; dx += 20) w.MouseMove(At(dx));
        w.MouseUp(At(60), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.Document!.Session.IsDirty);
    }

    [Fact]
    public void EachSystemHasItsSettingsFolder()
    {
        static string? None(string _) => null;
        Assert.Equal(Path.Combine(@"C:\Users\a\AppData\Local", "PdfNotes"), AppSettings.FolderFor(SettingsPlatform.Windows, None, @"C:\Users\a", @"C:\Users\a\AppData\Local"));
        Assert.Equal(Path.Combine("/Users/a", "Library", "Application Support", "PdfNotes"), AppSettings.FolderFor(SettingsPlatform.MacOS, None, "/Users/a", "/Users/a/.local/share"));
        Assert.Equal(Path.Combine("/home/a", ".config", "PdfNotes"), AppSettings.FolderFor(SettingsPlatform.Linux, None, "/home/a", "/home/a/.local/share"));
        string config = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "xdg-config"));
        Assert.Equal(Path.Combine(config, "PdfNotes"), AppSettings.FolderFor(SettingsPlatform.Linux, v => v == "XDG_CONFIG_HOME" ? config : null, "/home/a", ""));
        // A relative XDG_CONFIG_HOME is ignored (XDG base directory rules).
        Assert.Equal(Path.Combine("/home/a", ".config", "PdfNotes"), AppSettings.FolderFor(SettingsPlatform.Linux, v => v == "XDG_CONFIG_HOME" ? "config" : null, "/home/a", ""));
        // Backups, markers and language packs live in the same folder (the tests' folder here).
        Assert.Equal(Path.Combine(AppSettings.Folder, "Backups"), AppSettings.DefaultBackupFolder);
        Assert.Equal(Path.Combine(AppSettings.Folder, "settings.json"), AppSettings.DefaultPath);
    }

    /// <summary>A settings file as DeotPDF 1.1 (WPF) writes it: the Avalonia window starts from it.</summary>
    [Fact]
    public void TheSettingsOfTheWpfAppAreRead() => Headless.Run(() =>
    {
        string folder = Headless.TempFolder();
        string recent = SamplePdf(folder);
        string file = Path.Combine(folder, "settings.json");
        File.WriteAllText(file, $$"""
            {
              "viewMode": "singlePage",
              "pagePanelVisible": false,
              "outlineVisible": false,
              "outlineHeight": 220,
              "eraser": "areaEraser",
              "areaEraserPt": 20,
              "quickErase": true,
              "theme": "system",
              "accent": "green",
              "showPageNumbers": true,
              "pageNumberSize": "large",
              "showFavorites": false,
              "language": "ko",
              "checkUpdates": false,
              "noteColor": "#FFD54F",
              "autoBackup": true,
              "backupIntervalMinutes": 3,
              "backupMode": "folder",
              "backupFolder": null,
              "backupKeep": 7,
              "recentColors": ["#123456"],
              "recentFiles": [{{JsonSerializer.Serialize(recent)}}],
              "pen": { "color": "#C62828", "widthPt": 1.5, "transparency": 0 }
            }
            """);
        var settings = AppSettings.Load(file);
        var w = Window(folder, settings);
        Assert.Equal(ViewMode.SinglePage, w.DocumentCanvas.Mode);
        Assert.False(w.FindControl<Control>("FavoritesGroup")!.IsVisible);
        Assert.False(w.PagePanelVisible);
        Assert.Equal([recent], w.ViewModel.Recent.Files);
        Assert.Equal(3, w.ViewModel.Settings.BackupIntervalMinutes);
        Assert.Equal(7, w.ViewModel.Settings.BackupKeep);
        Assert.Equal(new RgbColor(0xC6, 0x28, 0x28), w.ViewModel.Settings.Pen.ToStyle(StrokeStyle.DefaultPen, AnnotationKind.Ink).Color);
        Assert.Equal(AccentColor.Green, w.ViewModel.Settings.Accent);
        w.Close();

        // Saving keeps what this app does not know (another version's keys, spec 4.6 merge).
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["someLaterSetting"] = 42;
        File.WriteAllText(file, json.ToJsonString());
        var again = AppSettings.Load(file);
        again.BackupKeep = 9;
        again.Save(file);
        var saved = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.Equal(42, (int)saved["someLaterSetting"]!);
        Assert.Equal(9, (int)saved["backupKeep"]!);
    });

    /// <summary>
    /// The window left by a WPF DeotPDF that did not end normally (its session marker and backup index in that app's
    /// format): the Avalonia app offers its backup and opens it.
    /// </summary>
    [Fact]
    public void ABackupLeftByTheWpfAppIsOffered() => Headless.Run(async () =>
    {
        string folder = Headless.TempFolder();
        string backups = Directory.CreateDirectory(Path.Combine(folder, "Backups")).FullName;
        string backupFile = SamplePdf(backups, "lecture_20261104-101500.pdf");
        Directory.CreateDirectory(Path.Combine(folder, "sessions"));
        // Written as BackupStore 1.1 writes them (camelCase, indented).
        File.WriteAllText(Path.Combine(folder, "sessions", "0123456789abcdef0123456789abcdef.json"), $$"""
            {
              "processId": {{DeadProcessId()}},
              "processStart": "2026-11-04T10:00:00+09:00",
              "recoverable": {
                "documentName": "lecture.pdf",
                "file": {{JsonSerializer.Serialize(backupFile)}},
                "created": "2026-11-04T10:15:00+09:00"
              },
              "documentPath": {{JsonSerializer.Serialize(Path.Combine(folder, "lecture.pdf"))}},
              "window": 132456
            }
            """);
        File.WriteAllText(Path.Combine(folder, "backups.json"), $$"""
            [
              { "document": {{JsonSerializer.Serialize(BackupStore.DocumentKey(Path.Combine(folder, "lecture.pdf")))}}, "file": {{JsonSerializer.Serialize(backupFile)}}, "created": "2026-11-04T10:15:00+09:00" }
            ]
            """);
        var w = Window(folder);
        string? asked = null;
        w.PromptOverride = (text, _) => { asked = text; return Answer.Yes; };
        Assert.Equal(1, w.Backups.Usage().Count);
        Assert.True(await w.OfferRecoveryAsync());
        Assert.Contains("lecture.pdf", asked);
        Assert.Equal(backupFile, w.Document!.FilePath);
        // Asked once: the marker is gone, the backup stays.
        Assert.False(await w.OfferRecoveryAsync());
        Assert.True(File.Exists(backupFile));
        w.Close();
    });

    private static int DeadProcessId()
    {
        // A process number nobody uses now (BackupStore compares number and start time).
        for (int id = 999_983; ; id -= 2)
            try { using var p = Process.GetProcessById(id); }
            catch (ArgumentException) { return id; }
    }

    /// <summary>A shown window marks itself (its file, for "open in another window") and a normal close removes the mark.</summary>
    [Fact]
    public void TheWindowMarksItsFileAndClearsTheMarkOnClose() => Headless.Run(async () =>
    {
        string folder = Headless.TempFolder();
        string pdf = SamplePdf(folder);
        var w = Window(folder);
        Assert.True(await w.OpenFileAsync(pdf));
        var marker = Assert.Single(Directory.GetFiles(Path.Combine(folder, "sessions"), "*.json"));
        var json = JsonNode.Parse(File.ReadAllText(marker))!;
        Assert.Equal(Path.GetFullPath(pdf), (string?)json["documentPath"]);
        Assert.Equal(Environment.ProcessId, (int)json["processId"]!);
        w.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(Directory.GetFiles(Path.Combine(folder, "sessions"), "*.json"));
    });

    /// <summary>Opening a file another window shows brings that window forward and keeps this window's document (spec 4.6).</summary>
    [Fact]
    public void AFileOpenElsewhereBringsThatWindowForward() => Headless.Run(async () =>
    {
        string folder = Headless.TempFolder();
        string pdf = SamplePdf(folder);
        var other = new BackupStore(folder); // another running window (this process, so it counts as running)
        other.Start();
        other.SetOpenDocument(pdf, 4242);
        var w = Window(folder);
        long? brought = null;
        w.BringOverride = id => brought = id;
        var before = w.Document;
        await w.OpenFileAsync(pdf);
        Assert.Equal(4242, brought);
        Assert.Same(before, w.Document);
        other.End();
        brought = null;
        Assert.True(await w.OpenFileAsync(pdf)); // nobody shows it any more
        Assert.Null(brought);
        w.Close();
    });

    /// <summary>The window's own automatic backup (spec 18.2): due after the interval, waiting while a button is held.</summary>
    [Fact]
    public void TheWindowBacksUpAndWaitsForAPressedButton() => Headless.Run(() =>
    {
        string folder = Headless.TempFolder();
        var settings = new AppSettings { BackupFolder = Path.Combine(folder, "Backups") };
        var w = Window(folder, settings);
        w.BackupIntervalOverride = TimeSpan.FromHours(1);
        AddStroke(w);
        // A button held anywhere in the window (here on the status line): the backup waits.
        var status = w.FindControl<Control>("StatusText")!;
        var on = status.TranslatePoint(new Avalonia.Point(2, status.Bounds.Height / 2), w)!.Value;
        w.MouseDown(on, MouseButton.Left);
        w.BackupIntervalOverride = TimeSpan.Zero;
        Thread.Sleep(1200);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(w.LastBackupFile);
        w.MouseUp(on, MouseButton.Left);
        Headless.Pump(() => w.LastBackupFile is not null);
        Assert.StartsWith(Path.Combine(folder, "Backups"), w.LastBackupFile);
        Assert.True(File.Exists(w.LastBackupFile));
        Assert.True(w.Document!.Session.IsDirty); // folder mode leaves the document unsaved
        Assert.Equal(1, w.Backups.Usage().Count);
        w.PromptOverride = (_, _) => Answer.No; // close without saving
        w.Close();
        Dispatcher.UIThread.RunJobs();
    });

    /// <summary>
    /// "Open with" on macOS and Linux (A6): the app bundle declares PDF as a document type it can open without taking the
    /// default, the .desktop entry passes one file. On Linux the registration script installs and removes the entry.
    /// </summary>
    [Fact]
    public void TheOpenWithFilesOfMacOsAndLinux()
    {
        string packaging = Path.Combine(Headless.Repository, "packaging");
        var plist = System.Xml.Linq.XDocument.Load(Path.Combine(packaging, "macos", "Info.plist"), System.Xml.Linq.LoadOptions.None);
        var keys = plist.Root!.Element("dict")!.Elements().ToList();
        System.Xml.Linq.XElement After(IEnumerable<System.Xml.Linq.XElement> list, string key) =>
            list.SkipWhile(e => !(e.Name == "key" && e.Value == key)).Skip(1).First();
        var type = After(keys, "CFBundleDocumentTypes").Element("dict")!.Elements().ToList();
        Assert.Equal("Alternate", After(type, "LSHandlerRank").Value);
        Assert.Contains("com.adobe.pdf", After(type, "LSItemContentTypes").Elements("string").Select(e => e.Value));
        Assert.Equal("{EXECUTABLE}", After(keys, "CFBundleExecutable").Value);

        var entry = File.ReadAllLines(Path.Combine(packaging, "linux", "deotpdf.desktop"));
        Assert.Contains("MimeType=application/pdf;", entry);
        Assert.Contains("Exec=\"{EXEC}\" %f", entry);
        if (!OperatingSystem.IsLinux()) return;

        string app = Headless.TempFolder(), data = Headless.TempFolder();
        foreach (string file in Directory.GetFiles(Path.Combine(packaging, "linux"))) File.Copy(file, Path.Combine(app, Path.GetFileName(file)));
        string exe = Path.Combine(app, "DeotPDF");
        File.WriteAllText(exe, "#!/bin/sh\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string Run(string args)
        {
            var start = new ProcessStartInfo("sh", $"\"{Path.Combine(app, "register-open-with.sh")}\" {args}") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["XDG_DATA_HOME"] = data;
            using var p = Process.Start(start)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, output);
            return output;
        }
        Run("");
        string installed = Path.Combine(data, "applications", "deotpdf.desktop");
        Assert.Contains($"Exec=\"{exe}\" %f", File.ReadAllLines(installed));
        Run("--unregister");
        Assert.False(File.Exists(installed));
    }

    /// <summary>
    /// Following the system theme (A6): the System choice leaves the variant to the OS, and what the app drew itself from
    /// theme colors (the page list's frames and numbers) changes with the theme and the accent while the window is open.
    /// </summary>
    [Fact]
    public void TheWindowFollowsAThemeChangeWhileOpen() => Headless.Run(async () =>
    {
        var app = Avalonia.Application.Current!;
        string folder = Headless.TempFolder();
        string pdf = SamplePdf(folder, pages: 3);
        var w = Window(folder);
        try
        {
            Assert.True(await w.OpenFileAsync(pdf)); // the theme changes below run without waiting in between
            DesktopTheme.Use(app, ThemeChoice.System, AccentColor.Blue);
            Assert.Equal(Avalonia.Styling.ThemeVariant.Default, app.RequestedThemeVariant);
            DesktopTheme.Use(app, ThemeChoice.Light, AccentColor.Blue);
            Headless.Pump(() => Numbers(w).Count == 3);
            Color Muted(Avalonia.Styling.ThemeVariant v) => app.TryGetResource("Text.Muted", v, out var b) ? ((ISolidColorBrush)b!).Color : default;
            Assert.Equal(Muted(Avalonia.Styling.ThemeVariant.Light), ((ISolidColorBrush)Numbers(w)[1].Foreground!).Color);

            // The theme changes under the open window (as when the OS switches to dark), then the accent.
            DesktopTheme.Use(app, ThemeChoice.Dark, AccentColor.Green);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Muted(Avalonia.Styling.ThemeVariant.Dark), ((ISolidColorBrush)Numbers(w)[1].Foreground!).Color);
            var accent = ((ISolidColorBrush)app.Resources["Accent"]!).Color;
            Assert.Equal(accent, ((ISolidColorBrush)Numbers(w)[0].Foreground!).Color); // the current page
            Assert.Equal(accent, ((ISolidColorBrush)Frames(w)[0].BorderBrush!).Color);
        }
        finally
        {
            DesktopTheme.Use(app, ThemeChoice.Light, AccentColor.Blue);
            w.Close();
        }
    });

    private static List<TextBlock> Numbers(MainWindow w) =>
        [.. w.FindControl<ListBox>("PageList")!.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "Number").OrderBy(t => t.TranslatePoint(default, w)!.Value.Y)];

    private static List<Border> Frames(MainWindow w) =>
        [.. w.FindControl<ListBox>("PageList")!.GetVisualDescendants().OfType<Border>().Where(t => t.Name == "Frame").OrderBy(t => t.TranslatePoint(default, w)!.Value.Y)];

    /// <summary>macOS and Linux: a window comes forward when another DeotPDF signals its pipe.</summary>
    [Fact]
    public async Task TheActivationPipeCarriesTheRequest()
    {
        string name = "deotpdf-test-" + Guid.NewGuid().ToString("N")[..12];
        using var stop = new CancellationTokenSource();
        var activated = new TaskCompletionSource();
        var listening = WindowActivation.Listen(name, () => activated.TrySetResult(), stop.Token);
        bool sent = false;
        for (int i = 0; i < 50 && !sent; i++) { sent = WindowActivation.Signal(name); if (!sent) await Task.Delay(50); }
        Assert.True(sent);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await listening.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(WindowActivation.Signal(name)); // nobody listens any more
    }
}
