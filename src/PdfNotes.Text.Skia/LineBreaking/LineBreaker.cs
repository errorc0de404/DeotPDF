using System.Globalization;
using System.Text;

namespace PdfNotes.Text.LineBreaking;

/// <summary>What may happen at a position between two characters.</summary>
public enum LineBreak : byte
{
    /// <summary>No break here (inside a word, a number, a combining sequence or a surrogate pair).</summary>
    None,
    /// <summary>A line may end here.</summary>
    Allowed,
    /// <summary>A line must end here (after a line feed, carriage return and the like).</summary>
    Mandatory,
}

/// <summary>
/// The Unicode line breaking algorithm (UAX #14, the default rules without tailoring) over the data in
/// <see cref="LineBreakData"/>. Classes and rules follow the version in <see cref="LineBreakData.UnicodeVersion"/>;
/// General_Category (for SA, Pi, Pf and unassigned code points) comes from .NET.
/// </summary>
public static class LineBreaker
{
    public static string UnicodeVersion => LineBreakData.UnicodeVersion;

    /// <summary>
    /// The break before each UTF-16 index of <paramref name="text"/>: one entry per index and one for the end
    /// (<c>result[text.Length]</c>, always <see cref="LineBreak.Mandatory"/>). Index 0 and the second half of a surrogate
    /// pair are <see cref="LineBreak.None"/>.
    /// </summary>
    public static LineBreak[] Breaks(string text)
    {
        var result = new LineBreak[text.Length + 1];
        result[text.Length] = LineBreak.Mandatory;
        if (text.Length == 0) return result;

        // Code points with their resolved classes (LB1) and properties.
        int n = 0;
        var starts = new int[text.Length];
        var raw = new LineBreakClass[text.Length];
        var points = new int[text.Length];
        for (int i = 0; i < text.Length; n++)
        {
            var (rune, length) = TextRunes.At(text, i);
            starts[n] = i; points[n] = rune.Value; raw[n] = Resolve(rune);
            i += length;
        }

        // LB9/LB10: a base and the combining marks or joiners after it form one unit with the base's class and
        // properties; a mark without a base is a unit of its own, treated as the letter A.
        var units = new Unit[n];
        var unitOf = new int[n];
        var absorbed = new bool[n];
        int count = 0;
        for (int k = 0; k < n; k++)
        {
            var c = raw[k];
            bool mark = c is LineBreakClass.CM or LineBreakClass.ZWJ;
            if (mark && count > 0 && units[count - 1].Base is not (LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF
                or LineBreakClass.NL or LineBreakClass.SP or LineBreakClass.ZW))
            {
                absorbed[k] = true;
                unitOf[k] = count - 1;
                continue;
            }
            unitOf[k] = count;
            units[count++] = mark ? new Unit(LineBreakClass.AL, c, 'A', false, false, false, false, false)
                : new Unit(c, c, points[k], EastAsian(points[k]), Pictographic(points[k]), Category(points[k]) == UnicodeCategory.InitialQuotePunctuation,
                    Category(points[k]) == UnicodeCategory.FinalQuotePunctuation, Category(points[k]) == UnicodeCategory.OtherNotAssigned);
        }

        for (int k = 1; k < n; k++)
            result[starts[k]] = Decide(raw, absorbed, k, units, count, unitOf[k - 1], unitOf[k]);
        return result;
    }

    private readonly record struct Unit(LineBreakClass Class, LineBreakClass Base, int Point, bool EastAsian, bool Pictographic, bool Initial, bool Final, bool Unassigned);

    /// <summary>The break between code points <paramref name="k"/> − 1 and <paramref name="k"/>.</summary>
    private static LineBreak Decide(LineBreakClass[] raw, bool[] absorbed, int k, Unit[] u, int count, int a, int b)
    {
        var before = raw[k - 1];
        var after = raw[k];
        // LB4–LB6: hard line breaks.
        if (before == LineBreakClass.BK) return LineBreak.Mandatory;
        if (before == LineBreakClass.CR && after == LineBreakClass.LF) return LineBreak.None;
        if (before is LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL) return LineBreak.Mandatory;
        if (after is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL) return LineBreak.None;
        // LB7, LB8, LB8a.
        if (after is LineBreakClass.SP or LineBreakClass.ZW) return LineBreak.None;
        int z = k - 1;
        while (z > 0 && raw[z] == LineBreakClass.SP) z--;
        if (raw[z] == LineBreakClass.ZW) return LineBreak.Allowed;
        if (before == LineBreakClass.ZWJ) return LineBreak.None;
        // LB9: inside a combining sequence.
        if (absorbed[k]) return LineBreak.None;

        var A = u[a].Class;
        var B = u[b].Class;
        LineBreakClass At(int i) => i >= 0 && i < count ? u[i].Class : LineBreakClass.XX;
        int SkipSpaces(int i) { while (i >= 0 && u[i].Class == LineBreakClass.SP) i--; return i; }

        // LB11, LB12, LB12a.
        if (A == LineBreakClass.WJ || B == LineBreakClass.WJ) return LineBreak.None;
        if (A == LineBreakClass.GL) return LineBreak.None;
        if (B == LineBreakClass.GL && A is not (LineBreakClass.SP or LineBreakClass.HY or LineBreakClass.HH)) return LineBreak.None;
        // LB13, LB14.
        if (B is LineBreakClass.CL or LineBreakClass.CP or LineBreakClass.EX or LineBreakClass.SY) return LineBreak.None;
        int s = SkipSpaces(a);
        if (At(s) == LineBreakClass.OP) return LineBreak.None;
        // LB15a: an initial quotation mark at the start, after a space or an opening, keeps what follows.
        if (s >= 0 && u[s].Class == LineBreakClass.QU && u[s].Initial && (s == 0 || At(s - 1) is LineBreakClass.BK or LineBreakClass.CR
            or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.OP or LineBreakClass.QU or LineBreakClass.GL or LineBreakClass.SP
            or LineBreakClass.ZW)) return LineBreak.None;
        // LB15b: a final quotation mark before a space, a prohibited break or the end stays with what precedes it.
        if (B == LineBreakClass.QU && u[b].Final && (b + 1 == count || At(b + 1) is LineBreakClass.SP or LineBreakClass.GL or LineBreakClass.WJ
            or LineBreakClass.CL or LineBreakClass.QU or LineBreakClass.CP or LineBreakClass.EX or LineBreakClass.IS or LineBreakClass.SY
            or LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.ZW)) return LineBreak.None;
        // LB15c, LB15d.
        if (A == LineBreakClass.SP && B == LineBreakClass.IS && At(b + 1) == LineBreakClass.NU) return LineBreak.Allowed;
        if (B == LineBreakClass.IS) return LineBreak.None;
        // LB16, LB17.
        if (B == LineBreakClass.NS && At(s) is LineBreakClass.CL or LineBreakClass.CP) return LineBreak.None;
        if (B == LineBreakClass.B2 && At(s) == LineBreakClass.B2) return LineBreak.None;
        // LB18.
        if (A == LineBreakClass.SP) return LineBreak.Allowed;
        // LB19, LB19a: quotation marks.
        if (B == LineBreakClass.QU && !u[b].Initial) return LineBreak.None;
        if (A == LineBreakClass.QU && !u[a].Final) return LineBreak.None;
        if (B == LineBreakClass.QU && (!u[a].EastAsian || b + 1 == count || !u[b + 1].EastAsian)) return LineBreak.None;
        if (A == LineBreakClass.QU && (!u[b].EastAsian || a == 0 || !u[a - 1].EastAsian)) return LineBreak.None;
        // LB20, LB20a.
        if (A == LineBreakClass.CB || B == LineBreakClass.CB) return LineBreak.Allowed;
        if (A is LineBreakClass.HY or LineBreakClass.HH && B is LineBreakClass.AL or LineBreakClass.HL && (a == 0 || At(a - 1) is LineBreakClass.BK
            or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.SP or LineBreakClass.ZW or LineBreakClass.CB
            or LineBreakClass.GL)) return LineBreak.None;
        // LB21, LB21a, LB21b, LB22.
        if (B is LineBreakClass.BA or LineBreakClass.HH or LineBreakClass.HY or LineBreakClass.NS) return LineBreak.None;
        if (A == LineBreakClass.BB) return LineBreak.None;
        if (A is LineBreakClass.HY or LineBreakClass.HH && At(a - 1) == LineBreakClass.HL && B != LineBreakClass.HL) return LineBreak.None;
        if (A == LineBreakClass.SY && B == LineBreakClass.HL) return LineBreak.None;
        if (B == LineBreakClass.IN) return LineBreak.None;
        // LB23, LB23a, LB24: letters, digits, prefixes and postfixes.
        bool letterA = A is LineBreakClass.AL or LineBreakClass.HL, letterB = B is LineBreakClass.AL or LineBreakClass.HL;
        if (letterA && B == LineBreakClass.NU || A == LineBreakClass.NU && letterB) return LineBreak.None;
        if (A == LineBreakClass.PR && B is LineBreakClass.ID or LineBreakClass.EB or LineBreakClass.EM) return LineBreak.None;
        if (A is LineBreakClass.ID or LineBreakClass.EB or LineBreakClass.EM && B == LineBreakClass.PO) return LineBreak.None;
        if (A is LineBreakClass.PR or LineBreakClass.PO && letterB || letterA && B is LineBreakClass.PR or LineBreakClass.PO) return LineBreak.None;
        // LB25: numbers.
        if (B is LineBreakClass.PO or LineBreakClass.PR)
        {
            int i = A is LineBreakClass.CL or LineBreakClass.CP ? a - 1 : a;
            while (At(i) is LineBreakClass.SY or LineBreakClass.IS) i--;
            if (At(i) == LineBreakClass.NU) return LineBreak.None;
        }
        if (A is LineBreakClass.PO or LineBreakClass.PR)
        {
            if (B == LineBreakClass.NU) return LineBreak.None;
            if (B == LineBreakClass.OP && (At(b + 1) == LineBreakClass.NU || At(b + 1) == LineBreakClass.IS && At(b + 2) == LineBreakClass.NU)) return LineBreak.None;
        }
        if (A is LineBreakClass.HY or LineBreakClass.IS && B == LineBreakClass.NU) return LineBreak.None;
        if (B == LineBreakClass.NU)
        {
            int i = a;
            while (At(i) is LineBreakClass.SY or LineBreakClass.IS) i--;
            if (At(i) == LineBreakClass.NU) return LineBreak.None;
        }
        // LB26, LB27: Korean syllable blocks.
        if (A == LineBreakClass.JL && B is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.H2 or LineBreakClass.H3) return LineBreak.None;
        if (A is LineBreakClass.JV or LineBreakClass.H2 && B is LineBreakClass.JV or LineBreakClass.JT) return LineBreak.None;
        if (A is LineBreakClass.JT or LineBreakClass.H3 && B == LineBreakClass.JT) return LineBreak.None;
        bool koreanA = A is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT or LineBreakClass.H2 or LineBreakClass.H3;
        if (koreanA && B == LineBreakClass.PO) return LineBreak.None;
        if (A == LineBreakClass.PR && B is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT or LineBreakClass.H2 or LineBreakClass.H3) return LineBreak.None;
        // LB28, LB28a.
        if (letterA && letterB) return LineBreak.None;
        bool Aksara(int i) => i >= 0 && i < count && (u[i].Class is LineBreakClass.AK or LineBreakClass.AS || u[i].Point == 0x25CC);
        if (A == LineBreakClass.AP && (B is LineBreakClass.AK or LineBreakClass.AS || u[b].Point == 0x25CC)) return LineBreak.None;
        if (Aksara(a) && B is LineBreakClass.VF or LineBreakClass.VI) return LineBreak.None;
        if (A == LineBreakClass.VI && Aksara(a - 1) && (B == LineBreakClass.AK || u[b].Point == 0x25CC)) return LineBreak.None;
        if (Aksara(a) && Aksara(b) && At(b + 1) == LineBreakClass.VF) return LineBreak.None;
        // LB29, LB30.
        if (A == LineBreakClass.IS && letterB) return LineBreak.None;
        if (A is LineBreakClass.AL or LineBreakClass.HL or LineBreakClass.NU && B == LineBreakClass.OP && !u[b].EastAsian) return LineBreak.None;
        if (A == LineBreakClass.CP && !u[a].EastAsian && B is LineBreakClass.AL or LineBreakClass.HL or LineBreakClass.NU) return LineBreak.None;
        // LB30a: regional indicators pair up.
        if (A == LineBreakClass.RI && B == LineBreakClass.RI)
        {
            int run = 0;
            for (int i = a; At(i) == LineBreakClass.RI; i--) run++;
            if (run % 2 == 1) return LineBreak.None;
        }
        // LB30b.
        if (B == LineBreakClass.EM && (A == LineBreakClass.EB || u[a].Pictographic && u[a].Unassigned)) return LineBreak.None;
        return LineBreak.Allowed; // LB31
    }

    // ----- character data -----

    private static readonly LineBreakClass[] Ascii = [.. Enumerable.Range(0, 128).Select(Lookup)];

    /// <summary>The Line_Break property of a code point (before LB1).</summary>
    public static LineBreakClass ClassOf(int point) => point < 128 ? Ascii[point] : Lookup(point);

    private static LineBreakClass Lookup(int point)
    {
        if (point is >= 0xAC00 and <= 0xD7A3) return (point - 0xAC00) % 28 == 0 ? LineBreakClass.H2 : LineBreakClass.H3;
        var starts = LineBreakData.Starts;
        int lo = 0, hi = starts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (starts[mid] <= point) lo = mid; else hi = mid - 1;
        }
        return (LineBreakClass)LineBreakData.Classes[lo];
    }

    /// <summary>LB1 without tailoring: AI, SG and XX become AL; SA becomes CM for marks and AL otherwise; CJ becomes NS.</summary>
    private static LineBreakClass Resolve(Rune rune)
    {
        var c = ClassOf(rune.Value);
        return c switch
        {
            LineBreakClass.AI or LineBreakClass.SG or LineBreakClass.XX => LineBreakClass.AL,
            LineBreakClass.SA => Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                ? LineBreakClass.CM : LineBreakClass.AL,
            LineBreakClass.CJ => LineBreakClass.NS,
            _ => c,
        };
    }

    private static UnicodeCategory Category(int point) => Rune.IsValid(point) ? Rune.GetUnicodeCategory(new Rune(point)) : UnicodeCategory.Surrogate;

    /// <summary>East_Asian_Width is F, W or H.</summary>
    public static bool EastAsian(int point) => InFlagRuns(LineBreakData.EastAsian, point);

    public static bool Pictographic(int point) => InFlagRuns(LineBreakData.Pictographic, point);

    // The table lists where a flag turns on and off, starting off; the flag is on after an odd number of boundaries.
    private static bool InFlagRuns(ReadOnlySpan<int> boundaries, int point)
    {
        int lo = 0, hi = boundaries.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (boundaries[mid] <= point) lo = mid + 1; else hi = mid;
        }
        return (lo & 1) == 1;
    }
}
