using System.Runtime.CompilerServices;
using PdfNotes.Text;

namespace PdfNotes.Tests;

/// <summary>The WPF-free test project always runs the Skia engine (roadmap P2 S7).</summary>
internal static class TextEngineSetup
{
#pragma warning disable CA2255 // the test assembly registers its text engine before any test runs (D14a)
    [ModuleInitializer]
    internal static void Register() => SkiaText.Register();
#pragma warning restore CA2255

    public static bool Skia => true;
}
