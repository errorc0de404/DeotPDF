using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Serialization;

/// <summary>Serialized metadata split the way the PDF layer stores it: one document header, one envelope per annotation, resource payloads.</summary>
public sealed record SerializedDocument(byte[] Header, ImmutableArray<SerializedAnnotation> Annotations, ImmutableArray<Resource> Resources);

public sealed record SerializedAnnotation(Guid PageId, Guid AnnotationId, byte[] Envelope);

public enum HeaderStatus { Valid, Unsupported, Invalid }

public enum AnnotationReadStatus
{
    /// <summary>Valid own annotation; restored into the model. The PDF layer hides its stored appearance.</summary>
    Restored,
    /// <summary>Valid own annotation with blank text (spec 6.2). Not restored and its appearance is not shown.</summary>
    DiscardedEmpty,
    /// <summary>Unverifiable data. The PDF layer keeps its appearance visible as a read-only element.</summary>
    Rejected
}

public sealed record AnnotationReadResult(AnnotationReadStatus Status, Guid? AnnotationId, string? Reason);

/// <summary>Result of reading metadata. <see cref="Model"/> is null unless the header is valid.</summary>
public sealed record DocumentReadResult(HeaderStatus HeaderStatus, DocumentModel? Model,
    ImmutableArray<AnnotationReadResult> Annotations, ImmutableArray<string> Warnings);

/// <summary>
/// Metadata schema v1 writer/reader. The writer only accepts a valid model without blank text boxes.
/// The reader never guesses: anything it cannot verify is reported as Rejected/Invalid so the caller keeps the
/// original appearance read-only instead of hiding it.
/// </summary>
public static class SchemaV1
{
    public const string AppId = "PdfNotes";
    public const int Version = 1;
    public const string CoordinateSpace = "cropbox-top-left-unrotated-pt";
    public const string TextIndexUnit = "utf16-code-unit/grapheme-boundary";
    public const int MaxHeaderBytes = 16 * 1024 * 1024;
    public const int MaxEnvelopeBytes = 64 * 1024 * 1024;
    public const double PageSizeTolerancePt = 1e-3;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowOutOfOrderMetadataProperties = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static SerializedDocument Serialize(DocumentModel model) => Serialize(model, allowOutline: false);

    /// <param name="allowOutline">Schema v3 writes area-eraser pieces; v1/v2 cannot represent them and refuse.</param>
    /// <param name="allowShape">Schema v5 writes shapes; older versions cannot represent them and refuse.</param>
    /// <param name="allowV6">Schema v6 writes underline/strikeout markups and sticky notes; older versions refuse them.</param>
    internal static SerializedDocument Serialize(DocumentModel model, bool allowOutline, bool allowShape = false, bool allowV6 = false)
    {
        if (!allowShape && model.AllAnnotations.Any(a => a is ShapeAnnotation)) throw new InvalidDataException("Shapes need schema v5.");
        if (!allowV6 && model.AllAnnotations.Any(a => a is PdfTextMarkup or StickyNote)) throw new InvalidDataException("Text markups and notes need schema v6.");
        if (model.Pages.Any(p => p.Setup.Content is not null) || model.AllAnnotations.OfType<TextBoxAnnotation>().Any(t => t.LayoutScale != 1))
            throw new InvalidDataException("Page transforms and scaled text metrics need schema v4.");
        if (ModelValidator.ValidateDocument(model) is { } error) throw new InvalidDataException(error);
        if (!allowOutline && model.AllAnnotations.OfType<StrokeAnnotation>().Any(s => s.IsOutlined))
            throw new InvalidDataException("Erased stroke pieces need schema v3.");
        if (model.AllAnnotations.OfType<TextBoxAnnotation>().Any(t => t.IsBlank))
            throw new InvalidDataException("Blank text boxes must be removed before saving.");
        var used = model.AllAnnotations.OfType<ImageAnnotation>().Select(i => i.ResourceId).ToHashSet();
        var resources = model.Resources.Values.Where(r => used.Contains(r.Id)).OrderBy(r => r.Id).ToImmutableArray();
        var header = new DocumentHeaderDto
        {
            App = AppId, SchemaVersion = Version, CoordinateSpace = CoordinateSpace, TextIndexUnit = TextIndexUnit,
            DocumentId = model.Id,
            Pages = model.Pages.Select(p => new PageDto
            {
                Id = p.Id, WidthPt = p.Setup.WidthPt, HeightPt = p.Setup.HeightPt, Rotation = p.Setup.Rotation,
                Background = new() { Kind = p.Setup.Background.Kind, SpacingMm = p.Setup.Background.SpacingMm, LineColor = p.Setup.Background.LineColor.ToHex() },
                AnnotationIds = p.Annotations.Select(a => a.Id).ToArray()
            }).ToArray(),
            Resources = resources.Select(r => new ResourceDescriptorDto
            {
                Id = r.Id, MediaType = r.MediaType, Sha256 = r.Sha256, Length = r.Payload.Length
            }).ToArray()
        };
        var annotations = model.Pages.SelectMany(p => p.Annotations.Select(a => new SerializedAnnotation(p.Id, a.Id,
            JsonSerializer.SerializeToUtf8Bytes(new AnnotationEnvelopeDto
            {
                App = AppId, SchemaVersion = Version, DocumentId = model.Id, PageId = p.Id, Annotation = ToDto(a)
            }, JsonOptions)))).ToImmutableArray();
        if (annotations.Any(a => a.Envelope.Length > MaxEnvelopeBytes)) throw new InvalidDataException("Annotation metadata exceeds its size limit.");
        byte[] headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        if (headerBytes.Length > MaxHeaderBytes) throw new InvalidDataException("Document metadata exceeds its size limit.");
        return new(headerBytes, annotations, resources);
    }

    /// <param name="header">Header bytes, or null when the PDF has none.</param>
    /// <param name="envelopes">Every own-marked annotation payload found in the PDF, in any order.</param>
    /// <param name="resourcePayload">Returns the stored payload for a resource id, or null when absent.</param>
    /// <param name="actualPages">Page frames measured from the PDF itself; a mismatch means another app changed the pages.</param>
    public static DocumentReadResult Read(byte[]? header, IReadOnlyList<byte[]> envelopes, Func<Guid, byte[]?> resourcePayload,
        IReadOnlyList<PageFrame>? actualPages = null) => Read(header, envelopes, resourcePayload, actualPages, allowOutline: false);

    internal static DocumentReadResult Read(byte[]? header, IReadOnlyList<byte[]> envelopes, Func<Guid, byte[]?> resourcePayload,
        IReadOnlyList<PageFrame>? actualPages, bool allowOutline, bool allowShape = false, bool allowV6 = false)
    {
        var warnings = new List<string>();
        var (status, dto, reason) = ReadHeader(header, actualPages);
        if (status != HeaderStatus.Valid)
        {
            if (header is not null) warnings.Add($"Document metadata is {status.ToString().ToLowerInvariant()}: {reason} Annotations stay read-only.");
            var rejected = envelopes.Select(_ => new AnnotationReadResult(AnnotationReadStatus.Rejected, null, "Document metadata is not usable.")).ToImmutableArray();
            return new(status, null, rejected, [.. warnings]);
        }

        var resources = new Dictionary<Guid, Resource>();
        foreach (var descriptor in dto!.Resources)
        {
            byte[]? payload = resourcePayload(descriptor.Id);
            var resource = payload is null || payload.LongLength != descriptor.Length ? null
                : new Resource(descriptor.Id, descriptor.MediaType, ImmutableArray.Create(payload), descriptor.Sha256);
            if (resource is null || ModelValidator.ValidateResource(resource, verifyHash: true) is not null)
                warnings.Add("An image resource is missing or damaged; images using it stay read-only.");
            else resources[descriptor.Id] = resource;
        }

        var expectedPage = new Dictionary<Guid, Guid>();
        foreach (var page in dto.Pages)
            foreach (var id in page.AnnotationIds) expectedPage[id] = page.Id;
        var restored = new Dictionary<Guid, Annotation>();
        var discarded = new HashSet<Guid>();
        var results = ImmutableArray.CreateBuilder<AnnotationReadResult>(envelopes.Count);
        foreach (var bytes in envelopes)
        {
            var result = ReadEnvelope(bytes, dto, expectedPage, resources, restored, discarded, allowOutline, allowShape, allowV6);
            results.Add(result);
        }

        int missing = expectedPage.Keys.Count(id => !restored.ContainsKey(id) && !discarded.Contains(id));
        if (missing > 0) warnings.Add($"{missing} annotation(s) listed in the document metadata could not be restored.");
        var pages = dto.Pages.Select((p, index) => new Page(p.Id,
            new PageSetup(p.WidthPt, p.HeightPt, p.Rotation, PageSource.Pdf(index), ToBackground(p.Background)),
            p.AnnotationIds.Where(restored.ContainsKey).Select(id => restored[id]).ToImmutableList())).ToImmutableList();
        var used = restored.Values.OfType<ImageAnnotation>().Select(i => i.ResourceId).ToHashSet();
        var model = new DocumentModel(dto.DocumentId, pages,
            resources.Where(r => used.Contains(r.Key)).ToImmutableDictionary());
        if (ModelValidator.ValidateDocument(model) is { } modelError)
        {
            warnings.Add($"Restored document failed validation: {modelError}");
            return new(HeaderStatus.Invalid, null,
                [.. envelopes.Select(_ => new AnnotationReadResult(AnnotationReadStatus.Rejected, null, modelError))], [.. warnings]);
        }
        return new(HeaderStatus.Valid, model, results.MoveToImmutable(), [.. warnings]);
    }

    private static (HeaderStatus, DocumentHeaderDto?, string) ReadHeader(byte[]? bytes, IReadOnlyList<PageFrame>? actualPages)
    {
        if (bytes is null || bytes.Length == 0) return (HeaderStatus.Invalid, null, "No metadata.");
        if (bytes.Length > MaxHeaderBytes) return (HeaderStatus.Invalid, null, "Metadata is too large.");
        DocumentHeaderDto dto;
        try
        {
            using (var probe = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }))
            {
                var root = probe.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("app", out var app) || app.ValueKind != JsonValueKind.String || app.GetString() != AppId)
                    return (HeaderStatus.Invalid, null, "Not PdfNotes metadata.");
                if (!root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out int v))
                    return (HeaderStatus.Invalid, null, "Schema version is missing.");
                if (v != Version) return (HeaderStatus.Unsupported, null, $"Schema version {v} is not supported.");
            }
            dto = JsonSerializer.Deserialize<DocumentHeaderDto>(bytes, JsonOptions) ?? throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return (HeaderStatus.Invalid, null, "Metadata is malformed.");
        }

        if (dto.CoordinateSpace != CoordinateSpace || dto.TextIndexUnit != TextIndexUnit) return (HeaderStatus.Invalid, null, "Unit declaration does not match.");
        if (dto.DocumentId == Guid.Empty || dto.Pages.Length == 0 || dto.Pages.Any(p => p is null) || dto.Resources.Any(r => r is null))
            return (HeaderStatus.Invalid, null, "Document structure is invalid.");
        var ids = new HashSet<Guid> { dto.DocumentId };
        foreach (var page in dto.Pages)
        {
            if (!ids.Add(page.Id) || page.Id == Guid.Empty || page.AnnotationIds.Any(id => id == Guid.Empty || !ids.Add(id)))
                return (HeaderStatus.Invalid, null, "Ids are empty or duplicated.");
            PageSetup setup;
            try { setup = new(page.WidthPt, page.HeightPt, page.Rotation, PageSource.Generated, ToBackground(page.Background)); }
            catch (FormatException) { return (HeaderStatus.Invalid, null, "Page background is invalid."); }
            if (ModelValidator.ValidatePageSetup(setup) is { } error) return (HeaderStatus.Invalid, null, error);
        }
        foreach (var r in dto.Resources)
            if (r.Id == Guid.Empty || !ids.Add(r.Id) || !ModelLimits.ImageMediaTypes.Contains(r.MediaType) ||
                r.Length <= 0 || r.Length > ModelLimits.MaxResourceBytes || r.Sha256.Length != 64)
                return (HeaderStatus.Invalid, null, "Resource descriptor is invalid.");
        if (actualPages is not null)
        {
            if (actualPages.Count != dto.Pages.Length) return (HeaderStatus.Invalid, null, "Page count differs from the PDF.");
            for (int i = 0; i < actualPages.Count; i++)
            {
                var a = actualPages[i];
                var p = dto.Pages[i];
                if (Math.Abs(a.WidthPt - p.WidthPt) > PageSizeTolerancePt || Math.Abs(a.HeightPt - p.HeightPt) > PageSizeTolerancePt || a.Rotation != p.Rotation)
                    return (HeaderStatus.Invalid, null, "Page geometry differs from the PDF.");
            }
        }
        return (HeaderStatus.Valid, dto, "");
    }

    private static AnnotationReadResult ReadEnvelope(byte[] bytes, DocumentHeaderDto header, Dictionary<Guid, Guid> expectedPage,
        Dictionary<Guid, Resource> resources, Dictionary<Guid, Annotation> restored, HashSet<Guid> discarded, bool allowOutline, bool allowShape, bool allowV6)
    {
        AnnotationEnvelopeDto envelope;
        try
        {
            if (bytes.Length > MaxEnvelopeBytes) return Rejected(null, "Annotation metadata is too large.");
            envelope = JsonSerializer.Deserialize<AnnotationEnvelopeDto>(bytes, JsonOptions) ?? throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return Rejected(null, "Annotation metadata is malformed.");
        }
        Guid id = envelope.Annotation.Id;
        if (envelope.App != AppId || envelope.SchemaVersion != Version) return Rejected(id, "Annotation schema is not supported.");
        if (envelope.DocumentId != header.DocumentId) return Rejected(id, "Annotation belongs to another document.");
        if (!expectedPage.TryGetValue(id, out var pageId) || pageId != envelope.PageId) return Rejected(id, "Annotation is not listed on its page.");
        if (restored.ContainsKey(id) || discarded.Contains(id)) return Rejected(id, "Annotation is duplicated.");
        if (!allowOutline && envelope.Annotation is StrokeDto { Outline: not null }) return Rejected(id, "Stroke outlines need schema v3.");
        if (!allowShape && envelope.Annotation is ShapeDto) return Rejected(id, "Shapes need schema v5.");
        if (!allowV6 && envelope.Annotation is TextMarkupDto or NoteDto) return Rejected(id, "Text markups and notes need schema v6.");
        Annotation annotation;
        try { annotation = FromDto(envelope.Annotation); }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidDataException)
        {
            return Rejected(id, "Annotation data is invalid.");
        }
        if (ModelValidator.ValidateAnnotation(annotation, resources.ContainsKey) is { } error) return Rejected(id, error);
        if (annotation is TextBoxAnnotation { IsBlank: true } or StickyNote { IsBlank: true })
        {
            discarded.Add(id);
            return new(AnnotationReadStatus.DiscardedEmpty, id, "Blank text box removed.");
        }
        restored.Add(id, annotation);
        return new(AnnotationReadStatus.Restored, id, null);
    }

    private static AnnotationReadResult Rejected(Guid? id, string reason) => new(AnnotationReadStatus.Rejected, id, reason);

    private static PageBackground ToBackground(BackgroundDto dto) =>
        new(dto.Kind, dto.SpacingMm, Color(dto.LineColor));

    private static AnnotationDto ToDto(Annotation annotation) => annotation switch
    {
        InkStroke s => new InkDto { Id = s.Id, Locked = s.Locked, Transform = s.Transform.ToArray(), Points = Flat(s.Points), WidthPt = s.WidthPt, Color = s.Color.ToHex(), Opacity = s.Opacity, Outline = FlatOutline(s) },
        FreeHighlightStroke s => new FreeHighlightDto { Id = s.Id, Locked = s.Locked, Transform = s.Transform.ToArray(), Points = Flat(s.Points), WidthPt = s.WidthPt, Color = s.Color.ToHex(), Opacity = s.Opacity, Outline = FlatOutline(s) },
        LineAnnotation l => new LineDto
        {
            Id = l.Id, Locked = l.Locked, Transform = l.Transform.ToArray(), LineKind = l.LineKind, Start = [l.Start.X, l.Start.Y], End = [l.End.X, l.End.Y],
            Controls = Flat(l.Controls), WidthPt = l.WidthPt, Dash = l.Dash, StartArrow = l.StartArrow, EndArrow = l.EndArrow, Color = l.Color.ToHex(), Opacity = l.Opacity
        },
        PdfTextHighlight h => new PdfTextHighlightDto
        {
            Id = h.Id, Locked = h.Locked, Transform = h.Transform.ToArray(),
            Quads = h.Quads.SelectMany(q => new[] { q.UpperLeft, q.UpperRight, q.LowerLeft, q.LowerRight }).SelectMany(p => new[] { p.X, p.Y }).ToArray(),
            SourceTextHint = h.SourceTextHint, HeightMode = h.HeightMode, ManualHeightPt = h.ManualHeightPt, Color = h.Color.ToHex(), Opacity = h.Opacity
        },
        TextBoxAnnotation t => new TextBoxDto
        {
            Id = t.Id, Locked = t.Locked, Transform = t.Transform.ToArray(), Text = t.Text, Alignment = t.Alignment, WidthPt = t.WidthPt,
            Runs = t.Runs.Select(r => new TextRunDto
            {
                Start = r.Start, Length = r.Length, FontFamily = r.FontFamily, SizePt = r.SizePt, Color = r.Color.ToHex(),
                Bold = r.Bold, Italic = r.Italic, Underline = r.Underline, Strikethrough = r.Strikethrough
            }).ToArray(),
            BoxStyle = new()
            {
                BackgroundColor = t.BoxStyle.BackgroundColor.ToHex(), BackgroundOpacity = t.BoxStyle.BackgroundOpacity,
                BorderEnabled = t.BoxStyle.BorderEnabled, BorderColor = t.BoxStyle.BorderColor.ToHex(), BorderOpacity = t.BoxStyle.BorderOpacity
            },
            Highlights = t.Highlights.Select(h => new TextHighlightDto
            {
                Start = h.Start, Length = h.Length, HeightMode = h.HeightMode, ManualHeightPt = h.ManualHeightPt, Color = h.Color.ToHex(), Opacity = h.Opacity
            }).ToArray()
        },
        ImageAnnotation i => new ImageDto
        {
            Id = i.Id, Locked = i.Locked, Transform = i.Transform.ToArray(), ResourceId = i.ResourceId,
            LocalBounds = [i.LocalBounds.X, i.LocalBounds.Y, i.LocalBounds.Width, i.LocalBounds.Height]
        },
        PdfTextMarkup m => new TextMarkupDto
        {
            Id = m.Id, Locked = m.Locked, Transform = m.Transform.ToArray(),
            Quads = m.Quads.SelectMany(q => new[] { q.UpperLeft, q.UpperRight, q.LowerLeft, q.LowerRight }).SelectMany(p => new[] { p.X, p.Y }).ToArray(),
            SourceTextHint = m.SourceTextHint, MarkupKind = m.MarkupKind, WidthPt = m.WidthPt, Color = m.Color.ToHex(), Opacity = m.Opacity
        },
        StickyNote n => new NoteDto { Id = n.Id, Locked = n.Locked, Transform = n.Transform.ToArray(), Text = n.Text, Color = n.Color.ToHex() },
        ShapeAnnotation s => new ShapeDto
        {
            Id = s.Id, Locked = s.Locked, Transform = s.Transform.ToArray(), ShapeKind = s.ShapeKind,
            Box = s.UsesBox ? [s.Box.X, s.Box.Y, s.Box.Width, s.Box.Height] : [], Points = Flat(s.Points),
            FillColor = s.Style.FillColor.ToHex(), FillOpacity = s.Style.FillOpacity, BorderEnabled = s.Style.BorderEnabled,
            BorderColor = s.Style.BorderColor.ToHex(), BorderWidthPt = s.Style.BorderWidthPt, BorderOpacity = s.Style.BorderOpacity, BorderDash = s.Style.BorderDash
        },
        _ => throw new NotSupportedException($"Annotation kind {annotation.Kind} has no v1 serializer.")
    };

    private static Annotation FromDto(AnnotationDto dto)
    {
        var transform = Affine.FromArray(Required(dto.Transform, 6));
        return dto switch
        {
            InkDto s => new InkStroke(s.Id, s.Locked, transform, Points(s.Points, allowEmpty: s.Outline is not null), s.WidthPt, Color(s.Color), s.Opacity) { Outline = ReadOutline(s) },
            FreeHighlightDto s => new FreeHighlightStroke(s.Id, s.Locked, transform, Points(s.Points, allowEmpty: s.Outline is not null), s.WidthPt, Color(s.Color), s.Opacity) { Outline = ReadOutline(s) },
            LineDto l => new LineAnnotation(l.Id, l.Locked, transform, l.LineKind, Point(l.Start), Point(l.End), Points(l.Controls, allowEmpty: true),
                l.WidthPt, l.Dash, l.StartArrow, l.EndArrow, Color(l.Color), l.Opacity),
            PdfTextHighlightDto h => new PdfTextHighlight(h.Id, h.Locked, Quads(h.Quads), h.SourceTextHint, h.HeightMode, h.ManualHeightPt, Color(h.Color), h.Opacity)
            {
                Transform = transform
            },
            TextBoxDto t => new TextBoxAnnotation(t.Id, t.Locked, transform, t.Text,
                NotNull(t.Runs).Select(r => new TextRun(r.Start, r.Length, r.FontFamily, r.SizePt, Color(r.Color), r.Bold, r.Italic, r.Underline, r.Strikethrough)).ToImmutableArray(),
                t.Alignment, t.WidthPt,
                new TextBoxStyle(Color(t.BoxStyle.BackgroundColor), t.BoxStyle.BackgroundOpacity, t.BoxStyle.BorderEnabled, Color(t.BoxStyle.BorderColor), t.BoxStyle.BorderOpacity),
                NotNull(t.Highlights).Select(h => new TextHighlightRange(h.Start, h.Length, h.HeightMode, h.ManualHeightPt, Color(h.Color), h.Opacity)).ToImmutableArray()),
            ImageDto i => new ImageAnnotation(i.Id, i.Locked, transform, i.ResourceId, Bounds(Required(i.LocalBounds, 4))),
            TextMarkupDto m => new PdfTextMarkup(m.Id, m.Locked, Quads(m.Quads), m.SourceTextHint, m.MarkupKind, m.WidthPt, Color(m.Color), m.Opacity) { Transform = transform },
            NoteDto n => new StickyNote(n.Id, n.Locked, transform, n.Text ?? throw new InvalidDataException(), Color(n.Color)),
            ShapeDto s => new ShapeAnnotation(s.Id, s.Locked, transform, s.ShapeKind,
                s.Box.Length == 0 ? default : Bounds(Required(s.Box, 4)), Points(s.Points, allowEmpty: true),
                new ShapeStyle(Color(s.FillColor), s.FillOpacity, s.BorderEnabled, Color(s.BorderColor), s.BorderWidthPt, s.BorderOpacity, s.BorderDash)),
            _ => throw new InvalidDataException("Unknown annotation type.")
        };
    }

    private static double[] Flat(ImmutableArray<Point2> points) => points.SelectMany(p => new[] { p.X, p.Y }).ToArray();

    private static double[][]? FlatOutline(StrokeAnnotation stroke) => stroke.IsOutlined ? stroke.Outline.Select(Flat).ToArray() : null;

    private static ImmutableArray<ImmutableArray<Point2>> ReadOutline(StrokeDto dto) => dto.Outline is null ? default
        : dto.Outline.Length == 0 ? throw new InvalidDataException("Outline is empty.")
        : NotNull(dto.Outline).Select(ring => Points(ring)).ToImmutableArray();

    private static double[] Required(double[] values, int count) =>
        values.Length == count ? values : throw new InvalidDataException("Unexpected number count.");

    private static Point2 Point(double[] values) { Required(values, 2); return new(values[0], values[1]); }

    private static Rect2 Bounds(double[] v) => new(v[0], v[1], v[2], v[3]);

    private static ImmutableArray<Point2> Points(double[] values, bool allowEmpty = false)
    {
        if (values.Length % 2 != 0 || (!allowEmpty && values.Length == 0)) throw new InvalidDataException("Point list is malformed.");
        var builder = ImmutableArray.CreateBuilder<Point2>(values.Length / 2);
        for (int i = 0; i < values.Length; i += 2) builder.Add(new(values[i], values[i + 1]));
        return builder.MoveToImmutable();
    }

    private static ImmutableArray<Quad> Quads(double[] values)
    {
        if (values.Length == 0 || values.Length % 8 != 0) throw new InvalidDataException("Quad list is malformed.");
        var builder = ImmutableArray.CreateBuilder<Quad>(values.Length / 8);
        for (int i = 0; i < values.Length; i += 8)
            builder.Add(new(new(values[i], values[i + 1]), new(values[i + 2], values[i + 3]), new(values[i + 4], values[i + 5]), new(values[i + 6], values[i + 7])));
        return builder.MoveToImmutable();
    }

    private static T[] NotNull<T>(T[] values) where T : class =>
        values.All(v => v is not null) ? values : throw new InvalidDataException("Null array element.");

    private static RgbColor Color(string text) =>
        RgbColor.TryParse(text, out var color) ? color : throw new FormatException("Color must be #RRGGBB.");
}
