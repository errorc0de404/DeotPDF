using System.Text;

namespace PdfNotes.Core.Geometry;

/// <summary>
/// Plain document search (spec 12): case-insensitive substring matching over a sequence of text units (PDF glyphs or
/// laid-out text box characters). Runs of white space — including the line ends an engine reports — match one space,
/// and a unit that starts a new line counts as a space before it, so a phrase broken across lines is still found.
/// No regular expressions, no OCR.
/// </summary>
public static class TextSearch
{
    /// <summary>The query as it is matched: trimmed, white space runs collapsed to one space. Empty means no search.</summary>
    public static string NormalizeQuery(string? query)
    {
        var text = new StringBuilder();
        foreach (char c in (query ?? "").Trim())
        {
            if (char.IsWhiteSpace(c)) { if (text.Length > 0 && text[^1] != ' ') text.Append(' '); }
            else text.Append(c);
        }
        return text.ToString();
    }

    /// <summary>
    /// Unit ranges [start, end) of the non-overlapping matches of <paramref name="query"/> in reading order.
    /// <paramref name="breakBefore"/> marks units that begin a new line or follow a gap (null: never).
    /// A match never starts or ends on the space that stands for a line break.
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> Find(IReadOnlyList<string> units, string query, Func<int, bool>? breakBefore = null)
    {
        query = NormalizeQuery(query);
        if (query.Length == 0 || units.Count == 0) return [];
        var text = new StringBuilder();
        var owner = new List<int>(); // unit of each searchable character
        for (int i = 0; i < units.Count; i++)
        {
            if (i > 0 && breakBefore?.Invoke(i) == true) AppendSpace(text, owner, i);
            foreach (char c in units[i] ?? "")
            {
                if (char.IsWhiteSpace(c)) AppendSpace(text, owner, i);
                else { text.Append(c); owner.Add(i); }
            }
        }
        string haystack = text.ToString();
        var hits = new List<(int, int)>();
        for (int at = 0; at <= haystack.Length - query.Length;)
        {
            int found = haystack.IndexOf(query, at, StringComparison.OrdinalIgnoreCase);
            if (found < 0) break;
            int first = owner[found], last = owner[found + query.Length - 1];
            hits.Add((first, last + 1));
            at = found + query.Length;
        }
        return hits;
    }

    private static void AppendSpace(StringBuilder text, List<int> owner, int unit)
    {
        if (text.Length == 0 || text[^1] == ' ') return;
        text.Append(' ');
        owner.Add(unit);
    }
}
