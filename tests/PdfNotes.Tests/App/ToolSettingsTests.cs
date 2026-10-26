using System.IO;
using PdfNotes.App.Services;
using PdfNotes.App.Tools;
using PdfNotes.Core.Models;

namespace PdfNotes.Tests.App;

/// <summary>D04: pen/highlighter defaults, ranges and persisted recent tool settings (spec 7.2, 9.2, 18).</summary>
public sealed class ToolSettingsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));

    public ToolSettingsTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void DefaultsAndRangesFollowTheSpec()
    {
        Assert.Equal(new StrokeStyle(RgbColor.Black, 2, 0), StrokeStyle.DefaultPen);
        Assert.Equal(1, StrokeStyle.DefaultPen.Opacity);
        Assert.Equal(14, StrokeStyle.DefaultHighlighter.WidthPt);
        Assert.Equal(0.4, StrokeStyle.DefaultHighlighter.Opacity, 12);
        Assert.Equal((0.5, 40), StrokeStyle.WidthRange(AnnotationKind.Ink));
        Assert.Equal((2, 80), StrokeStyle.WidthRange(AnnotationKind.FreeHighlight));
        Assert.Equal(40, (StrokeStyle.DefaultPen with { WidthPt = 99 }).ClampFor(AnnotationKind.Ink).WidthPt);
        Assert.Equal(2, (StrokeStyle.DefaultPen with { WidthPt = 0.5 }).ClampFor(AnnotationKind.FreeHighlight).WidthPt);
        Assert.Equal(100, (StrokeStyle.DefaultPen with { TransparencyPercent = 150 }).ClampFor(AnnotationKind.Ink).TransparencyPercent);
    }

    [Fact]
    public void ColorPlaneConvertsBetweenRgbAndHsv()
    {
        // The + picker's plane/strip (spec 3.2, 2026-10-13): hue 0–360, saturation and brightness 0–1.
        Assert.Equal(new RgbColor(255, 0, 0), PdfNotes.App.Views.ColorPickerPanel.FromHsv(0, 1, 1));
        Assert.Equal(new RgbColor(0, 255, 0), PdfNotes.App.Views.ColorPickerPanel.FromHsv(120, 1, 1));
        Assert.Equal(new RgbColor(0, 0, 255), PdfNotes.App.Views.ColorPickerPanel.FromHsv(240, 1, 1));
        Assert.Equal(RgbColor.White, PdfNotes.App.Views.ColorPickerPanel.FromHsv(200, 0, 1));
        Assert.Equal(RgbColor.Black, PdfNotes.App.Views.ColorPickerPanel.FromHsv(200, 1, 0));
        foreach (var color in ColorPalette.Basic.Append(new RgbColor(0x12, 0x80, 0x40)).Append(new RgbColor(0xFF, 0xF2, 0xA8)))
        {
            var (h, s, v) = PdfNotes.App.Views.ColorPickerPanel.ToHsv(color);
            Assert.Equal(color, PdfNotes.App.Views.ColorPickerPanel.FromHsv(h, s, v));
        }
    }

    [Fact]
    public void UserColorsAreAFixedListThatStopsWhenFull()
    {
        // Spec 3.4 (D14g): added at the end, never pushed out; a full list takes no more until one is removed.
        var user = new List<RgbColor>();
        for (byte i = 1; i <= 14; i++) user = ColorPalette.Add(user, new(i, i, i));
        Assert.Equal(ColorPalette.MaxUser, user.Count);
        Assert.Equal(12, ColorPalette.MaxUser);
        Assert.Equal(new RgbColor(1, 1, 1), user[0]);
        Assert.Equal(new RgbColor(12, 12, 12), user[^1]);
        user = ColorPalette.Add(user, new(5, 5, 5)); // already there: nothing moves
        Assert.Equal(new RgbColor(5, 5, 5), user[4]);
        user = ColorPalette.Add(ColorPalette.Remove(user, new(3, 3, 3)), new(20, 20, 20));
        Assert.Equal(new RgbColor(20, 20, 20), user[^1]);
        Assert.Equal(ColorPalette.MaxUser, user.Count);
    }

    [Fact]
    public void ToolSettingsSurviveARestartAndDamagedValuesFallBack()
    {
        string path = Path.Combine(folder, "settings.json");
        var settings = new AppSettings
        {
            Pen = AppSettings.StyleSettings.From(new StrokeStyle(new(10, 20, 30), 7.5, 25)),
            RecentColors = ["#102030", "bad", "#102030", "#ABCDEF"]
        };
        settings.Save(path);
        var loaded = AppSettings.Load(path);
        Assert.Equal(new StrokeStyle(new(10, 20, 30), 7.5, 25), loaded.Pen.ToStyle(StrokeStyle.DefaultPen, AnnotationKind.Ink));
        Assert.Equal([new RgbColor(0x10, 0x20, 0x30), new RgbColor(0xAB, 0xCD, 0xEF)], loaded.GetRecentColors());

        // Unknown colour → default style; out-of-range numbers → clamped; missing section → default.
        File.WriteAllText(path, """{ "pen": { "color": "red", "widthPt": 1e9, "transparency": -5 }, "highlighter": null }""");
        loaded = AppSettings.Load(path);
        Assert.Equal(StrokeStyle.DefaultPen, loaded.Pen.ToStyle(StrokeStyle.DefaultPen, AnnotationKind.Ink));
        Assert.Equal(StrokeStyle.DefaultHighlighter, loaded.Highlighter.ToStyle(StrokeStyle.DefaultHighlighter, AnnotationKind.FreeHighlight));
        File.WriteAllText(path, """{ "pen": { "color": "#FF0000", "widthPt": 1e9, "transparency": -5 } }""");
        Assert.Equal(new StrokeStyle(new(255, 0, 0), 40, 0), AppSettings.Load(path).Pen.ToStyle(StrokeStyle.DefaultPen, AnnotationKind.Ink));
    }
}
