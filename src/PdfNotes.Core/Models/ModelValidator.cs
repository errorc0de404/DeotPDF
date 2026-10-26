using System.Globalization;
using PdfNotes.Core.Geometry;

namespace PdfNotes.Core.Models;

/// <summary>
/// Data-integrity limits of the stored model. These are deliberately wider than UI input limits
/// (<see cref="InputLimits"/>) because committed scaling may legitimately exceed what a slider offers.
/// They exist to reject corrupted or hostile metadata, not to enforce UI ranges.
/// </summary>
public static class ModelLimits
{
    public const double MaxPageSidePt = 1_000_000;
    public const double MaxCoordinatePt = 10_000_000;
    public const double MaxLengthPt = 100_000;
    public const int MaxStrokePoints = 1_000_000;
    public const int MaxQuads = 100_000;
    public const int MaxTextLength = 1_000_000;
    public const int MaxRuns = 100_000;
    public const int MaxFontFamilyLength = 256;
    public const int MaxHintLength = 100_000;
    public const long MaxResourceBytes = 256L * 1024 * 1024;

    public static readonly IReadOnlySet<string> ImageMediaTypes =
        new HashSet<string>(StringComparer.Ordinal) { "image/png", "image/jpeg", "image/bmp" };
}

/// <summary>UI input ranges from the spec. Used by tools/dialogs; not by metadata validation.</summary>
public static class InputLimits
{
    public const double MinPenWidthPt = 0.5, MaxPenWidthPt = 40, DefaultPenWidthPt = 2;
    public const double MinFreeHighlightWidthPt = 2, MaxFreeHighlightWidthPt = 80, DefaultFreeHighlightWidthPt = 14;
    public const double MinLineWidthPt = 0.5, MaxLineWidthPt = 40, DefaultLineWidthPt = 2;
    public const double MinManualHighlightHeightPt = 1, MaxManualHighlightHeightPt = 80;
    public const double MinFontSizePt = 1, MaxFontSizePt = 400, DefaultFontSizePt = 12;
    public const double MinNewPageSideMm = 10, MaxNewPageSideMm = 2000;
    public const double DefaultTextBoxWidthPt = 180;
    public const double DefaultPasteOffsetPt = 10;
}

/// <summary>Validates model invariants. Returns null when valid, otherwise a short English reason (never content).</summary>
public static class ModelValidator
{
    public static string? ValidateDocument(DocumentModel model)
    {
        if (model.Id == Guid.Empty) return "Document id is empty.";
        if (model.Pages.Count == 0) return "A document needs at least one page.";
        var ids = new HashSet<Guid> { model.Id };
        foreach (var page in model.Pages)
        {
            if (!ids.Add(page.Id) || page.Id == Guid.Empty) return "Page id is empty or duplicated.";
            if (ValidatePageSetup(page.Setup) is { } setupError) return setupError;
            foreach (var annotation in page.Annotations)
            {
                if (!ids.Add(annotation.Id)) return "Annotation id is duplicated.";
                if (ValidateAnnotation(annotation, model.Resources.ContainsKey) is { } error) return error;
            }
        }
        foreach (var (_, tag) in model.LiveTags)
            if (tag.Validate() is { } tagError) return tagError;
        foreach (var (id, resource) in model.Resources)
        {
            if (id != resource.Id || !ids.Add(id)) return "Resource id is inconsistent or duplicated.";
            if (ValidateResource(resource, verifyHash: false) is { } error) return error;
        }
        return null;
    }

    public static string? ValidatePageSetup(PageSetup setup)
    {
        if (setup.Content is { } c && (!InRange(c.WidthPt, 0, ModelLimits.MaxPageSidePt) || c.WidthPt <= 0 ||
            !InRange(c.HeightPt, 0, ModelLimits.MaxPageSidePt) || c.HeightPt <= 0 || !c.Transform.IsFinite ||
            c.Transform.M11 <= 0 || c.Transform.M11 != c.Transform.M22 || c.Transform.M12 != 0 || c.Transform.M21 != 0 ||
            Math.Abs(c.Transform.OffsetX) > ModelLimits.MaxCoordinatePt || Math.Abs(c.Transform.OffsetY) > ModelLimits.MaxCoordinatePt))
            return "Source placement is invalid.";
        if (!InRange(setup.WidthPt, 0, ModelLimits.MaxPageSidePt) || setup.WidthPt <= 0 ||
            !InRange(setup.HeightPt, 0, ModelLimits.MaxPageSidePt) || setup.HeightPt <= 0)
            return "Page size is invalid.";
        if (!PageRotation.IsValid(setup.Rotation)) return "Page rotation is invalid.";
        if (setup.Source is null || setup.Background is null) return "Page source/background is missing.";
        if (setup.Source.Kind == PageSourceKind.Pdf ? setup.Source.PdfPageIndex is not >= 0 : setup.Source.PdfPageIndex is not null)
            return "Page source is invalid.";
        if (!Enum.IsDefined(setup.Background.Kind) || !double.IsFinite(setup.Background.SpacingMm) ||
            (setup.Background.Kind == PageBackgroundKind.Blank ? setup.Background.SpacingMm != 0 : setup.Background.SpacingMm is <= 0 or > 1000))
            return "Page background is invalid.";
        return null;
    }

    public static string? ValidateResource(Resource resource, bool verifyHash)
    {
        if (resource.Id == Guid.Empty) return "Resource id is empty.";
        if (!ModelLimits.ImageMediaTypes.Contains(resource.MediaType)) return "Resource media type is not supported.";
        if (resource.Payload.IsDefaultOrEmpty || resource.Payload.Length > ModelLimits.MaxResourceBytes) return "Resource size is invalid.";
        if (!HasImageSignature(resource.MediaType, resource.Payload.AsSpan())) return "Resource content does not match its media type.";
        if (verifyHash && !string.Equals(Resource.ComputeSha256(resource.Payload.AsSpan()), resource.Sha256, StringComparison.Ordinal))
            return "Resource hash does not match.";
        return null;
    }

    public static string? ValidateAnnotation(Annotation annotation, Func<Guid, bool> resourceExists)
    {
        if (annotation is TextBoxAnnotation metrics && (!double.IsFinite(metrics.LayoutScale) || metrics.LayoutScale <= 0 || metrics.LayoutScale > ModelLimits.MaxLengthPt))
            return "Text layout scale is invalid.";
        if (annotation.Id == Guid.Empty) return "Annotation id is empty.";
        if (!annotation.Transform.IsRigid || !TransformInRange(annotation.Transform)) return "Annotation transform must be rigid and finite.";
        return annotation switch
        {
            StrokeAnnotation s => ValidateStroke(s),
            LineAnnotation l => ValidateLine(l),
            PdfTextHighlight h => ValidatePdfHighlight(h),
            PdfTextMarkup m => ValidateMarkup(m),
            StickyNote n => n.Text is null || n.Text.Length > ModelLimits.MaxTextLength ? "Note text length is invalid."
                : !n.Transform.IsTranslation ? "A note is only translated." : null,
            TextBoxAnnotation t => ValidateTextBox(t),
            ImageAnnotation i => ValidateImage(i, resourceExists),
            ShapeAnnotation s => ValidateShape(s),
            _ => "Annotation type is not supported."
        };
    }

    private static string? ValidateShape(ShapeAnnotation shape)
    {
        if (!Enum.IsDefined(shape.ShapeKind) || shape.Points.IsDefault) return "Shape kind is invalid.";
        var b = shape.Box;
        if (shape.UsesBox)
        {
            if (shape.Points.Length != 0 || !b.IsFinite || Math.Abs(b.X) > ModelLimits.MaxCoordinatePt || Math.Abs(b.Y) > ModelLimits.MaxCoordinatePt ||
                !InRange(b.Width, 0, ModelLimits.MaxLengthPt) || b.Width <= 0 || !InRange(b.Height, 0, ModelLimits.MaxLengthPt) || b.Height <= 0)
                return "Shape box is invalid.";
        }
        else
        {
            if (b != default) return "A polygon has no box.";
            int min = shape.IsClosed ? 3 : 2;
            if (shape.Points.Length < min || shape.Points.Length > ModelLimits.MaxStrokePoints || !shape.Points.All(Coordinate)) return "Shape points are invalid.";
        }
        var style = shape.Style;
        if (style is null || !Enum.IsDefined(style.BorderDash) || ValidateOpacity(style.FillOpacity) is not null) return "Shape style is invalid.";
        if (ValidatePaint(style.BorderWidthPt, style.BorderOpacity) is { } error) return error;
        if (!style.BorderEnabled && (!shape.IsClosed || style.FillOpacity == 0)) return "A shape needs a fill or a border.";
        return null;
    }

    private static string? ValidateStroke(StrokeAnnotation stroke)
    {
        if (stroke.IsOutlined)
        {
            // Area eraser piece (schema v3): no centerline, closed rings only.
            if (stroke.Points.IsDefault || stroke.Points.Length != 0) return "An outlined stroke has no centerline points.";
            if (stroke.Outline.Length > ModelLimits.MaxQuads || stroke.Outline.Any(r => r.IsDefault || r.Length < 3) ||
                stroke.Outline.Sum(r => (long)r.Length) > ModelLimits.MaxStrokePoints) return "Stroke outline is invalid.";
            if (!stroke.Outline.All(r => r.All(Coordinate))) return "Stroke outline point is invalid.";
            return ValidatePaint(stroke.WidthPt, stroke.Opacity);
        }
        if (!stroke.Outline.IsDefault) return "Stroke outline is empty.";
        if (stroke.Points.IsDefaultOrEmpty || stroke.Points.Length > ModelLimits.MaxStrokePoints) return "Stroke point count is invalid.";
        if (!stroke.Points.All(Coordinate)) return "Stroke point is invalid.";
        return ValidatePaint(stroke.WidthPt, stroke.Opacity);
    }

    private static string? ValidateLine(LineAnnotation line)
    {
        if (!Enum.IsDefined(line.LineKind) || !Enum.IsDefined(line.Dash)) return "Line kind or dash is invalid.";
        if (line.Controls.IsDefault || line.Controls.Length != LineAnnotation.ControlCount(line.LineKind)) return "Line control count does not match its kind.";
        if (!Coordinate(line.Start) || !Coordinate(line.End) || !line.Controls.All(Coordinate)) return "Line point is invalid.";
        return ValidatePaint(line.WidthPt, line.Opacity);
    }

    private static string? ValidatePdfHighlight(PdfTextHighlight highlight)
    {
        if (!highlight.Transform.IsIdentity) return "PDF text highlights cannot be transformed.";
        if (highlight.Quads.IsDefaultOrEmpty || highlight.Quads.Length > ModelLimits.MaxQuads) return "Highlight quad count is invalid.";
        if (!highlight.Quads.All(q => Coordinate(q.UpperLeft) && Coordinate(q.UpperRight) && Coordinate(q.LowerLeft) && Coordinate(q.LowerRight)))
            return "Highlight quad is invalid.";
        if (highlight.SourceTextHint is { Length: > ModelLimits.MaxHintLength }) return "Highlight hint is too long.";
        return ValidateHeight(highlight.HeightMode, highlight.ManualHeightPt) ?? ValidateOpacity(highlight.Opacity);
    }

    /// <summary>Underline/strikeout width 0.5–4 pt (spec 24.8).</summary>
    public const double MinMarkupWidthPt = 0.5, MaxMarkupWidthPt = 4;

    private static string? ValidateMarkup(PdfTextMarkup markup)
    {
        if (!markup.Transform.IsIdentity) return "PDF text markups cannot be transformed.";
        if (!Enum.IsDefined(markup.MarkupKind)) return "Markup kind is invalid.";
        if (markup.Quads.IsDefaultOrEmpty || markup.Quads.Length > ModelLimits.MaxQuads) return "Markup quad count is invalid.";
        if (!markup.Quads.All(q => Coordinate(q.UpperLeft) && Coordinate(q.UpperRight) && Coordinate(q.LowerLeft) && Coordinate(q.LowerRight)))
            return "Markup quad is invalid.";
        if (markup.SourceTextHint is { Length: > ModelLimits.MaxHintLength }) return "Markup hint is too long.";
        if (!InRange(markup.WidthPt, MinMarkupWidthPt, MaxMarkupWidthPt)) return "Markup width is invalid.";
        return ValidateOpacity(markup.Opacity);
    }

    private static string? ValidateTextBox(TextBoxAnnotation box)
    {
        if (box.Text is null || box.Text.Length > ModelLimits.MaxTextLength) return "Text length is invalid.";
        if (!Enum.IsDefined(box.Alignment)) return "Text alignment is invalid.";
        if (!InRange(box.WidthPt, 0, ModelLimits.MaxLengthPt) || box.WidthPt <= 0) return "Text box width is invalid.";
        var style = box.BoxStyle;
        if (style is null || ValidateOpacity(style.BackgroundOpacity) is not null || ValidateOpacity(style.BorderOpacity) is not null)
            return "Text box style is invalid.";
        if (box.Runs.IsDefault || box.Highlights.IsDefault || box.Runs.Length > ModelLimits.MaxRuns || box.Highlights.Length > ModelLimits.MaxRuns)
            return "Text ranges are missing.";
        var boundaries = GraphemeBoundaries(box.Text);
        // Runs partition the text: contiguous, non-empty, in order, starting at 0 and ending at Text.Length.
        if (box.Text.Length == 0 ? box.Runs.Length != 0 : box.Runs.Length == 0) return "Text runs must cover the text.";
        int expected = 0;
        foreach (var run in box.Runs)
        {
            if (run.Start != expected || run.Length <= 0 || !RangeOnBoundaries(run.Start, run.Length, boundaries))
                return "Text runs must be contiguous and on grapheme boundaries.";
            expected = run.Start + run.Length;
            if (string.IsNullOrWhiteSpace(run.FontFamily) || run.FontFamily.Length > ModelLimits.MaxFontFamilyLength)
                return "Font family is invalid.";
            if (!InRange(run.SizePt, 0, ModelLimits.MaxLengthPt) || run.SizePt <= 0) return "Font size is invalid.";
        }
        if (expected != box.Text.Length) return "Text runs must cover the text.";
        int highlightEnd = 0;
        foreach (var highlight in box.Highlights)
        {
            if (highlight.Length <= 0 || highlight.Start < highlightEnd || !RangeOnBoundaries(highlight.Start, highlight.Length, boundaries))
                return "Text highlights must be ordered, non-overlapping and on grapheme boundaries.";
            highlightEnd = highlight.Start + highlight.Length;
            if ((ValidateHeight(highlight.HeightMode, highlight.ManualHeightPt) ?? ValidateOpacity(highlight.Opacity)) is { } error) return error;
        }
        return null;
    }

    private static string? ValidateImage(ImageAnnotation image, Func<Guid, bool> resourceExists)
    {
        var b = image.LocalBounds;
        if (!b.IsFinite || Math.Abs(b.X) > ModelLimits.MaxCoordinatePt || Math.Abs(b.Y) > ModelLimits.MaxCoordinatePt ||
            !InRange(b.Width, 0, ModelLimits.MaxLengthPt) || b.Width <= 0 || !InRange(b.Height, 0, ModelLimits.MaxLengthPt) || b.Height <= 0)
            return "Image bounds are invalid.";
        return image.ResourceId != Guid.Empty && resourceExists(image.ResourceId) ? null : "Image resource is missing.";
    }

    private static string? ValidatePaint(double widthPt, double opacity) =>
        !InRange(widthPt, 0, ModelLimits.MaxLengthPt) || widthPt <= 0 ? "Stroke width is invalid." : ValidateOpacity(opacity);

    private static string? ValidateOpacity(double opacity) => InRange(opacity, 0, 1) ? null : "Opacity must be within 0..1.";

    private static string? ValidateHeight(HighlightHeightMode mode, double? manual) => mode switch
    {
        HighlightHeightMode.Auto when manual is null => null,
        HighlightHeightMode.Manual when manual is { } h && InRange(h, 0, ModelLimits.MaxLengthPt) && h > 0 => null,
        _ => "Highlight height mode is invalid."
    };

    private static bool InRange(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;

    private static bool Coordinate(Point2 p) => p.IsFinite && Math.Abs(p.X) <= ModelLimits.MaxCoordinatePt && Math.Abs(p.Y) <= ModelLimits.MaxCoordinatePt;

    private static bool TransformInRange(Affine t) => Math.Abs(t.OffsetX) <= ModelLimits.MaxCoordinatePt && Math.Abs(t.OffsetY) <= ModelLimits.MaxCoordinatePt;

    private static bool RangeOnBoundaries(int start, int length, bool[] boundaries) =>
        start >= 0 && length >= 0 && start + length < boundaries.Length && boundaries[start] && boundaries[start + length];

    /// <summary>boundaries[i] is true when UTF-16 index i is an extended grapheme cluster boundary.</summary>
    public static bool[] GraphemeBoundaries(string text)
    {
        var boundaries = new bool[text.Length + 1];
        boundaries[0] = true;
        for (int i = 0; i < text.Length;)
        {
            i += StringInfo.GetNextTextElementLength(text, i);
            boundaries[i] = true;
        }
        return boundaries;
    }

    private static bool HasImageSignature(string mediaType, ReadOnlySpan<byte> data) => mediaType switch
    {
        "image/png" => data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        "image/jpeg" => data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]),
        "image/bmp" => data.StartsWith("BM"u8),
        _ => false
    };
}
