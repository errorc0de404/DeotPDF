using PdfNotes.Core.Geometry;

namespace PdfNotes.Core.Models;

public enum PaperSize { A4, A5, Letter, Custom }

public enum PageOrientation { Portrait, Landscape }

/// <summary>
/// Size and background chosen in the new note / new page dialog (spec 4.1, 11.5). Sizes are entered in mm;
/// generated pages are stored as canonical points with rotation 0.
/// </summary>
public sealed record NewPageSpec(double WidthMm, double HeightMm, PageBackground Background)
{
    /// <summary>Spec 4.1 default: A4 portrait, blank.</summary>
    public static NewPageSpec Default { get; } = FromPreset(PaperSize.A4, PageOrientation.Portrait, PageBackground.Blank);

    /// <summary>Portrait width × height in mm. Letter is 8.5 × 11 in.</summary>
    public static (double WidthMm, double HeightMm) PresetSizeMm(PaperSize size) => size switch
    {
        PaperSize.A4 => (210, 297),
        PaperSize.A5 => (148, 210),
        PaperSize.Letter => (215.9, 279.4),
        _ => throw new ArgumentOutOfRangeException(nameof(size), "Custom has no preset size.")
    };

    public static NewPageSpec FromPreset(PaperSize size, PageOrientation orientation, PageBackground background)
    {
        var (w, h) = PresetSizeMm(size);
        return orientation == PageOrientation.Landscape ? new(h, w, background) : new(w, h, background);
    }

    /// <summary>Returns null when valid, otherwise the language pack key of the message (placeholders <c>{min}</c>, <c>{max}</c>).</summary>
    public string? Validate()
    {
        if (!InRange(WidthMm) || !InRange(HeightMm))
            return "newPage.sizeRange";
        if (Background is null || !Enum.IsDefined(Background.Kind)) return "newPage.badBackground";
        return null;
    }

    public PageSetup ToPageSetup()
    {
        if (Validate() is { } error) throw new ArgumentException(error);
        return new(Units.MillimetersToPoints(WidthMm), Units.MillimetersToPoints(HeightMm), 0, PageSource.Generated, Background);
    }

    /// <summary>A new one-page note document (spec 4.1).</summary>
    public DocumentModel CreateDocument() => DocumentModel.Create([ToPageSetup()]);

    private static bool InRange(double mm) =>
        double.IsFinite(mm) && mm >= InputLimits.MinNewPageSideMm && mm <= InputLimits.MaxNewPageSideMm;
}
