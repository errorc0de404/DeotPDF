using System.Collections.Immutable;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Commands;

/// <summary>UTF-16 text edits snap to extended grapheme boundaries; runs always partition the content.</summary>
public static class TextEditing
{
    public static double InputSize(double size) => double.IsFinite(size) ? Math.Clamp(size, 1, 400) : 12;
    public static TextRun Default { get; } = new(0, 0, "Malgun Gothic", 12, RgbColor.Black, false, false, false, false);
    public static (int Start, int End) Range(string text, int start, int length)
    {
        var b = ModelValidator.GraphemeBoundaries(text);
        int a = Math.Clamp(start, 0, text.Length), z = Math.Clamp(start + length, 0, text.Length);
        while (a > 0 && !b[a]) a--;
        while (z < text.Length && !b[z]) z++;
        return (a, z);
    }
    public static TextRun StyleAt(TextBoxAnnotation t, int offset) => t.Runs.FirstOrDefault(r => offset >= r.Start && offset < r.Start + r.Length)
        ?? t.Runs.LastOrDefault() ?? Default;
    public static TextBoxAnnotation Format(TextBoxAnnotation box, int start, int length, Func<TextRun, TextRun> change)
    {
        var (a, z) = Range(box.Text, start, length);
        var runs = new List<TextRun>();
        foreach (var r in box.Runs)
        {
            int end = r.Start + r.Length;
            if (r.Start < a) Add(runs, r with { Length = Math.Min(a, end) - r.Start });
            int left = Math.Max(a, r.Start), right = Math.Min(z, end);
            if (right > left) Add(runs, change(r) with { Start = left, Length = right - left });
            if (end > z) Add(runs, r with { Start = Math.Max(z, r.Start), Length = end - Math.Max(z, r.Start) });
        }
        return box.Runs.SequenceEqual(runs) ? box : box with { Runs = runs.ToImmutableArray() };
    }
    public static void Add(List<TextRun> runs, TextRun run)
    {
        if (run.Length <= 0) return;
        if (runs.Count > 0 && runs[^1] with { Start = run.Start, Length = run.Length } == run)
            runs[^1] = runs[^1] with { Length = runs[^1].Length + run.Length };
        else runs.Add(run);
    }
}
