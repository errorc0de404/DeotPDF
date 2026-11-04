using System.Globalization;
using System.IO;
using System.Text.Json;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;
using PdfNotes.Text.LineBreaking;

namespace PdfNotes.Tests.TextEngine;

/// <summary>
/// Roadmap P2 S1: the Skia engine's layout rules. The engine is called directly (<see cref="SkiaTextEngine.Layout"/>);
/// the test assembly's registered engine stays WPF until S3.
/// </summary>
public class SkiaEngineTests
{
    private const double Pad = TextLayout.PaddingPt;

    private static TextRun Run(int start, int length, string font = "Malgun Gothic", double size = 12, bool bold = false) =>
        new(start, length, font, size, RgbColor.Black, bold, false, false, false);

    private static TextBoxAnnotation Box(string text, double width, TextAlignment align = TextAlignment.Left, params TextRun[] runs) =>
        new(Guid.NewGuid(), false, Affine.Identity, text, runs.Length > 0 ? [.. runs] : text.Length == 0 ? [] : [Run(0, text.Length)], align, width, TextBoxStyle.Default, []);

    private static SkiaTextLayout Lay(TextBoxAnnotation box) => SkiaTextEngine.Layout(box);

    private static double Right(TextLayout.Character c) => c.Bounds.X + c.Bounds.Width;

    [Fact]
    public void AnEmptyBoxIsOneLineOfItsTypingStyle()
    {
        var layout = Lay(Box("", 100));
        var face = SkiaFonts.Face("Malgun Gothic", false, false);
        Assert.Empty(layout.Characters);
        Assert.Single(layout.Lines);
        Assert.Equal(Math.Max(20, 2 * Pad + face.LineSpacing * 12), layout.Height, 6);
    }

    [Theory]
    [InlineData("한글 문장은 줄바꿈이 어디서 일어나는지 확인합니다. 긴 단어와 공백, 문장부호까지.", 120)]
    [InlineData("The quick brown fox jumps over the lazy dog; hyphen-ated words, numbers 1234567890 and (brackets).", 90)]
    [InlineData("회의 메모: Q3 계획 — budget 12.5% 증가, 담당 김철수/이영희, 마감 10/31.", 70)]
    public void LinesFitAndEndAtBreakOpportunities(string text, double width)
    {
        var layout = Lay(Box(text, width));
        var breaks = LineBreaker.Breaks(text);
        var lines = layout.Characters.GroupBy(c => c.Line).ToArray();
        Assert.True(lines.Length > 2);
        foreach (var line in lines)
        {
            Assert.All(line.Where(c => !char.IsWhiteSpace(c.Text[0])), c => Assert.True(Right(c) <= width - Pad + 1e-6, $"'{c.Text}' ends at {Right(c)}"));
            int first = line.First().Offset;
            Assert.True(first == 0 || breaks[first] != LineBreak.None, $"line starts at {first} without a break opportunity");
        }
        // Greedy: the first word of each next line would not have fitted on the line before it.
        for (int n = 1; n < lines.Length; n++)
        {
            var next = lines[n].TakeWhile((c, i) => i == 0 || breaks[c.Offset] == LineBreak.None).ToArray();
            double used = lines[n - 1].Where(c => !char.IsWhiteSpace(c.Text[0])).Max(Right);
            double spaces = lines[n - 1].Where(c => char.IsWhiteSpace(c.Text[0]) && c.Bounds.X >= used - 1e-9).Sum(c => c.Bounds.Width);
            Assert.True(used + spaces + next.Sum(c => c.Bounds.Width) > width - Pad - 1e-6, $"line {n} could have started on line {n - 1}");
        }
    }

    [Fact]
    public void AWordLongerThanTheLineBreaksBetweenGraphemes()
    {
        const string word = "Pneumonoultramicroscopicsilicovolcanoconiosis";
        var layout = Lay(Box(word, 60, TextAlignment.Left, Run(0, word.Length, "Arial", 12)));
        var lines = layout.Characters.GroupBy(c => c.Line).ToArray();
        Assert.True(lines.Length > 3);
        Assert.All(lines, line => Assert.True(line.Max(Right) <= 60 - Pad + 1e-6));
        Assert.Equal(word, string.Concat(layout.Characters.Select(c => c.Text)));

        // A grapheme is never split: é written as e + U+0301 stays on one line, also when one grapheme is wider than the line.
        string accents = string.Concat(Enumerable.Repeat("é", 12));
        var narrow = Lay(Box(accents, 16, TextAlignment.Left, Run(0, accents.Length, "Arial", 20)));
        for (int i = 0; i < narrow.Characters.Length; i += 2) Assert.Equal(narrow.Characters[i].Line, narrow.Characters[i + 1].Line);
        Assert.True(narrow.Characters.Select(c => c.Line).Distinct().Count() >= 6);
    }

    [Theory]
    [InlineData("a\nb", 2)]
    [InlineData("a\n", 2)]
    [InlineData("\n\n", 3)]
    [InlineData("", 1)]
    [InlineData("a\r\nb", 2)]
    public void HardBreaksEndLinesAndAFinalOneAddsAnEmptyLine(string text, int lines)
    {
        var layout = Lay(Box(text, 100, TextAlignment.Left, text.Length == 0 ? [] : [Run(0, text.Length)]));
        Assert.Equal(lines, layout.Lines.Length);
        Assert.DoesNotContain(layout.Characters, c => c.Text is "\n" or "\r");
        Assert.Equal(2 * Pad + layout.Lines.Sum(l => l.Height), Math.Max(layout.Height, 2 * Pad + layout.Lines.Sum(l => l.Height)), 6);
        if (lines > 1) Assert.True(layout.Lines[^1].Top > layout.Lines[0].Top);
    }

    [Fact]
    public void TrailingSpacesHangAndDoNotMoveAlignedText()
    {
        var plain = Lay(Box("ab", 100, TextAlignment.Right));
        var spaced = Lay(Box("ab      ", 100, TextAlignment.Right));
        Assert.Equal(100 - Pad, Right(plain.Characters[1]), 6);
        Assert.Equal(Right(plain.Characters[1]), Right(spaced.Characters[1]), 6);
        Assert.Single(spaced.Lines); // spaces past the edge do not open a line

        var centered = Lay(Box("ab", 100, TextAlignment.Center));
        Assert.Equal(50, (centered.Characters[0].Bounds.X + Right(centered.Characters[1])) / 2, 6);
    }

    [Fact]
    public void TabsGoToTheNextStopFromTheLineStart()
    {
        var layout = Lay(Box("a\tb\tc\n\td", 400));
        double StartOf(string s) => layout.Characters.Single(c => c.Text == s).Bounds.X - Pad;
        Assert.Equal(SkiaTextEngine.TabStop, StartOf("b"), 6);
        Assert.Equal(2 * SkiaTextEngine.TabStop, StartOf("c"), 6);
        Assert.Equal(SkiaTextEngine.TabStop, StartOf("d"), 6);
    }

    [Fact]
    public void KerningIsApplied()
    {
        // Arial kerns "AV": the A before V advances less than an A alone (the WPF engine did not kern).
        var kerned = Lay(Box("AV", 200, TextAlignment.Left, Run(0, 2, "Arial", 40)));
        var alone = Lay(Box("A", 200, TextAlignment.Left, Run(0, 1, "Arial", 40)));
        Assert.True(kerned.Characters[0].Bounds.Width < alone.Characters[0].Bounds.Width - .5);
    }

    [Fact]
    public void MixedSizesShareABaselineAndTheLineFitsTheLargest()
    {
        var layout = Lay(Box("small BIG", 400, TextAlignment.Left, Run(0, 6, size: 10), Run(6, 3, size: 30)));
        var face = SkiaFonts.Face("Malgun Gothic", false, false);
        var small = layout.Characters[0];
        var big = layout.Characters[^1];
        Assert.Equal(small.Line, big.Line);
        Assert.Equal(small.Bounds.Y + face.Ascent * 10, big.Bounds.Y + face.Ascent * 30, 6);
        Assert.Equal(face.LineSpacing * 30, layout.Lines[0].Height, 6);
        Assert.Equal(2 * Pad + face.LineSpacing * 30, layout.Height, 6);
    }

    [Fact]
    public void ALayoutScaleScalesTheLayoutOfTheOriginalUnits()
    {
        const string text = "한글 문장은 줄바꿈이 어디서 일어나는지 확인합니다. The quick brown fox.";
        var original = Lay(Box(text, 150, TextAlignment.Left, Run(0, text.Length, size: 11)));
        var scaled = Lay(Box(text, 150 * .73, TextAlignment.Left, Run(0, text.Length, size: 11 * .73)) with { LayoutScale = .73 });
        Assert.Equal(original.Height * .73, scaled.Height, 6);
        Assert.Equal(original.Characters.Length, scaled.Characters.Length);
        for (int i = 0; i < original.Characters.Length; i++)
        {
            Assert.Equal(original.Characters[i].Line, scaled.Characters[i].Line);
            Assert.Equal(original.Characters[i].Bounds.X * .73, scaled.Characters[i].Bounds.X, 6);
        }
    }

    [Fact]
    public void AMissingFamilyUsesTheFallbackFamily()
    {
        Assert.False(SkiaText.Engine.FontAvailable("NoSuchFont-P2"));
        Assert.True(SkiaText.Engine.FontAvailable(SkiaText.Engine.FallbackFont));
        if (OperatingSystem.IsWindows()) Assert.Equal("Malgun Gothic", SkiaText.Engine.FallbackFont);
        const string text = "메모 memo 123";
        var missing = Lay(Box(text, 300, TextAlignment.Left, Run(0, text.Length, "NoSuchFont-P2")));
        var fallback = Lay(Box(text, 300, TextAlignment.Left, Run(0, text.Length, SkiaText.Engine.FallbackFont)));
        Assert.Equal(fallback.Characters.Select(c => c.Bounds), missing.Characters.Select(c => c.Bounds));
    }

    [Fact]
    public void EveryCharacterButLineBreaksIsReportedOnceInOrder()
    {
        foreach (var box in D14aCorpus.Boxes())
        {
            var layout = Lay(box);
            var expected = new List<int>();
            for (int i = 0; i < box.Text.Length; i += char.IsSurrogatePair(box.Text, i) ? 2 : 1)
                if (box.Text[i] != '\n') expected.Add(i);
            Assert.Equal(expected, layout.Characters.Select(c => c.Offset));
            Assert.All(layout.Characters, c => Assert.Equal(box.Text.Substring(c.Offset, c.Text.Length), c.Text));
        }
    }

    // ----- d14a-text baseline -----

    private static string Baseline => Path.Combine(TestSourceFolder(), "TextEngine", "skia-d14a.json");

    private static string TestSourceFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PdfNotes.slnx"))) return Path.Combine(dir.FullName, "tests", "PdfNotes.Tests");
        throw new DirectoryNotFoundException("PdfNotes.slnx not found above the test folder.");
    }

    private static string R(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static object[] Snapshot() => [.. D14aCorpus.Boxes().Select((box, i) =>
    {
        var layout = Lay(box);
        return new
        {
            Index = i,
            Height = R(layout.Height),
            Characters = layout.Characters.Select(c => $"{c.Text}|{c.Line}|{c.Offset}|{R(c.Bounds.X)},{R(c.Bounds.Y)},{R(c.Bounds.Width)},{R(c.Bounds.Height)}").ToArray(),
        };
    })];

    /// <summary>
    /// The d14a-text corpus laid out by the Skia engine matches <c>skia-d14a.json</c>: the same characters on the same
    /// lines, positions and heights within 0.01 pt (font files differ slightly between Windows builds). Regenerate with
    /// PDFNOTES_UPDATE_BASELINES=1 after an intended engine change and say why in the work log.
    /// </summary>
    [Fact]
    public void D14aCorpusMatchesTheSkiaBaseline()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        string current = JsonSerializer.Serialize(new { Engine = "skia", Boxes = Snapshot() }, options);
        if (Environment.GetEnvironmentVariable("PDFNOTES_UPDATE_BASELINES") == "1")
        {
            File.WriteAllText(Baseline, current.ReplaceLineEndings("\n") + "\n");
            return;
        }
        using var expected = JsonDocument.Parse(File.ReadAllText(Baseline));
        using var actual = JsonDocument.Parse(current);
        var e = expected.RootElement.GetProperty("Boxes").EnumerateArray().ToArray();
        var a = actual.RootElement.GetProperty("Boxes").EnumerateArray().ToArray();
        Assert.Equal(e.Length, a.Length);
        for (int i = 0; i < e.Length; i++)
        {
            Assert.True(Math.Abs(double.Parse(e[i].GetProperty("Height").GetString()!, CultureInfo.InvariantCulture)
                - double.Parse(a[i].GetProperty("Height").GetString()!, CultureInfo.InvariantCulture)) <= .01, $"box {i} height");
            var ec = e[i].GetProperty("Characters").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var ac = a[i].GetProperty("Characters").EnumerateArray().Select(x => x.GetString()!).ToArray();
            Assert.Equal(ec.Length, ac.Length);
            for (int k = 0; k < ec.Length; k++)
            {
                var (ep, ap) = (ec[k].Split('|'), ac[k].Split('|'));
                Assert.True(ep[0] == ap[0] && ep[1] == ap[1] && ep[2] == ap[2], $"box {i}: {ac[k]} instead of {ec[k]}");
                var (er, ar) = (ep[3].Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray(),
                    ap[3].Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray());
                Assert.True(er.Zip(ar).All(p => Math.Abs(p.First - p.Second) <= .01), $"box {i}: {ac[k]} instead of {ec[k]}");
            }
        }
    }
}
