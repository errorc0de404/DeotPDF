using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using PdfNotes.Core.Geometry;

namespace PdfNotes.Core.Models;

/// <summary>sRGB color without alpha. Opacity is stored separately as 0..1.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Black { get; } = new(0, 0, 0);
    public static RgbColor White { get; } = new(255, 255, 255);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static bool TryParse(string? text, out RgbColor color)
    {
        color = default;
        if (text is not { Length: 7 } || text[0] != '#' ||
            !int.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value))
            return false;
        color = new((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public override string ToString() => ToHex();
}

public static class Opacity
{
    /// <summary>UI transparency T (0 = opaque, 100 = invisible) to stored opacity 1 - T/100.</summary>
    public static double FromTransparencyPercent(double transparency)
    {
        if (!double.IsFinite(transparency) || transparency is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(transparency));
        return 1 - transparency / 100;
    }

    public static double ToTransparencyPercent(double opacity)
    {
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
        return (1 - opacity) * 100;
    }
}

public enum PageSourceKind { Pdf, Generated }

/// <summary>Where the page background comes from. Pdf pages reference a page index in the opened source PDF.</summary>
public sealed record PageSource(PageSourceKind Kind, int? PdfPageIndex)
{
    /// <summary>
    /// The page shows its source without the other apps' annotations that the 다른 프로그램 주석 편집 command turned into
    /// app annotations (spec 14.6, D14g). Session state only: never serialized — a save writes that converted source, so a
    /// reopened file has nothing left to convert. Part of the render key, so Undo brings the originals back on screen.
    /// </summary>
    public bool ForeignConverted { get; init; }

    public static PageSource Generated { get; } = new(PageSourceKind.Generated, null);

    public static PageSource Pdf(int pageIndex) => pageIndex >= 0
        ? new(PageSourceKind.Pdf, pageIndex)
        : throw new ArgumentOutOfRangeException(nameof(pageIndex));
}

public enum PageBackgroundKind { Blank, Grid, Lined }

/// <summary>Generated note background. Spec 4.1: white, 5mm grid or 8mm lines in light gray.</summary>
public sealed record PageBackground(PageBackgroundKind Kind, double SpacingMm, RgbColor LineColor)
{
    public static readonly RgbColor DefaultLineColor = new(0xD0, 0xD0, 0xD0);
    public static PageBackground Blank { get; } = new(PageBackgroundKind.Blank, 0, DefaultLineColor);
    public static PageBackground Grid { get; } = new(PageBackgroundKind.Grid, 5, DefaultLineColor);
    public static PageBackground Lined { get; } = new(PageBackgroundKind.Lined, 8, DefaultLineColor);
}

/// <summary>Page properties except annotations. Width/height are canonical points (unrotated CropBox × UserUnit).</summary>
public sealed record PageSetup(double WidthPt, double HeightPt, int Rotation, PageSource Source, PageBackground Background)
{
    /// <summary>Original visible source rectangle and its cumulative placement in current canonical points (v4).</summary>
    public SourcePlacement? Content { get; init; }
    public PageFrame Frame => new(WidthPt, HeightPt, Rotation);
}

public sealed record SourcePlacement(double WidthPt, double HeightPt, Affine Transform);

/// <summary>A page. The annotation list order is the stacking order: index 0 is the bottom-most annotation.</summary>
public sealed record Page(Guid Id, PageSetup Setup, ImmutableList<Annotation> Annotations)
{
    public static Page Create(PageSetup setup) => new(Guid.NewGuid(), setup, ImmutableList<Annotation>.Empty);

    public int IndexOf(Guid annotationId)
    {
        for (int i = 0; i < Annotations.Count; i++)
            if (Annotations[i].Id == annotationId) return i;
        return -1;
    }
}

/// <summary>
/// Immutable binary resource (image original). The payload array is never mutated after creation, so snapshots
/// and history entries share it instead of copying it.
/// </summary>
public sealed record Resource(Guid Id, string MediaType, ImmutableArray<byte> Payload, string Sha256)
{
    public static Resource Create(Guid id, string mediaType, ReadOnlySpan<byte> payload) =>
        new(id, mediaType, ImmutableArray.Create(payload), ComputeSha256(payload));

    public static string ComputeSha256(ReadOnlySpan<byte> payload) => Convert.ToHexString(SHA256.HashData(payload));
}

public sealed record AnnotationLocation(int PageIndex, Page Page, int AnnotationIndex, Annotation Annotation);

/// <summary>
/// The single editable document state. Immutable: every edit produces a new root that shares unchanged pages,
/// annotations and resources with the previous root.
/// </summary>
public sealed record DocumentModel(Guid Id, ImmutableList<Page> Pages, ImmutableDictionary<Guid, Resource> Resources)
{
    public static DocumentModel Create(IEnumerable<PageSetup> pages) => new(Guid.NewGuid(),
        pages.Select(Page.Create).ToImmutableList(), ImmutableDictionary<Guid, Resource>.Empty);

    /// <summary>
    /// Tags by annotation ID (schema v7, <see cref="AnnotationTag"/>). Entries of annotations that no longer exist are
    /// harmless and never saved, so a deletion needs no tag edit and its Undo brings the tag back with the annotation.
    /// </summary>
    public ImmutableDictionary<Guid, AnnotationTag> Tags { get; init; } = ImmutableDictionary<Guid, AnnotationTag>.Empty;

    /// <summary>The tag of an annotation that exists in this document, or null.</summary>
    public AnnotationTag? TagOf(Guid annotationId) => Tags.TryGetValue(annotationId, out var tag) ? tag : null;

    /// <summary>Tags of the annotations that exist, in page and stacking order.</summary>
    public IEnumerable<(Annotation Annotation, AnnotationTag Tag)> LiveTags =>
        Tags.IsEmpty ? [] : AllAnnotations.Where(a => Tags.ContainsKey(a.Id)).Select(a => (a, Tags[a.Id]));

    public int IndexOfPage(Guid pageId)
    {
        for (int i = 0; i < Pages.Count; i++)
            if (Pages[i].Id == pageId) return i;
        return -1;
    }

    public Page GetPage(Guid pageId)
    {
        int index = IndexOfPage(pageId);
        return index >= 0 ? Pages[index] : throw new KeyNotFoundException($"Page {pageId} does not exist.");
    }

    public AnnotationLocation? FindAnnotation(Guid annotationId)
    {
        for (int p = 0; p < Pages.Count; p++)
        {
            int index = Pages[p].IndexOf(annotationId);
            if (index >= 0) return new(p, Pages[p], index, Pages[p].Annotations[index]);
        }
        return null;
    }

    public IEnumerable<Annotation> AllAnnotations => Pages.SelectMany(p => p.Annotations);

    public bool IsResourceReferenced(Guid resourceId) =>
        AllAnnotations.OfType<ImageAnnotation>().Any(image => image.ResourceId == resourceId);
}
