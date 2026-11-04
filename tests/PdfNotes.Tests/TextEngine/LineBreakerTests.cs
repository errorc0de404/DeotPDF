using System.IO;
using System.Text;
using PdfNotes.Text.LineBreaking;

namespace PdfNotes.Tests.TextEngine;

/// <summary>Roadmap P2 S1: UAX #14 line break opportunities (the default rules, no tailoring).</summary>
public class LineBreakerTests
{
    /// <summary>The text with '|' where a line may end and '!' where it must (the end of text is left out).</summary>
    private static string Marked(string text)
    {
        var breaks = LineBreaker.Breaks(text);
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (breaks[i] == LineBreak.Allowed) sb.Append('|');
            if (breaks[i] == LineBreak.Mandatory) sb.Append('!');
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    [Theory]
    [InlineData("The quick brown fox", "The |quick |brown |fox")]
    [InlineData("hyphen-ated words", "hyphen-|ated |words")]
    [InlineData("12.5% (brackets).", "12.5% |(brackets).")]
    [InlineData("-5 and 1/12", "-5 |and |1/12")]
    [InlineData("e.g. this", "e.g. |this")]
    [InlineData("a\nb\r\nc", "a\n!b\r\n!c")]
    [InlineData("no break", "no break")]
    [InlineData("tab\there", "tab\t|here")]
    public void LatinBreaksAfterSpacesAndHyphensAndNotInsideNumbers(string text, string expected) => Assert.Equal(expected, Marked(text));

    [Theory]
    // Between Hangul syllables, but not before closing punctuation or inside a syllable written with conjoining jamo.
    [InlineData("한글 문장.", "한|글 |문|장.")]
    [InlineData("각가", "각|가")]
    [InlineData("(괄호)와", "(괄|호)|와")]
    [InlineData("회의 메모: Q3", "회|의 |메|모: |Q3")]
    [InlineData("漢字かな、カナ。", "漢|字|か|な、|カ|ナ。")]
    public void EastAsianTextBreaksBetweenCharacters(string text, string expected) => Assert.Equal(expected, Marked(text));

    [Fact]
    public void EmojiSequencesAndCombiningMarksStayTogether()
    {
        // 👍🏽 (base + skin tone), 👨‍👩‍👧 (ZWJ sequence), é as e + U+0301, two flags.
        const string text = "a👍🏽👨‍👩‍👧é🇰🇷🇯🇵";
        var breaks = LineBreaker.Breaks(text);
        int[] expected = [1, text.IndexOf("👨", StringComparison.Ordinal), text.IndexOf("é", StringComparison.Ordinal),
            text.IndexOf("🇰", StringComparison.Ordinal), text.IndexOf("🇯", StringComparison.Ordinal)];
        var allowed = Enumerable.Range(1, text.Length - 1).Where(i => breaks[i] != LineBreak.None).ToArray();
        // Breaks only between the sequences, never inside one.
        Assert.Equal(expected, allowed);
        Assert.Contains(text.IndexOf("🇯", StringComparison.Ordinal), allowed); // two flags: break between pairs
        Assert.DoesNotContain(text.IndexOf("🏽", StringComparison.Ordinal), allowed);
        Assert.DoesNotContain(text.IndexOf("́", StringComparison.Ordinal), allowed);
    }

    [Fact]
    public void SurrogateHalvesAndTheStartNeverBreak()
    {
        var breaks = LineBreaker.Breaks("😀😀");
        Assert.Equal([LineBreak.None, LineBreak.None, LineBreak.Allowed, LineBreak.None, LineBreak.Mandatory], breaks);
        Assert.Equal([LineBreak.Mandatory], LineBreaker.Breaks(""));
    }

    /// <summary>
    /// The Unicode conformance file (LineBreakTest.txt of <see cref="LineBreaker.UnicodeVersion"/>, about 19,000 cases).
    /// It is 3 MB, so it is not in the repo: set PDFNOTES_UAX14_TEST to its path
    /// (https://www.unicode.org/Public/UCD/latest/ucd/auxiliary/LineBreakTest.txt). All cases passed when S1 was written.
    /// </summary>
    [Fact]
    public void PassesTheUnicodeConformanceFileWhenGiven()
    {
        string? path = Environment.GetEnvironmentVariable("PDFNOTES_UAX14_TEST");
        if (string.IsNullOrEmpty(path)) return;
        var failures = new List<string>();
        int total = 0;
        foreach (string line in File.ReadLines(path))
        {
            string data = line.Split('#')[0].Trim();
            if (data.Length == 0) continue;
            total++;
            var text = new StringBuilder();
            var expected = new List<(int Index, bool Break)>();
            foreach (string token in data.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token is "×" or "÷") expected.Add((text.Length, token == "÷"));
                else text.Append(char.ConvertFromUtf32(Convert.ToInt32(token, 16)));
            }
            var breaks = LineBreaker.Breaks(text.ToString());
            if (expected.Any(e => e.Index > 0 && e.Index < text.Length && (breaks[e.Index] != LineBreak.None) != e.Break)) failures.Add(line);
        }
        Assert.True(total > 10_000, $"only {total} cases read from {path}");
        Assert.True(failures.Count == 0, $"{failures.Count}/{total} failed, first: {string.Join("\n", failures.Take(5))}");
    }
}
