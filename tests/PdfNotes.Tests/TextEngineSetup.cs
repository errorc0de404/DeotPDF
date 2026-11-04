using System.Runtime.CompilerServices;
using PdfNotes.Text;

namespace PdfNotes.Tests;

/// <summary>
/// The text engine of the test assembly (D14a). WPF by default; PDFNOTES_TEXT_ENGINE=skia runs the tests with the Skia
/// engine instead (roadmap P2 S2/S3). Tests that compare with an engine's own drawing ask <see cref="Skia"/>.
/// </summary>
internal static class TextEngineSetup
{
#pragma warning disable CA2255 // the test assembly registers its text engine before any test runs (D14a)
    [ModuleInitializer]
    internal static void Register()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("PDFNOTES_TEXT_ENGINE"), "skia", StringComparison.OrdinalIgnoreCase)) SkiaText.Register();
        else WpfText.Register();
    }
#pragma warning restore CA2255

    public static bool Skia => TextLayout.Engine is SkiaTextEngine;

    /// <summary>The default family (Malgun Gothic) is not installed and the tests pretend it is (Cli.Tests on Linux and macOS).</summary>
    public static bool DefaultFamilySimulated { get; private set; }

    /// <summary>
    /// macOS has a last-resort font with a box for every code point, so nothing is ever a missing glyph there.
    /// </summary>
    public static bool EveryCodePointHasAGlyph => OperatingSystem.IsMacOS();
}
