using System.Collections.Immutable;
using System.Globalization;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Writing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Pdf.Reading;

/// <summary>A standard annotation of another app turned into an app annotation of source page <see cref="SourcePageIndex"/> (canonical points of that page).</summary>
public sealed record ConvertedAnnotation(int SourcePageIndex, Annotation Annotation, string Subtype);

/// <summary>Result of <see cref="ForeignAnnotations.Convert"/>: the PDF without the converted annotations and what replaced them.</summary>
public sealed record ForeignConversion(byte[] Pdf, ImmutableArray<ConvertedAnnotation> Converted, ImmutableHashSet<string> RemovedKeys);

/// <summary>
/// Other apps' annotations made editable (spec 14.6, setting off by default). Ink, Highlight, Underline, StrikeOut (D13i),
/// Text notes (D13i), Line, FreeText, Square, Circle, Polygon and PolyLine become app annotations; everything else — and anything that fails to convert — keeps
/// its own appearance read-only. Converted annotations (and their popups) are removed from the PDF the app renders and
/// saves from, so after a save only the app's version remains. The conversion is approximate: pressure widths,
/// app-specific appearances, authors and replies are not kept.
/// </summary>
public static class ForeignAnnotations
{
    private const int FlagHidden = 2, FlagNoView = 32, FlagReadOnly = 64, FlagLocked = 128;

    public static ForeignConversion Convert(byte[] pdf, IReadOnlyList<PdfPageGeometry> pages)
    {
        using var stream = new MemoryStream(pdf, writable: false);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
        var converted = ImmutableArray.CreateBuilder<ConvertedAnnotation>();
        var keys = ImmutableHashSet.CreateBuilder<string>();
        for (int p = 0; p < document.PageCount && p < pages.Count; p++)
        {
            var annots = document.Pages[p].Elements.GetArray("/Annots");
            if (annots is null) continue;
            var remove = new List<PdfDictionary>();
            for (int i = 0; i < annots.Elements.Count; i++)
            {
                if (Resolve(annots.Elements[i]) is not { } annot || IsOwn(annot)) continue;
                int flags = annot.Elements.GetInteger("/F");
                if ((flags & (FlagHidden | FlagNoView)) != 0) continue;
                string subtype = annot.Elements.GetName("/Subtype");
                IReadOnlyList<Annotation> made;
                try { made = ConvertOne(annot, subtype, pages[p], (flags & (FlagReadOnly | FlagLocked)) != 0); }
                catch (Exception e) when (e is InvalidCastException or InvalidOperationException or ArgumentException or FormatException or NullReferenceException or IndexOutOfRangeException)
                { made = []; } // stays read-only with its own appearance
                if (made.Count == 0 || made.Any(a => ModelValidator.ValidateAnnotation(a, _ => false) is not null)) continue;
                foreach (var a in made) converted.Add(new(p, a, subtype));
                remove.Add(annot);
                keys.Add(Key(p, annot));
                if (annot.Elements.GetDictionary("/Popup") is { } popup) remove.Add(popup);
            }
            if (remove.Count == 0) continue;
            for (int i = annots.Elements.Count - 1; i >= 0; i--)
                if (Resolve(annots.Elements[i]) is { } a && remove.Any(r => ReferenceEquals(r, a))) annots.Elements.RemoveAt(i);
        }
        if (converted.Count == 0) return new(pdf, [], []);
        using var output = new MemoryStream();
        document.Save(output, false);
        return new(output.ToArray(), converted.ToImmutable(), keys.ToImmutable());
    }

    /// <summary>
    /// Removes the annotations converted from the same source (matched by <see cref="Key"/>) from another copy of it,
    /// such as the background the app renders without its own restored annotations.
    /// </summary>
    public static byte[] Remove(byte[] pdf, IReadOnlySet<string> keys)
    {
        if (keys.Count == 0) return pdf;
        using var stream = new MemoryStream(pdf, writable: false);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
        bool changed = false;
        for (int p = 0; p < document.PageCount; p++)
        {
            var annots = document.Pages[p].Elements.GetArray("/Annots");
            if (annots is null) continue;
            var popups = new List<PdfDictionary>();
            for (int i = annots.Elements.Count - 1; i >= 0; i--)
                if (Resolve(annots.Elements[i]) is { } a && !IsOwn(a) && keys.Contains(Key(p, a)))
                {
                    if (a.Elements.GetDictionary("/Popup") is { } popup) popups.Add(popup);
                    annots.Elements.RemoveAt(i); changed = true;
                }
            for (int i = annots.Elements.Count - 1; i >= 0; i--)
                if (Resolve(annots.Elements[i]) is { } a && popups.Any(x => ReferenceEquals(x, a))) annots.Elements.RemoveAt(i);
        }
        if (!changed) return pdf;
        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }

    /// <summary>Identity of an annotation that survives a re-save of the same file: page, subtype, name and rectangle.</summary>
    private static string Key(int page, PdfDictionary annot)
    {
        var rect = annot.Elements.GetArray("/Rect");
        string r = rect is null ? "" : string.Join(",", Enumerable.Range(0, rect.Elements.Count).Select(i => Math.Round(rect.Elements.GetReal(i), 3).ToString(CultureInfo.InvariantCulture)));
        return $"{page}|{annot.Elements.GetName("/Subtype")}|{annot.Elements.GetString("/NM")}|{r}";
    }

    private static bool IsOwn(PdfDictionary annot) =>
        (annot.Elements.GetString("/NM") ?? "").StartsWith(EditablePdfKeys.NamePrefix, StringComparison.Ordinal) || annot.Elements.ContainsKey(EditablePdfKeys.Envelope);

    private static PdfDictionary? Resolve(PdfItem? item) => item switch
    {
        PdfReference r => r.Value as PdfDictionary,
        PdfDictionary d => d,
        _ => null
    };

    private static IReadOnlyList<Annotation> ConvertOne(PdfDictionary a, string subtype, PdfPageGeometry page, bool locked)
    {
        var toCanonical = page.Box.PdfUserToCanonical;
        var frame = page.Frame;
        double opacity = a.Elements.ContainsKey("/CA") ? Math.Clamp(a.Elements.GetReal("/CA"), 0, 1) : 1;
        double width = BorderWidth(a);
        switch (subtype)
        {
            case "/Ink":
            {
                if (Color(a, "/C") is not { } color || a.Elements.GetArray("/InkList") is not { } list) return [];
                var strokes = new List<Annotation>();
                for (int i = 0; i < list.Elements.Count; i++)
                {
                    var points = Points(list.Elements[i] is PdfReference r ? r.Value as PdfArray : list.Elements[i] as PdfArray, toCanonical);
                    if (points.Length == 0) continue;
                    strokes.Add(new InkStroke(Guid.NewGuid(), locked, Affine.Identity, points, width > 0 ? width : 1, color, opacity));
                }
                return strokes;
            }
            case "/Highlight":
            {
                if (Color(a, "/C") is not { } color || a.Elements.GetArray("/QuadPoints") is not { } q || q.Elements.Count == 0 || q.Elements.Count % 8 != 0) return [];
                var quads = Enumerable.Range(0, q.Elements.Count / 8).Select(k =>
                {
                    Point2 P(int j) => toCanonical.Transform(new(q.Elements.GetReal(k * 8 + j * 2), q.Elements.GetReal(k * 8 + j * 2 + 1)));
                    return new Quad(P(0), P(1), P(2), P(3)); // the order every viewer writes: upper-left, upper-right, lower-left, lower-right
                }).ToImmutableArray();
                return [new PdfTextHighlight(Guid.NewGuid(), locked, quads, null, HighlightHeightMode.Auto, null, color, opacity)];
            }
            case "/Underline" or "/StrikeOut":
            {
                // Spec 24.8 (D13i): standard text markups become app underline/strikeout on the same glyph quads.
                if (Color(a, "/C") is not { } color || a.Elements.GetArray("/QuadPoints") is not { } q || q.Elements.Count == 0 || q.Elements.Count % 8 != 0) return [];
                var quads = Enumerable.Range(0, q.Elements.Count / 8).Select(k =>
                {
                    Point2 P(int j) => toCanonical.Transform(new(q.Elements.GetReal(k * 8 + j * 2), q.Elements.GetReal(k * 8 + j * 2 + 1)));
                    return new Quad(P(0), P(1), P(2), P(3));
                }).ToImmutableArray();
                double lineWidth = Math.Clamp(BorderWidth(a) is var w && w > 0 ? w : 1, ModelValidator.MinMarkupWidthPt, ModelValidator.MaxMarkupWidthPt);
                return [new PdfTextMarkup(Guid.NewGuid(), locked, quads, null, subtype == "/Underline" ? TextMarkupKind.Underline : TextMarkupKind.StrikeOut, lineWidth, color, opacity)];
            }
            case "/Text":
            {
                // Spec 24.9 (D13i): a sticky note with text becomes an app note at the icon's upper-left corner.
                string text = (a.Elements.GetString("/Contents") ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
                if (string.IsNullOrWhiteSpace(text) || a.Elements.GetArray("/Rect") is not { Elements.Count: 4 } r) return [];
                double left = Math.Min(r.Elements.GetReal(0), r.Elements.GetReal(2)), top = Math.Max(r.Elements.GetReal(1), r.Elements.GetReal(3));
                var corner = toCanonical.Transform(new Point2(left, top));
                if (!corner.IsFinite) return [];
                return [new StickyNote(Guid.NewGuid(), locked, Affine.Translation(corner.X, corner.Y), text, Color(a, "/C") ?? new RgbColor(0xFF, 0xD5, 0x4F))];
            }
            case "/Line":
            {
                if (Color(a, "/C") is not { } color || a.Elements.GetArray("/L") is not { Elements.Count: 4 } l) return [];
                var start = toCanonical.Transform(new(l.Elements.GetReal(0), l.Elements.GetReal(1)));
                var end = toCanonical.Transform(new(l.Elements.GetReal(2), l.Elements.GetReal(3)));
                var ends = a.Elements.GetArray("/LE");
                bool Arrow(int i) => ends is { Elements.Count: 2 } && ends.Elements.GetName(i) is "/OpenArrow" or "/ClosedArrow";
                return [new LineAnnotation(Guid.NewGuid(), locked, Affine.Identity, LineKind.Straight, start, end, [], width > 0 ? width : 1,
                    Dashed(a) ? DashStyle.Dash : DashStyle.Solid, Arrow(0), Arrow(1), color, opacity)];
            }
            case "/FreeText":
            {
                string text = (a.Elements.GetString("/Contents") ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
                if (string.IsNullOrWhiteSpace(text) || DisplayRect(a, toCanonical, frame) is not { } rect) return [];
                var (size, color) = DefaultAppearance(a.Elements.GetString("/DA"));
                var run = Core.Commands.TextEditing.Default with { Start = 0, Length = text.Length, SizePt = size, Color = color };
                var align = a.Elements.GetInteger("/Q") switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, _ => TextAlignment.Left };
                // Upright on the shown page like a typed box: the local frame is the display frame (as for shapes).
                var transform = Affine.Translation(rect.X, rect.Y).Then(frame.DisplayToCanonical);
                return [new TextBoxAnnotation(Guid.NewGuid(), locked, transform, text, [run], align, Math.Max(16, rect.Width), TextBoxStyle.Default, [])];
            }
            case "/Square" or "/Circle":
            {
                var border = Color(a, "/C"); var fill = Color(a, "/IC");
                if (border is null && fill is null || DisplayRect(a, toCanonical, frame) is not { } rect) return [];
                // The border is drawn inside /Rect (minus /RD): the outline runs half a border width inside.
                double inset = width / 2;
                if (a.Elements.GetArray("/RD") is { Elements.Count: 4 } rd) inset += Math.Max(Math.Max(rd.Elements.GetReal(0), rd.Elements.GetReal(1)), Math.Max(rd.Elements.GetReal(2), rd.Elements.GetReal(3)));
                var box = new Rect2(rect.X + inset, rect.Y + inset, rect.Width - 2 * inset, rect.Height - 2 * inset);
                if (box.Width <= 0 || box.Height <= 0) return [];
                return [new ShapeAnnotation(Guid.NewGuid(), locked, frame.DisplayToCanonical, subtype == "/Square" ? ShapeKind.Rectangle : ShapeKind.Ellipse, box, [],
                    Style(fill, border, width, opacity, a))];
            }
            case "/Polygon" or "/PolyLine":
            {
                var border = Color(a, "/C"); var fill = subtype == "/Polygon" ? Color(a, "/IC") : null;
                var points = Points(a.Elements.GetArray("/Vertices"), toCanonical);
                if (border is null && fill is null) return [];
                return [new ShapeAnnotation(Guid.NewGuid(), locked, Affine.Identity, subtype == "/Polygon" ? ShapeKind.Polygon : ShapeKind.Polyline, default, points,
                    Style(fill, border, width, opacity, a))];
            }
            default:
                return [];
        }
    }

    private static ShapeStyle Style(RgbColor? fill, RgbColor? border, double width, double opacity, PdfDictionary a) =>
        new(fill ?? RgbColor.White, fill is null ? 0 : opacity, border is not null && width > 0, border ?? RgbColor.Black, width > 0 ? width : 1, opacity,
            Dashed(a) ? DashStyle.Dash : DashStyle.Solid);

    /// <summary>/BS /W, else the /Border array's width, else 1 (PDF default).</summary>
    private static double BorderWidth(PdfDictionary a)
    {
        if (a.Elements.GetDictionary("/BS") is { } bs && bs.Elements.ContainsKey("/W")) return Math.Max(0, bs.Elements.GetReal("/W"));
        if (a.Elements.GetArray("/Border") is { Elements.Count: >= 3 } border) return Math.Max(0, border.Elements.GetReal(2));
        return 1;
    }

    private static bool Dashed(PdfDictionary a) => a.Elements.GetDictionary("/BS")?.Elements.GetName("/S") == "/D";

    /// <summary>DeviceGray, DeviceRGB or DeviceCMYK color array; null for none (transparent).</summary>
    private static RgbColor? Color(PdfDictionary a, string key)
    {
        if (a.Elements.GetArray(key) is not { } c) return null;
        double V(int i) => Math.Clamp(c.Elements.GetReal(i), 0, 1);
        byte B(double v) => (byte)Math.Round(v * 255);
        return c.Elements.Count switch
        {
            1 => new RgbColor(B(V(0)), B(V(0)), B(V(0))),
            3 => new RgbColor(B(V(0)), B(V(1)), B(V(2))),
            4 => new RgbColor(B((1 - V(0)) * (1 - V(3))), B((1 - V(1)) * (1 - V(3))), B((1 - V(2)) * (1 - V(3)))),
            _ => null
        };
    }

    private static ImmutableArray<Point2> Points(PdfArray? flat, Affine toCanonical)
    {
        if (flat is null || flat.Elements.Count < 2 || flat.Elements.Count % 2 != 0) return [];
        return Enumerable.Range(0, flat.Elements.Count / 2)
            .Select(i => toCanonical.Transform(new(flat.Elements.GetReal(i * 2), flat.Elements.GetReal(i * 2 + 1)))).ToImmutableArray();
    }

    /// <summary>/Rect in display coordinates of the page (top-left origin after /Rotate).</summary>
    private static Rect2? DisplayRect(PdfDictionary a, Affine toCanonical, PageFrame frame)
    {
        if (a.Elements.GetArray("/Rect") is not { Elements.Count: 4 } r) return null;
        var corners = new[] { new Point2(r.Elements.GetReal(0), r.Elements.GetReal(1)), new Point2(r.Elements.GetReal(2), r.Elements.GetReal(3)) }
            .Select(p => frame.CanonicalToDisplay.Transform(toCanonical.Transform(p))).ToArray();
        double x0 = Math.Min(corners[0].X, corners[1].X), y0 = Math.Min(corners[0].Y, corners[1].Y);
        var rect = new Rect2(x0, y0, Math.Abs(corners[1].X - corners[0].X), Math.Abs(corners[1].Y - corners[0].Y));
        return rect.IsFinite && rect.Width > 0 && rect.Height > 0 ? rect : null;
    }

    /// <summary>Font size and fill color of a /DA string such as "0 0 1 rg /Helv 12 Tf" (defaults 12pt black).</summary>
    private static (double Size, RgbColor Color) DefaultAppearance(string? da)
    {
        double size = 12; var color = RgbColor.Black;
        var tokens = (da ?? "").Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        double Num(int i) => double.Parse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture);
        for (int i = 0; i < tokens.Length; i++)
        {
            try
            {
                if (tokens[i] == "Tf" && i >= 1 && Num(i - 1) > 0) size = Num(i - 1);
                else if (tokens[i] == "rg" && i >= 3) color = new((byte)Math.Round(Math.Clamp(Num(i - 3), 0, 1) * 255), (byte)Math.Round(Math.Clamp(Num(i - 2), 0, 1) * 255), (byte)Math.Round(Math.Clamp(Num(i - 1), 0, 1) * 255));
                else if (tokens[i] == "g" && i >= 1) { byte v = (byte)Math.Round(Math.Clamp(Num(i - 1), 0, 1) * 255); color = new(v, v, v); }
            }
            catch (FormatException) { }
        }
        return (Math.Clamp(size, InputLimits.MinFontSizePt, InputLimits.MaxFontSizePt), color);
    }
}
