using System.Globalization;
using System.Text;
using PdfNotes.Core.Geometry;

namespace PdfNotes.Cli;

/// <summary>
/// Phrase matching for <c>find</c> (cli_plan 4.3.1). Both sides are compared after normalization: Unicode compatibility
/// forms (NFKC: ligatures such as "ﬁ", full-width letters), case, all white space and line breaks dropped (so spacing
/// differences do not matter, as in Korean transcripts), soft hyphens dropped and a hyphen that ends a line dropped (a
/// word split across lines matches the whole word).
/// </summary>
public static class TextMatch
{
    /// <summary>The normalized text of a page and, for each of its characters, the glyph it came from.</summary>
    public sealed record Haystack(string Text, int[] Owner);

    /// <summary>One match: glyph range [Start, End) and score (1 = exact after normalization).</summary>
    public readonly record struct Match(int Start, int End, double Score);

    private static readonly HashSet<string> Hyphens = ["-", "‐", "‑", "­"];

    public static string Normalize(string text)
    {
        var result = new StringBuilder();
        foreach (char c in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
            if (!char.IsWhiteSpace(c) && c != '­' && char.GetUnicodeCategory(c) != UnicodeCategory.Control) result.Append(c);
        return result.ToString();
    }

    public static Haystack Build(IReadOnlyList<TextGlyph> glyphs)
    {
        var lineEndHyphens = new HashSet<int>();
        foreach (var (start, end, _) in TextSelection.LineRuns(glyphs, 0, glyphs.Count))
        {
            int last = end - 1;
            while (last >= start && (glyphs[last].IsEmpty || string.IsNullOrWhiteSpace(glyphs[last].Text))) last--;
            if (last >= start && Hyphens.Contains(glyphs[last].Text)) lineEndHyphens.Add(last);
        }
        var text = new StringBuilder();
        var owner = new List<int>();
        for (int i = 0; i < glyphs.Count; i++)
        {
            if (lineEndHyphens.Contains(i)) continue;
            foreach (char c in Normalize(glyphs[i].Text ?? ""))
            {
                text.Append(c);
                owner.Add(i);
            }
        }
        return new(text.ToString(), [.. owner]);
    }

    /// <summary>
    /// Non-overlapping matches of <paramref name="query"/> (already normalized) in glyph order. Without
    /// <paramref name="fuzzy"/> only exact matches; with it also approximate ones scoring at least
    /// <paramref name="minScore"/> (1 − edit distance / query length), best first when they compete for the same text.
    /// </summary>
    public static IReadOnlyList<Match> Find(Haystack haystack, string query, bool fuzzy, double minScore)
    {
        if (query.Length == 0 || haystack.Text.Length == 0) return [];
        var spans = new List<(int Start, int End, int Distance)>();
        if (!fuzzy)
        {
            for (int at = haystack.Text.IndexOf(query, StringComparison.Ordinal); at >= 0;
                 at = haystack.Text.IndexOf(query, at + query.Length, StringComparison.Ordinal))
                spans.Add((at, at + query.Length, 0));
        }
        else spans.AddRange(Approximate(haystack.Text, query, (int)Math.Floor((1 - minScore) * query.Length + 1e-9)));
        var taken = new List<(int Start, int End)>();
        var result = new List<Match>();
        foreach (var s in spans.OrderBy(s => s.Distance).ThenBy(s => s.Start))
        {
            if (taken.Any(t => s.Start < t.End && t.Start < s.End)) continue;
            taken.Add((s.Start, s.End));
            result.Add(new(haystack.Owner[s.Start], haystack.Owner[s.End - 1] + 1, 1 - (double)s.Distance / query.Length));
        }
        return [.. result.OrderBy(m => m.Start)];
    }

    /// <summary>
    /// Approximate substring matches (Sellers): every end where the edit distance to some substring is at most
    /// <paramref name="k"/> and locally smallest, with the start of its best substring.
    /// </summary>
    private static IEnumerable<(int Start, int End, int Distance)> Approximate(string text, string query, int k)
    {
        int m = query.Length;
        var column = new int[m + 1];
        for (int i = 0; i <= m; i++) column[i] = i;
        var ends = new int[text.Length + 1];
        ends[0] = m;
        for (int j = 1; j <= text.Length; j++)
        {
            int diagonal = column[0];
            column[0] = 0;
            for (int i = 1; i <= m; i++)
            {
                int up = column[i];
                column[i] = Math.Min(Math.Min(column[i] + 1, column[i - 1] + 1), diagonal + (query[i - 1] == text[j - 1] ? 0 : 1));
                diagonal = up;
            }
            ends[j] = column[m];
        }
        // Each run of equal distance that is a local minimum gives one match: the end whose substring length is nearest
        // the query's (a wrong last character ties with dropping it; the whole word wins).
        for (int j = 1; j <= text.Length;)
        {
            int d = ends[j], last = j;
            while (last < text.Length && ends[last + 1] == d) last++;
            bool minimum = d <= k && (j == 1 || ends[j - 1] > d) && (last == text.Length || ends[last + 1] > d);
            if (minimum)
            {
                var best = Enumerable.Range(j, last - j + 1).Select(e => (Start: StartOf(text, query, e, d), End: e))
                    .OrderBy(c => Math.Abs(c.End - c.Start - m)).ThenByDescending(c => c.End).First();
                yield return (best.Start, best.End, d);
            }
            j = last + 1;
        }
    }

    /// <summary>The start of the closest substring ending at <paramref name="end"/> (smallest distance, then length nearest the query's).</summary>
    private static int StartOf(string text, string query, int end, int d)
    {
        int m = query.Length, window = Math.Min(end, m + d);
        // Distance between query and text[end - len .. end) for each len, by a DP over reversed strings.
        var column = new int[m + 1];
        for (int i = 0; i <= m; i++) column[i] = i;
        int bestLength = Math.Min(end, m), bestDistance = int.MaxValue;
        for (int len = 1; len <= window; len++)
        {
            char c = text[end - len];
            int diagonal = column[0];
            column[0] = len;
            for (int i = 1; i <= m; i++)
            {
                int up = column[i];
                column[i] = Math.Min(Math.Min(column[i] + 1, column[i - 1] + 1), diagonal + (query[m - i] == c ? 0 : 1));
                diagonal = up;
            }
            if (column[m] < bestDistance || column[m] == bestDistance && Math.Abs(len - m) < Math.Abs(bestLength - m))
                (bestDistance, bestLength) = (column[m], len);
        }
        return end - bestLength;
    }
}
