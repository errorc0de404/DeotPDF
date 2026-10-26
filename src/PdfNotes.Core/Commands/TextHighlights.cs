using System.Collections.Immutable;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Commands;

/// <summary>
/// Text box highlights (spec 9.4): character ranges of the box text, kept ordered, non-overlapping and on grapheme
/// boundaries. Editing the text moves them with their characters: text inserted strictly inside a highlight joins it,
/// text inserted exactly at either edge does not (spec D09 work 4), deleted characters leave it.
/// </summary>
public static class TextHighlights
{
    /// <summary>Highlights [start, start+length) with <paramref name="style"/>, replacing what overlapped there.</summary>
    public static TextBoxAnnotation Add(TextBoxAnnotation box, int start, int length, TextHighlightRange style)
    {
        var (a, z) = TextEditing.Range(box.Text, start, length);
        if (z <= a) return box;
        var kept = Cut(box.Highlights, a, z);
        kept.Add(style with { Start = a, Length = z - a });
        return box with { Highlights = [.. kept.OrderBy(h => h.Start)] };
    }

    /// <summary>Removes highlighting from [start, start+length); highlights partly inside keep their outside parts.</summary>
    public static TextBoxAnnotation Remove(TextBoxAnnotation box, int start, int length)
    {
        var (a, z) = TextEditing.Range(box.Text, start, length);
        var kept = Cut(box.Highlights, a, z);
        return kept.SequenceEqual(box.Highlights) ? box : box with { Highlights = [.. kept] };
    }

    /// <summary>True when every character of [start, start+length) is highlighted.</summary>
    public static bool Covers(TextBoxAnnotation box, int start, int length)
    {
        var (a, z) = TextEditing.Range(box.Text, start, length);
        if (z <= a) return false;
        int at = a;
        foreach (var h in box.Highlights.OrderBy(h => h.Start))
        {
            if (h.Start > at) return false;
            at = Math.Max(at, h.Start + h.Length);
            if (at >= z) return true;
        }
        return false;
    }

    private static List<TextHighlightRange> Cut(IEnumerable<TextHighlightRange> highlights, int a, int z)
    {
        var kept = new List<TextHighlightRange>();
        foreach (var h in highlights)
        {
            int end = h.Start + h.Length;
            if (end <= a || h.Start >= z) { kept.Add(h); continue; }
            if (h.Start < a) kept.Add(h with { Length = a - h.Start });
            if (end > z) kept.Add(h with { Start = z, Length = end - z });
        }
        return kept;
    }

    /// <summary>
    /// Moves <paramref name="highlights"/> of <paramref name="before"/> onto <paramref name="after"/>. The edit is the
    /// changed middle between the common prefix and suffix. When the same characters are inserted or deleted next to
    /// equal ones the position is ambiguous ("aa" → "aaa"); <paramref name="caretAfter"/>, the caret after the edit,
    /// decides it so typing at a highlight's edge never joins it.
    /// </summary>
    public static ImmutableArray<TextHighlightRange> Remap(string before, string after, ImmutableArray<TextHighlightRange> highlights, int? caretAfter = null)
    {
        if (highlights.IsDefaultOrEmpty || before == after) return highlights.IsDefault ? [] : highlights;
        int p = 0;
        while (p < before.Length && p < after.Length && before[p] == after[p]) p++;
        int s = 0;
        while (s < before.Length - p && s < after.Length - p && before[^(s + 1)] == after[^(s + 1)]) s++;
        int removed = before.Length - p - s, inserted = after.Length - p - s;
        if (caretAfter is { } caret)
        {
            if (removed == 0 && inserted > 0)
                while (p > caret - inserted && p > 0 && after[p - 1] == after[p - 1 + inserted]) p--;
            else if (inserted == 0 && removed > 0)
                while (p > caret && p > 0 && before[p - 1] == before[p - 1 + removed]) p--;
        }
        int oldEnd = p + removed, delta = inserted - removed;
        var result = new List<TextHighlightRange>();
        foreach (var h in highlights)
        {
            int a = h.Start, b = h.Start + h.Length;
            (int A, int B) moved =
                b <= p ? (a, b) :                                    // before the edit (or ends exactly where text is inserted)
                a >= oldEnd ? (a + delta, b + delta) :               // after the edit (or starts exactly where text is inserted)
                a < p && b > oldEnd ? (a, b + delta) :               // the edit is strictly inside: replacement joins it
                a < p ? (a, p) :                                     // its tail was replaced
                b > oldEnd ? (p + inserted, b + delta) :             // its head was replaced
                (0, 0);                                              // all of it was deleted
            if (moved.B <= moved.A) continue;
            var (x, y) = TextEditing.Range(after, moved.A, moved.B - moved.A);
            if (result.Count > 0 && result[^1].Start + result[^1].Length > x) x = result[^1].Start + result[^1].Length;
            if (y > x) result.Add(h with { Start = x, Length = y - x });
        }
        return [.. result];
    }
}
