using System.Globalization;
using System.Text;
using HarfBuzzSharp;
using PdfNotes.Core.Models;
using PdfNotes.Text.LineBreaking;

namespace PdfNotes.Text;

/// <summary>A glyph of a shaped cluster: its offset from the cluster's pen position and from the baseline (down), in points.</summary>
internal readonly record struct ShapedGlyph(ushort Id, double X, double Y);

/// <summary>
/// One character (rune) of a box after shaping: its advance in points, the face that draws it, the break before it and
/// the glyphs of the cluster it starts (none for the other characters of a cluster).
/// </summary>
internal sealed class ShapedItem(string text, int offset, TextRun run, SkiaFontFace face)
{
    public string Text { get; } = text;
    public int Offset { get; } = offset;
    public TextRun Run { get; } = run;
    public SkiaFontFace Face { get; } = face;
    public double Size => Run.SizePt;
    public double Advance { get; set; }
    public LineBreak BreakBefore { get; set; }
    public bool GraphemeStart { get; set; }
    /// <summary>White space that may stand past the line end without counting in its width (not no-break spaces).</summary>
    public bool Hangs { get; set; }
    public bool Tab { get; set; }
    /// <summary>A line feed, carriage return or other hard line break character; it ends its line and is not drawn.</summary>
    public bool HardBreak { get; set; }
    public ShapedGlyph[] Glyphs { get; set; } = [];
    /// <summary>The run asks for bold or italic and the face has no such style: drawn emboldened or slanted (S2).</summary>
    public bool FakeBold => Run.Bold && Face.Weight < 600;
    public bool FakeItalic => Run.Italic && !Face.Italic;
    public double Ascent => Face.Ascent * Size;
    /// <summary>Descent plus line gap.</summary>
    public double Below => (Face.Descent + Face.LineGap) * Size;
}

/// <summary>Turns a box's text and runs into <see cref="ShapedItem"/>s: font per grapheme, segments by font and script, HarfBuzz shaping.</summary>
internal static class SkiaShaper
{
    public static List<ShapedItem> Items(TextBoxAnnotation box)
    {
        string text = box.Text;
        var breaks = LineBreaker.Breaks(text);
        var graphemeStart = new bool[text.Length + 1];
        for (int i = 0; i < text.Length; i += StringInfo.GetNextTextElementLength(text, i)) graphemeStart[i] = true;
        graphemeStart[text.Length] = true;

        var items = new List<ShapedItem>(text.Length);
        foreach (var run in box.Runs)
        {
            int start = Math.Clamp(run.Start, 0, text.Length), end = Math.Clamp(run.Start + run.Length, start, text.Length);
            if (end > start) ShapeRun(text, start, end, run, items, graphemeStart);
        }
        foreach (var item in items)
        {
            item.BreakBefore = breaks[item.Offset];
            item.GraphemeStart = graphemeStart[item.Offset];
        }
        return items;
    }

    private static void ShapeRun(string text, int start, int end, TextRun run, List<ShapedItem> items, bool[] graphemeStart)
    {
        var primary = SkiaFonts.Face(run.FontFamily, run.Bold, run.Italic);
        var scripts = Scripts(text, start, end);

        // Characters are added in order; a segment (same face and script) is shaped when it ends.
        int segmentStart = -1, segmentItem = 0;
        SkiaFontFace? segmentFace = null;
        Script segmentScript = Script.Common;
        SkiaFontFace graphemeFace = primary;
        void Flush(int at)
        {
            if (segmentStart >= 0) Shape(text, segmentStart, at, segmentFace!, segmentScript, items, segmentItem, graphemeStart);
            segmentStart = -1;
        }

        for (int i = start; i < end;)
        {
            var (rune, length) = TextRunes.At(text, i);
            var cls = LineBreaker.ClassOf(rune.Value);
            bool hardBreak = cls is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL;
            // Controls and line or paragraph separators (U+2028, U+2029) are not drawn; the line breaks end their line.
            if (Rune.IsControl(rune) || hardBreak)
            {
                Flush(i);
                items.Add(new ShapedItem(text.Substring(i, length), i, run, primary) { Hangs = true, Tab = rune.Value == '\t', HardBreak = hardBreak });
                i += length;
                continue;
            }
            // A grapheme (base with its marks, joiners, variation selectors and emoji modifiers) keeps one face.
            if (graphemeStart[i] || i == start) graphemeFace = SkiaFonts.FaceFor(primary, rune.Value, run.Bold, run.Italic);
            var script = scripts[i - start];
            if (segmentStart < 0 || graphemeFace != segmentFace || script != segmentScript)
            {
                Flush(i);
                segmentStart = i; segmentItem = items.Count; segmentFace = graphemeFace; segmentScript = script;
            }
            items.Add(new ShapedItem(text.Substring(i, length), i, run, graphemeFace)
            {
                Hangs = Rune.IsWhiteSpace(rune) && LineBreaker.ClassOf(rune.Value) != LineBreakClass.GL,
            });
            i += length;
        }
        Flush(end);
    }

    /// <summary>
    /// The script of each UTF-16 unit of the run: common and inherited characters (spaces, digits, punctuation, marks)
    /// take the script before them, or the first real script after them at the start.
    /// </summary>
    private static Script[] Scripts(string text, int start, int end)
    {
        var result = new Script[end - start];
        var unicode = UnicodeFunctions.Default;
        Script current = Script.Common;
        var pending = new List<int>();
        for (int i = start; i < end;)
        {
            var (rune, length) = TextRunes.At(text, i);
            var script = unicode.GetScript(rune.Value);
            bool weak = script == Script.Common || script == Script.Inherited || script == Script.Unknown;
            if (weak && current == Script.Common) pending.Add(i);
            else
            {
                if (!weak)
                {
                    if (current == Script.Common) foreach (int p in pending) Fill(result, p - start, text, p, script);
                    pending.Clear();
                    current = script;
                }
                Fill(result, i - start, text, i, current);
            }
            i += length;
        }
        foreach (int p in pending) Fill(result, p - start, text, p, Script.Common);
        return result;
    }

    private static void Fill(Script[] result, int at, string text, int index, Script script)
    {
        result[at] = script;
        if (char.IsSurrogatePair(text, index) && at + 1 < result.Length) result[at + 1] = script;
    }

    /// <summary>
    /// Shapes [<paramref name="start"/>, <paramref name="end"/>) of the text (the rest of the text is context) and sets
    /// the advances and glyphs of its items, which begin at <paramref name="firstItem"/>. A cluster's advance is shared
    /// equally by the graphemes in it (a ligature like "fi" puts the caret between its letters); within a grapheme the
    /// first character takes it all.
    /// </summary>
    private static void Shape(string text, int start, int end, SkiaFontFace face, Script script, List<ShapedItem> items, int firstItem, bool[] graphemeStart)
    {
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(text.AsSpan(), start, end - start);
        buffer.Direction = Direction.LeftToRight;
        buffer.Script = script;
        buffer.GuessSegmentProperties();
        var font = face.HarfBuzz;
        lock (font) font.Shape(buffer);
        var infos = buffer.GetGlyphInfoSpan();
        var positions = buffer.GetGlyphPositionSpan();
        double scale = items[firstItem].Size / face.UnitsPerEm;

        // Items of this segment by UTF-16 index.
        var byIndex = new Dictionary<int, ShapedItem>(end - start);
        for (int k = firstItem; k < items.Count; k++) byIndex[items[k].Offset] = items[k];

        int g = 0;
        while (g < infos.Length)
        {
            int cluster = (int)infos[g].Cluster;
            var glyphs = new List<ShapedGlyph>();
            double pen = 0;
            for (; g < infos.Length && (int)infos[g].Cluster == cluster; g++)
            {
                glyphs.Add(new((ushort)infos[g].Codepoint, pen + positions[g].XOffset * scale, -positions[g].YOffset * scale));
                pen += positions[g].XAdvance * scale;
            }
            int next = g < infos.Length ? (int)infos[g].Cluster : end;
            if (next <= cluster) next = end; // not monotone (should not happen left to right): give the rest to this cluster
            if (!byIndex.TryGetValue(cluster, out var first)) continue;
            first.Glyphs = [.. glyphs];

            int graphemes = 0;
            for (int i = cluster; i < next; i++) if (i == cluster || graphemeStart[i]) graphemes++;
            double share = pen / Math.Max(1, graphemes);
            for (int i = cluster; i < next; i++)
                if (byIndex.TryGetValue(i, out var item)) item.Advance = i == cluster || graphemeStart[i] ? share : 0;
        }
    }
}
