using System.Text;

namespace PdfNotes.Text;

/// <summary>
/// Code points of box text that may hold an unpaired surrogate (the model validator allows it; WPF typed or pasted text
/// can carry one): such a unit reads as U+FFFD, one UTF-16 unit long, instead of throwing like <see cref="Rune.GetRuneAt"/>.
/// </summary>
internal static class TextRunes
{
    public static (Rune Rune, int Length) At(string text, int index) =>
        Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out int length) == System.Buffers.OperationStatus.Done
            ? (rune, length) : (Rune.ReplacementChar, 1);
}
