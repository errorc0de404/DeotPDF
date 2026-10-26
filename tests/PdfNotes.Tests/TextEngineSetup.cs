using System.Runtime.CompilerServices;

namespace PdfNotes.Tests;

internal static class TextEngineSetup
{
#pragma warning disable CA2255 // the test assembly registers the WPF text engine before any test runs (D14a)
    [ModuleInitializer]
    internal static void Register() => PdfNotes.Text.WpfText.Register();
#pragma warning restore CA2255
}
