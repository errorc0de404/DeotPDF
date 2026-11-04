using System.Buffers.Binary;
using System.Collections.Concurrent;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace PdfNotes.Text;

/// <summary>
/// One font file face as the engine uses it: the Skia typeface, its HarfBuzz font at one unit per font unit (advances are
/// exact, no 26.6 rounding) and its vertical metrics from <c>hhea</c> per em.
/// </summary>
public sealed class SkiaFontFace
{
    private readonly Lazy<(HarfBuzzSharp.Font Font, SKStreamAsset Stream)> harfBuzz;

    internal SkiaFontFace(SKTypeface typeface)
    {
        Typeface = typeface;
        harfBuzz = new(() =>
        {
            // The blob reads the stream's memory, so the stream lives as long as the face (for the process).
            var stream = typeface.OpenStream(out int index);
            var face = new HarfBuzzSharp.Face(stream.ToHarfBuzzBlob(), index);
            var font = new HarfBuzzSharp.Font(face);
            font.SetScale(face.UnitsPerEm, face.UnitsPerEm);
            font.SetFunctionsOpenType();
            return (font, stream);
        });
        (UnitsPerEm, Ascent, Descent, LineGap) = ReadMetrics(typeface);
        (Weight, Italic) = ReadStyle(typeface);
        drawing = new(() => SfntTables.WithoutColor(typeface) ?? typeface);
    }

    public SKTypeface Typeface { get; }
    public string Family => Typeface.FamilyName;

    private readonly Lazy<SKTypeface> drawing;

    /// <summary>
    /// The typeface glyphs are drawn and outlined with. For a color font (COLR, CBDT, sbix or SVG glyphs, like Segoe UI
    /// Emoji) Skia gives no outline, so this is a copy of the font without its color tables: the one-color glyphs the
    /// font keeps for plain renderers, the same on screen and in PDF (the WPF engine drew these too). Glyph ids are
    /// unchanged, so shaping stays on <see cref="Typeface"/>.
    /// </summary>
    public SKTypeface DrawingTypeface => drawing.Value;
    public int UnitsPerEm { get; }

    /// <summary>Height above the baseline per em (hhea ascender).</summary>
    public double Ascent { get; }

    /// <summary>Depth below the baseline per em (−hhea descender).</summary>
    public double Descent { get; }

    /// <summary>Extra line spacing per em (hhea lineGap), below the descent.</summary>
    public double LineGap { get; }

    /// <summary>Line spacing per em: ascent + descent + line gap.</summary>
    public double LineSpacing => Ascent + Descent + LineGap;

    /// <summary>
    /// The weight the font file was designed with (OS/2 usWeightClass: 400 regular, 700 bold) — not what a platform
    /// simulation reports for it.
    /// </summary>
    public int Weight { get; }

    /// <summary>The font file is an italic or oblique design (OS/2 fsSelection, post italicAngle).</summary>
    public bool Italic { get; }

    internal HarfBuzzSharp.Font HarfBuzz => harfBuzz.Value.Font;

    public bool HasGlyph(int codePoint) => Typeface.ContainsGlyph(codePoint);

    private static (int Upm, double Ascent, double Descent, double Gap) ReadMetrics(SKTypeface face)
    {
        var hhea = Table(face, "hhea");
        var head = Table(face, "head");
        if (hhea is { Length: >= 10 } && head is { Length: >= 20 })
        {
            int upm = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(18));
            short ascender = BinaryPrimitives.ReadInt16BigEndian(hhea.AsSpan(4));
            short descender = BinaryPrimitives.ReadInt16BigEndian(hhea.AsSpan(6));
            short gap = BinaryPrimitives.ReadInt16BigEndian(hhea.AsSpan(8));
            if (upm > 0 && ascender - descender > 0) return (upm, ascender / (double)upm, -descender / (double)upm, Math.Max(0, (int)gap) / (double)upm);
        }
        // No usable hhea (some bitmap or old fonts): Skia's own metrics at 1 unit.
        using var font = new SKFont(face, 1);
        var m = font.Metrics;
        return (face.UnitsPerEm > 0 ? face.UnitsPerEm : 1000, -m.Ascent, m.Descent, Math.Max(0, m.Leading));
    }

    private static (int Weight, bool Italic) ReadStyle(SKTypeface face)
    {
        var os2 = Table(face, "OS/2");
        var post = Table(face, "post");
        bool slanted = post is { Length: >= 8 } && BinaryPrimitives.ReadInt32BigEndian(post.AsSpan(4)) != 0;
        if (os2 is { Length: >= 64 })
        {
            int weight = BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(4));
            int selection = BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(62));
            return (weight is > 0 and <= 1000 ? weight : 400, (selection & 0x201) != 0 || slanted);
        }
        return (face.FontWeight, slanted);
    }

    internal static uint Tag(string t) => (uint)(t[0] << 24 | t[1] << 16 | t[2] << 8 | t[3]);

    /// <summary>
    /// A font table, or null when the font has none (some macOS system fonts lack OS/2 or post; Skia's GetTableData
    /// throws for a missing table, which stopped every layout that fell back to such a font — CI 2026-11-04).
    /// </summary>
    private static byte[]? Table(SKTypeface face, string tag) => face.TryGetTableData(Tag(tag), out var data) ? data : null;
}

/// <summary>
/// Font choice for the Skia engine (roadmap P2 S1): the family a run asks for when it is installed, otherwise the
/// platform's Korean UI font; per character, that Korean font and then any installed font with the character.
/// </summary>
public static class SkiaFonts
{
    private static readonly SKFontManager Manager = SKFontManager.Default;

    /// <summary>
    /// Preferred families for text whose family is not installed, per platform: Malgun Gothic on Windows, Apple SD Gothic
    /// Neo on macOS, Noto Sans CJK KR elsewhere. The first installed one is used, else the platform default face.
    /// </summary>
    private static string[] FallbackCandidates =>
        OperatingSystem.IsWindows() ? ["Malgun Gothic", "Noto Sans CJK KR", "Noto Sans KR"]
        : OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? ["Apple SD Gothic Neo", "Noto Sans CJK KR", "Noto Sans KR"]
        : ["Noto Sans CJK KR", "Noto Sans KR", "NanumGothic", "Malgun Gothic"];

    private static readonly Lazy<string> Fallback = new(() =>
        FallbackCandidates.FirstOrDefault(Available) ?? SKTypeface.Default.FamilyName);

    /// <summary>The family used for a run whose family is not installed (and first for characters a font lacks).</summary>
    public static string FallbackFamily => Fallback.Value;

    private static readonly ConcurrentDictionary<string, bool> Installed = new(StringComparer.OrdinalIgnoreCase);

    // Typefaces are not disposed: Skia may hand the same managed object to every caller of a face.
    public static bool Available(string family) => !string.IsNullOrWhiteSpace(family) &&
        Installed.GetOrAdd(family, name => Manager.MatchFamily(name) is not null);

    private static readonly Lazy<string[]> FamilyNames = new(() => [.. Manager.FontFamilies
        .Select(name => Manager.MatchFamily(name)?.FamilyName is { Length: > 0 } english ? english : name)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)]);

    /// <summary>
    /// The installed families, sorted. Windows lists some under their localized names (맑은 고딕); each is given by the
    /// name its typeface reports (Malgun Gothic), which is also what <see cref="SkiaFontFace.Family"/> says.
    /// </summary>
    public static IReadOnlyList<string> Families() => FamilyNames.Value;

    private static readonly ConcurrentDictionary<(string Family, bool Bold, bool Italic), SkiaFontFace> Styled = new();
    private static readonly ConcurrentDictionary<(string Family, int Weight, int Width, SKFontStyleSlant Slant), SkiaFontFace> ByTypeface = new();

    private static SKFontStyle Style(bool bold, bool italic) => new(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
        SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

    /// <summary>
    /// The face for a run: its family (or the fallback family) in the closest weight and slant the family really has.
    /// A face a platform would only simulate (DirectWrite slants Gulim for italic, but not in its glyph paths) is not
    /// taken: the engine draws from the real face and applies fake bold or slant itself, the same on screen and in PDF.
    /// </summary>
    public static SkiaFontFace Face(string family, bool bold, bool italic)
    {
        string resolved = Available(family) ? family : FallbackFamily;
        return Styled.GetOrAdd((resolved.ToLowerInvariant(), bold, italic), key =>
        {
            SkiaFontFace Match(bool b, bool i) => Wrap(Manager.MatchFamily(resolved, Style(b, i)) ?? SKTypeface.Default);
            var face = Match(key.Bold, key.Italic);
            if (key.Italic && !face.Italic) face = Match(key.Bold, false);
            if (key.Bold && face.Weight < 600) face = Match(false, face.Italic);
            return face;
        });
    }

    private static SkiaFontFace Wrap(SKTypeface typeface) =>
        ByTypeface.GetOrAdd((typeface.FamilyName, typeface.FontWeight, typeface.FontWidth, typeface.FontSlant), _ => new SkiaFontFace(typeface));

    private static readonly ConcurrentDictionary<(SkiaFontFace Face, int CodePoint, bool Bold, bool Italic), SkiaFontFace> ForCharacter = new();

    /// <summary>
    /// The face that draws <paramref name="codePoint"/> in a run of <paramref name="primary"/>: the primary face if it has
    /// the character, else the fallback family, else whatever installed font Skia finds for it. A character no font has
    /// stays with the primary face (it draws the face's missing-glyph box).
    /// </summary>
    public static SkiaFontFace FaceFor(SkiaFontFace primary, int codePoint, bool bold, bool italic)
    {
        if (codePoint < 0x20 || primary.HasGlyph(codePoint)) return primary;
        return ForCharacter.GetOrAdd((primary, codePoint, bold, italic), key =>
        {
            var korean = Face(FallbackFamily, key.Bold, key.Italic);
            if (korean.HasGlyph(key.CodePoint)) return korean;
            var found = Manager.MatchCharacter(key.Face.Family, Style(key.Bold, key.Italic), null, key.CodePoint);
            return found is null ? key.Face : Wrap(found);
        });
    }
}

/// <summary>Rewrites an OpenType font file without some of its tables (roadmap P2 S2: color glyph tables).</summary>
internal static class SfntTables
{
    private static readonly uint[] ColorTables = [Tag("COLR"), Tag("CPAL"), Tag("CBDT"), Tag("CBLC"), Tag("sbix"), Tag("SVG ")];

    private static uint Tag(string t) => SkiaFontFace.Tag(t);

    /// <summary>A typeface from the font file without color tables, or null when it has none (or cannot be read).</summary>
    public static SKTypeface? WithoutColor(SKTypeface typeface)
    {
        var tags = typeface.GetTableTags();
        if (!tags.Any(t => ColorTables.Contains(t))) return null;
        using var stream = typeface.OpenStream(out int index);
        if (stream is null || !stream.HasLength) return null;
        var data = new byte[stream.Length];
        if (stream.Read(data, data.Length) != data.Length) return null;
        var font = Strip(data, index, ColorTables);
        return font is null ? null : SKFontManager.Default.CreateTypeface(new MemoryStream(font));
    }

    /// <summary>
    /// The font at <paramref name="index"/> of a font file or collection, rebuilt with every table but
    /// <paramref name="drop"/>. Checksums are copied; renderers do not check them.
    /// </summary>
    public static byte[]? Strip(byte[] data, int index, uint[] drop)
    {
        int start = 0;
        if (data.Length >= 16 && BinaryPrimitives.ReadUInt32BigEndian(data) == Tag("ttcf"))
        {
            int count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
            if (index < 0 || index >= count) return null;
            start = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12 + 4 * index));
        }
        if (data.Length < start + 12) return null;
        uint version = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start));
        int tables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(start + 4));
        var keep = new List<(uint Tag, uint Sum, int Offset, int Length)>();
        for (int i = 0; i < tables; i++)
        {
            var record = data.AsSpan(start + 12 + 16 * i, 16);
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(record);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]), length = (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            if (offset < 0 || length < 0 || offset + (long)length > data.Length) return null;
            if (!drop.Contains(tag)) keep.Add((tag, BinaryPrimitives.ReadUInt32BigEndian(record[4..]), offset, length));
        }
        int head = 12 + 16 * keep.Count;
        int size = head + keep.Sum(k => (k.Length + 3) & ~3);
        var result = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(result, version);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), (ushort)keep.Count);
        int log = 0;
        while (2 << log <= keep.Count) log++;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6), (ushort)(16 << log));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(8), (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(10), (ushort)(keep.Count * 16 - (16 << log)));
        int at = head;
        for (int i = 0; i < keep.Count; i++)
        {
            var (tag, sum, offset, length) = keep[i];
            var record = result.AsSpan(12 + 16 * i, 16);
            BinaryPrimitives.WriteUInt32BigEndian(record, tag);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], sum);
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)length);
            data.AsSpan(offset, length).CopyTo(result.AsSpan(at));
            at += (length + 3) & ~3;
        }
        return result;
    }
}
