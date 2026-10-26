using System.Collections;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfNotes.App.Themes;

namespace PdfNotes.Tests.App;

[CollectionDefinition("Theme", DisableParallelization = true)]
public sealed class ThemeCollection;

/// <summary>D13a: design tokens and the runtime theme (spec 3.1·3.2). Alone: it switches the shared theme and counts its events.</summary>
[Collection("Theme")]
public sealed class ThemeTests
{
    private static readonly string[] Palettes = ["Light", "Dark"];

    [Fact]
    public void LightAndDarkDefineTheSameColorTokens() => Sta(() =>
    {
        var light = Keys(Load("Light"));
        var dark = Keys(Load("Dark"));
        Assert.Equal(light.Order(), dark.Order());
        Assert.All(light, key => Assert.IsType<SolidColorBrush>(Load("Dark")[key]));
    });

    [Fact]
    public void NoTokenOrStyleKeyIsDefinedTwice() => Sta(() =>
    {
        // A style named like a brush made DynamicResource return the style (D13a regression).
        var all = Theme.Resources.MergedDictionaries.SelectMany(Keys).ToList();
        Assert.Empty(all.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key));
    });

    [Fact]
    public void EveryTokenUsedByTheAppExists() => Sta(() =>
    {
        var defined = Theme.Resources.MergedDictionaries.SelectMany(Keys).ToHashSet();
        var used = new List<(string File, string Key)>();
        foreach (var file in Directory.EnumerateFiles(AppSource(), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)) && !IsBuildOutput(f)))
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"\{(?:Dynamic|Static)Resource ([A-Za-z][\w.]*)\}")) used.Add((file, m.Groups[1].Value));
            foreach (Match m in Regex.Matches(text, @"(?:SetResourceReference\([^,]+,|FindResource\()\s*""([^""]+)""")) used.Add((file, m.Groups[1].Value));
            foreach (Match call in Regex.Matches(text, @"Theme\.(?:Brush|Color)\(([^;]*?)\)"))
                foreach (Match key in Regex.Matches(call.Groups[1].Value, @"""([^""]+)""")) used.Add((file, key.Groups[1].Value));
        }
        Assert.True(used.Count > 100, $"only {used.Count} references found");
        Assert.Empty(used.Where(u => !defined.Contains(u.Key)).Select(u => $"{Path.GetFileName(u.File)}: {u.Key}").Distinct());
    });

    [Fact]
    public void FrameCodeUsesNoLiteralColors()
    {
        // Frame colors live only in Themes/. Annotation colors are document data (their RGB values come from the model);
        // AppSettings holds the default annotation colors.
        var literal = new Regex(@"#[0-9A-Fa-f]{6}\b|(?<![\w.])Brushes\.(?!Transparent)\w+|(?<![\w.])Colors\.\w+|Color\.From(?:Rgb|Argb)\(0x");
        var offenders = Directory.EnumerateFiles(AppSource(), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)) && !IsBuildOutput(f))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Themes" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !f.Contains(Path.DirectorySeparatorChar + "Prototype" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !f.EndsWith("AppSettings.cs", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => literal.IsMatch(x.line) && !x.line.Contains("Field(", StringComparison.Ordinal)) // text style field defaults are values, not frame colors
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void ApplySwapsPaletteAndAccentInPlace() => Sta(() =>
    {
        var root = Theme.Resources;
        var bar = new Border();
        bar.Resources.MergedDictionaries.Add(root);
        bar.SetResourceReference(Border.BackgroundProperty, "Bar.Background");
        var lightBar = ((SolidColorBrush)bar.Background).Color;
        var selection = Theme.Color("Overlay.Selection");
        int changed = 0;
        EventHandler count = (_, _) => changed++;
        Theme.Changed += count;
        try
        {
            Theme.Apply(AppTheme.Dark, AccentColor.Green);
            Assert.Equal(((SolidColorBrush)Load("Dark")["Bar.Background"]).Color, ((SolidColorBrush)bar.Background).Color);
            Assert.NotEqual(lightBar, ((SolidColorBrush)bar.Background).Color);
            Assert.Equal((Color)Load("Accents")["Accent.Green.Dark"], Theme.Color("Accent"));
            Assert.Equal(selection, Theme.Color("Overlay.Selection")); // paper-side marks do not follow the theme
            Assert.Equal(Colors.White, Theme.Color("Paper"));
            Theme.Apply(AppTheme.Dark, AccentColor.Green);
            Assert.Equal(1, changed);
        }
        finally
        {
            Theme.Apply(AppTheme.Light, AccentColor.Blue);
            Theme.Changed -= count;
        }
        Assert.Equal(lightBar, ((SolidColorBrush)bar.Background).Color);
        Assert.Equal(Color.FromRgb(0x1E, 0x6F, 0xD9), Theme.Color("Accent"));
    });

    [Fact]
    public void TextAndAccentKeepReadableContrastInBothThemes() => Sta(() =>
    {
        try
        {
            foreach (var mode in Enum.GetValues<AppTheme>())
                foreach (var accent in Enum.GetValues<AccentColor>())
                {
                    Theme.Apply(mode, accent);
                    foreach (var surface in new[] { "Window.Background", "Bar.Background", "SubBar.Background", "Panel.Background", "Surface.Background", "Control.Background" })
                    {
                        foreach (var text in new[] { "Text.Primary", "Text.Secondary", "Text.Muted" })
                            Assert.True(Contrast(text, surface) >= 4.5, $"{mode} {text} on {surface}: {Contrast(text, surface):0.00}");
                        Assert.True(Contrast("Text.Primary", "Accent.Subtle") >= 4.5, $"{mode} {accent} text on checked: {Contrast("Text.Primary", "Accent.Subtle"):0.00}");
                    }
                    Assert.True(Contrast("Accent", "Bar.Background") >= 3, $"{mode} {accent} accent on bar: {Contrast("Accent", "Bar.Background"):0.00}");
                    Assert.True(Contrast("Accent.Foreground", "Accent") >= 3, $"{mode} {accent} mark on accent: {Contrast("Accent.Foreground", "Accent"):0.00}");
                }
        }
        finally { Theme.Apply(AppTheme.Light, AccentColor.Blue); }
    });

    // ----- helpers -----

    private static double Contrast(string a, string b)
    {
        double la = Luminance(Theme.Color(a)), lb = Luminance(Theme.Color(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static ResourceDictionary Load(string name) =>
        (ResourceDictionary)Application.LoadComponent(new Uri($"/DeotPDF;component/Themes/{name}.xaml", UriKind.Relative));

    private static IEnumerable<string> Keys(ResourceDictionary dictionary) => dictionary.Keys.Cast<object>().Select(k => k.ToString()!);

    private static bool IsBuildOutput(string path) =>
        path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string AppSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "PdfNotes.App");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/PdfNotes.App");
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
