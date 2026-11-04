using System.Globalization;
using System.Text;

namespace PdfNotes.TextEditor;

/// <summary>
/// Caret units of box text (roadmap P2 S4): graphemes (extended grapheme clusters, what one arrow key or Backspace
/// moves over) and words for Ctrl+arrows, Ctrl+Backspace and double clicks.
/// </summary>
/// <remarks>
/// Words (an agent default, roadmap P2; ask before it goes into the spec): runs of one kind of grapheme. Hangul
/// (syllables and jamo) is one kind and other letters, digits and marks another, so "Q3계획" is two words; white space,
/// punctuation and symbols are their own kinds; every line feed is a word of its own. Ctrl+Right goes past the word and
/// the spaces after it (to the start of the next word), Ctrl+Left back over spaces to the start of the word before.
/// </remarks>
public static class TextUnits
{
    /// <summary>Where each grapheme starts: true at 0, at every start and at the end.</summary>
    public static bool[] GraphemeStarts(string text)
    {
        var starts = new bool[text.Length + 1];
        for (int i = 0; i < text.Length; i += StringInfo.GetNextTextElementLength(text, i)) starts[i] = true;
        starts[text.Length] = true;
        return starts;
    }

    public static int NextGrapheme(string text, int at) => at >= text.Length ? text.Length : at + StringInfo.GetNextTextElementLength(text, at);

    public static int PreviousGrapheme(string text, int at)
    {
        if (at <= 0) return 0;
        int previous = 0;
        for (int i = 0; i < at; i += StringInfo.GetNextTextElementLength(text, i)) previous = i;
        return previous;
    }

    /// <summary>The nearest grapheme boundary at or before <paramref name="at"/>.</summary>
    public static int Snap(string text, int at)
    {
        at = Math.Clamp(at, 0, text.Length);
        int start = 0;
        for (int i = 0; i <= at && i < text.Length; i += StringInfo.GetNextTextElementLength(text, i)) start = i;
        return at == text.Length ? at : start;
    }

    public enum WordKind { Space, LineBreak, Hangul, Letter, Punctuation }

    /// <summary>A character that ends its line: line feed, carriage return, vertical tab, form feed, NEL, U+2028, U+2029.</summary>
    public static bool IsLineBreak(char c) => c is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029';

    public static WordKind KindAt(string text, int at)
    {
        var rune = Rune.DecodeFromUtf16(text.AsSpan(at), out var decoded, out _) == System.Buffers.OperationStatus.Done ? decoded : Rune.ReplacementChar;
        if (IsLineBreak(text[at])) return WordKind.LineBreak;
        if (Rune.IsWhiteSpace(rune)) return WordKind.Space;
        int v = rune.Value;
        if (v is >= 0xAC00 and <= 0xD7A3 or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F or >= 0xD7B0 and <= 0xD7FF)
            return WordKind.Hangul;
        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber
                or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                or UnicodeCategory.ConnectorPunctuation => WordKind.Letter,
            _ => WordKind.Punctuation,
        };
    }

    /// <summary>The end of the run of graphemes of one kind that starts at <paramref name="at"/> (a line feed alone).</summary>
    private static int RunEnd(string text, int at)
    {
        var kind = KindAt(text, at);
        int end = NextGrapheme(text, at);
        if (kind == WordKind.LineBreak) return end;
        while (end < text.Length && KindAt(text, end) == kind) end = NextGrapheme(text, end);
        return end;
    }

    /// <summary>The start of the run of one kind that ends at <paramref name="at"/>.</summary>
    private static int RunStart(string text, int at)
    {
        int start = PreviousGrapheme(text, at);
        var kind = KindAt(text, start);
        if (kind == WordKind.LineBreak) return start;
        while (start > 0)
        {
            int before = PreviousGrapheme(text, start);
            if (KindAt(text, before) != kind) break;
            start = before;
        }
        return start;
    }

    /// <summary>Ctrl+Right: past the word at <paramref name="at"/> and the spaces after it.</summary>
    public static int NextWordStart(string text, int at)
    {
        if (at >= text.Length) return text.Length;
        int end = KindAt(text, at) == WordKind.Space ? at : RunEnd(text, at);
        while (end < text.Length && KindAt(text, end) == WordKind.Space) end = NextGrapheme(text, end);
        return end;
    }

    /// <summary>Ctrl+Left: back over spaces, then to the start of the word before.</summary>
    public static int PreviousWordStart(string text, int at)
    {
        int start = at;
        while (start > 0 && KindAt(text, PreviousGrapheme(text, start)) == WordKind.Space) start = PreviousGrapheme(text, start);
        return start > 0 ? RunStart(text, start) : 0;
    }

    /// <summary>The word a double click at <paramref name="at"/> selects: the run of one kind there (spaces too).</summary>
    public static (int Start, int End) WordAt(string text, int at)
    {
        if (text.Length == 0) return (0, 0);
        int start = at >= text.Length ? PreviousGrapheme(text, text.Length) : Snap(text, at);
        int end = RunEnd(text, start);
        return (RunStart(text, end), end);
    }
}
