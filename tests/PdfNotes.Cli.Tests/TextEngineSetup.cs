using System.Runtime.CompilerServices;
using PdfNotes.Text;

namespace PdfNotes.Tests;

/// <summary>The WPF-free test project always runs the Skia engine (roadmap P2 S7).</summary>
internal static class TextEngineSetup
{
#pragma warning disable CA2255 // the test assembly registers its text engine before any test runs (D14a)
    [ModuleInitializer]
    internal static void Register()
    {
        SkiaText.Register();
        // Linux and macOS have no Malgun Gothic, the default text family. A text box in a family the machine lacks reopens
        // read-only by design (what other platforms do with it is a P4 decision, roadmap 5). These tests are about
        // everything else, so the default family counts as installed and is drawn with the engine's fallback.
        if (!TextLayout.FontAvailable(DefaultFamily)) TextLayout.Engine = new DefaultFamilyPresent(TextLayout.Engine);
    }
#pragma warning restore CA2255

    public static bool Skia => true;

    private const string DefaultFamily = "Malgun Gothic"; // TextEditing.Default, not UI text (D14e)

    private sealed class DefaultFamilyPresent(ITextLayoutEngine inner) : ITextLayoutEngine
    {
        public TextLayout.Layout Build(PdfNotes.Core.Models.TextBoxAnnotation box) => inner.Build(box);
        public Task<TextLayout.Layout> BuildAsync(PdfNotes.Core.Models.TextBoxAnnotation box, bool urgent) => inner.BuildAsync(box, urgent);
        public bool FontAvailable(string name) => name == DefaultFamily || inner.FontAvailable(name);
        public string FallbackFont => inner.FallbackFont;
        public IReadOnlyList<TextFontUse> Fonts(PdfNotes.Core.Models.TextBoxAnnotation box) =>
            [.. inner.Fonts(box).Select(f => f.Requested == DefaultFamily && !f.Missing ? f with { Font = DefaultFamily } : f)];
        public IReadOnlyList<string> FontFamilies() => [.. inner.FontFamilies().Append(DefaultFamily).Distinct()];
    }
}
