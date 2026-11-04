namespace PdfNotes.Text;

/// <summary>The SkiaSharp + HarfBuzz text engine (roadmap P2): registration for apps and tests that use it.</summary>
public static class SkiaText
{
    public static SkiaTextEngine Engine { get; } = new();

    /// <summary>Registers this engine for <see cref="TextLayout"/> unless another one is registered (idempotent).</summary>
    public static void Register() { if (!TextLayout.HasEngine) TextLayout.Engine = Engine; }
}
