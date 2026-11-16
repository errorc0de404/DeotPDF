using System.IO;
using PdfNotes.Editor.Services;

namespace PdfNotes.Tests.App;

/// <summary>
/// A settings file with a value this version cannot read (a newer version's theme) keeps everything else. The whole file
/// used to fall back to the defaults, and the next save wrote those over it (review 2026-11-16).
/// </summary>
public sealed class SettingsToleranceTests
{
    [Fact]
    public void AnUnknownValueCostsOnlyThatSetting()
    {
        string path = Path.Combine(TestFolders.New(), "settings.json");
        var settings = new AppSettings { BackupKeep = 7 };
        settings.RecentFiles.Add(@"C:\lectures\one.pdf");
        settings.Save(path);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(json.ContainsKey("theme"));
        json["theme"] = "Sepia"; // a theme of some later version
        File.WriteAllText(path, json.ToJsonString());

        var loaded = AppSettings.Load(path);
        Assert.Equal(ThemeChoice.System, loaded.Theme);
        Assert.Equal(7, loaded.BackupKeep);
        Assert.Equal([@"C:\lectures\one.pdf"], loaded.RecentFiles);

        // And a save keeps what was read.
        loaded.Save(path);
        Assert.Equal([@"C:\lectures\one.pdf"], AppSettings.Load(path).RecentFiles);

        // A file that is not JSON at all still gives the defaults.
        File.WriteAllText(path, "{ not json");
        Assert.Empty(AppSettings.Load(path).RecentFiles);
    }
}
