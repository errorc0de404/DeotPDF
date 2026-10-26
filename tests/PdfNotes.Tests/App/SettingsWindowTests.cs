using System.IO;
using PdfNotes.App.Services;
using PdfNotes.App.Themes;
using PdfNotes.App.Tools;
using PdfNotes.Core.Models;

namespace PdfNotes.Tests.App;

/// <summary>D11: settings window values (spec 18.1) — defaults, persistence across a restart, older files.</summary>
public sealed class SettingsWindowTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));

    public SettingsWindowTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void DefaultsFollowTheSpec()
    {
        var settings = new AppSettings();
        Assert.True(settings.QuickErase);
        Assert.False(settings.PageDrag); // D14g: dragging between pages is off until turned on
        Assert.True(settings.HighlighterSnap);
        Assert.True(settings.AutoBackup);
        // D13d: follow the Windows theme, blue accent, page numbers on (spec 3.2, 4.5, 18.1).
        Assert.Equal(ThemeChoice.System, settings.Theme);
        Assert.Equal(AccentColor.Blue, settings.Accent);
        Assert.True(settings.ShowPageNumbers);
    }

    [Fact]
    public void DisplaySettingsSurviveARestart()
    {
        string path = Path.Combine(folder, "settings.json");
        new AppSettings { Theme = ThemeChoice.Dark, Accent = AccentColor.Purple, ShowPageNumbers = false }.Save(path);
        var read = AppSettings.Load(path);
        Assert.Equal(ThemeChoice.Dark, read.Theme);
        Assert.Equal(AccentColor.Purple, read.Accent);
        Assert.False(read.ShowPageNumbers);

        // Out-of-range numbers fall back to the defaults (an unknown name resets the file, like every other value).
        File.WriteAllText(path, """{ "theme": 7, "accent": 42 }""");
        read = AppSettings.Load(path);
        Assert.Equal(ThemeChoice.System, read.Theme);
        Assert.Equal(AccentColor.Blue, read.Accent);
        Assert.True(read.ShowPageNumbers);
    }

    [Fact]
    public void UserColorsCanBeRemovedButKeepTheirOrder()
    {
        RgbColor a = new(1, 2, 3), b = new(4, 5, 6), c = new(7, 8, 9);
        Assert.Equal([a, c], ColorPalette.Remove([a, b, c], b));
        Assert.Equal([a, b], ColorPalette.Remove([a, b], c)); // not a user color: unchanged
    }

    [Fact]
    public void ChangedValuesSurviveARestart()
    {
        string path = Path.Combine(folder, "settings.json");
        new AppSettings { QuickErase = false, PageDrag = true }.Save(path);
        var read = AppSettings.Load(path);
        Assert.False(read.QuickErase);
        Assert.True(read.PageDrag);
    }

    [Fact]
    public void TwoWindowsSavingKeepEachOthersChanges()
    {
        // Spec 4.6 (D14g): each window saves only what it changed; the other window's change stays in the file.
        string path = Path.Combine(folder, "settings.json");
        new AppSettings().Save(path);
        var first = AppSettings.Load(path);
        var second = AppSettings.Load(path);
        first.Theme = ThemeChoice.Dark;
        first.Save(path);
        second.QuickErase = false;
        second.Save(path);
        var read = AppSettings.Load(path);
        Assert.Equal(ThemeChoice.Dark, read.Theme);
        Assert.False(read.QuickErase);
        // The same item changed in both: the last save wins; an item a window did not change is not written back.
        second.Theme = ThemeChoice.Light;
        second.Save(path);
        Assert.Equal(ThemeChoice.Light, AppSettings.Load(path).Theme);
        first.ShowPageNumbers = false;
        first.Save(path);
        read = AppSettings.Load(path);
        Assert.Equal(ThemeChoice.Light, read.Theme); // first still holds Dark, saved before: not written again
        Assert.False(read.QuickErase);
        Assert.False(read.ShowPageNumbers);
    }

    [Fact]
    public void HighlighterSettingsOfBeforeD14gMoveToTheOneHighlighter()
    {
        // One markup style for underline and strikeout, the separate glyph highlight tool's style and the global
        // other-app setting (all before D14g): both kinds start from the markup style, the rest is dropped.
        string path = Path.Combine(folder, "settings.json");
        File.WriteAllText(path, """
            { "markup": { "color": "#112233", "widthPt": 2.5, "transparency": 10 }, "markKind": "underline",
              "textHighlight": { "color": "#00FF00", "widthPt": 9, "transparency": 30 }, "textHighlightAuto": false,
              "editForeignAnnotations": true, "recentColors": ["#010101", "#020202"] }
            """);
        var read = AppSettings.Load(path);
        Assert.Equal("#112233", read.Underline!.Color);
        Assert.Equal(2.5, read.StrikeOut!.WidthPt);
        Assert.Equal(TextMarkupKind.Underline, read.MarkKind);
        Assert.True(read.HighlighterAuto);
        Assert.Equal(2, read.GetRecentColors().Count);
        read.Save(path);
        string saved = File.ReadAllText(path);
        Assert.DoesNotContain("\"markup\"", saved);
        Assert.DoesNotContain("textHighlight", saved);
        Assert.DoesNotContain("editForeignAnnotations", saved);
    }

    [Fact]
    public void AnOlderSettingsFileGetsTheDefaults()
    {
        string path = Path.Combine(folder, "settings.json");
        File.WriteAllText(path, """{ "viewMode": "singlePage", "areaEraserPt": 30 }""");
        var read = AppSettings.Load(path);
        Assert.True(read.QuickErase);
        Assert.False(read.PageDrag);
        Assert.Equal(30, read.AreaEraserPt);
    }
}
