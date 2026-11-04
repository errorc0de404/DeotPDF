using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using PdfNotes.Desktop.Themes;
using PdfNotes.Desktop.Views;
using PdfNotes.Editor.Services;

namespace PdfNotes.Desktop.Tests;

/// <summary>Roadmap P4 A0: the design tokens, the theme setting and the window shell.</summary>
public sealed class ShellTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>key → value of a resource dictionary file (brush colors, numbers, thicknesses, font names).</summary>
    private static Dictionary<string, string> Entries(XElement dictionary) => dictionary.Elements()
        .Where(e => e.Attribute(X + "Key") is not null && e.Name.LocalName != "ResourceDictionary.ThemeDictionaries" && e.Name.LocalName != "Null")
        .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => ((string?)e.Attribute("Color") ?? e.Value).Trim().ToUpperInvariant());

    private static XElement Load(params string[] path) => XElement.Load(Path.Combine([Headless.Repository, .. path]));

    [Fact]
    public void TheTokensAreTheWpfTokens()
    {
        // Until the WPF app is retired both apps must look the same: same keys, same values (roadmap P4, decision record 18).
        var wpf = Path.Combine("src", "PdfNotes.App", "Themes");
        var desktop = Path.Combine("src", "PdfNotes.Desktop", "Themes");
        Assert.Equal(Entries(Load(wpf, "Tokens.xaml")), Entries(Load(desktop, "Tokens.axaml")));
        var colors = Load(desktop, "Colors.axaml");
        var themes = colors.Elements().Single(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries").Elements()
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, Entries);
        Assert.Equal(Entries(Load(wpf, "Light.xaml")), themes["Light"]);
        Assert.Equal(Entries(Load(wpf, "Dark.xaml")), themes["Dark"]);
        var shared = Entries(colors);
        foreach (var (key, value) in Entries(Load(wpf, "Canvas.xaml")).Concat(Entries(Load(wpf, "Accents.xaml"))))
            Assert.Equal(value, shared[key]);
    }

    [Fact]
    public void TheThemeSettingSwitchesThePaletteAndTheAccent() => Headless.Run(() =>
    {
        var app = Application.Current!;
        var window = new MainWindow();
        window.Show();
        try
        {
            DesktopTheme.Use(app, ThemeChoice.Light, AccentColor.Blue);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(Color.Parse("#FAFAFA"), ((ISolidColorBrush)window.Background!).Color);
            Assert.Equal(Color.Parse("#1E6FD9"), ((ISolidColorBrush)app.Resources["Accent"]!).Color);

            DesktopTheme.Use(app, ThemeChoice.Dark, AccentColor.Green);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Dark, app.ActualThemeVariant);
            Assert.NotEqual(Color.Parse("#FAFAFA"), ((ISolidColorBrush)window.Background!).Color);
            Assert.True(app.TryGetResource("Window.Background", ThemeVariant.Dark, out var dark));
            Assert.Equal(((ISolidColorBrush)dark!).Color, ((ISolidColorBrush)window.Background!).Color);
            // The dark variant of the accent (lighter), and Fluent controls take it too.
            Assert.Equal(Color.Parse("#4CC07A"), ((ISolidColorBrush)app.Resources["Accent"]!).Color);
            Assert.Equal(Color.Parse("#4CC07A"), (Color)app.Resources["SystemAccentColor"]!);
        }
        finally
        {
            DesktopTheme.Use(app, ThemeChoice.Light, AccentColor.Blue);
            window.Close();
        }
    });

    [Fact]
    public void TheWindowHasItsParts() => Headless.Run(() =>
    {
        var window = new MainWindow();
        window.Show();
        Assert.NotNull(window.FindControl<Border>("RibbonHost"));
        Assert.NotNull(window.FindControl<Border>("StatusBar"));
        Assert.Same(window.DocumentCanvas, window.FindControl<PdfNotes.Desktop.Canvas.DocumentCanvas>("Canvas"));
        Assert.Equal(1, window.DocumentCanvas.PageCount); // a new note until a file is opened
        window.Close();
    });
}
