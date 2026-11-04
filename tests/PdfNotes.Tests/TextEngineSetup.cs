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
}
