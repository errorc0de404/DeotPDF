using System.Text.Json.Serialization;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Serialization;

// Wire contract of metadata schema v1. These classes are the only JSON shape; model records never serialize
// directly, so refactoring the model cannot silently change stored files. See docs/file-format.md.

internal sealed class DocumentHeaderDto
{
    public required string App { get; init; }
    public required int SchemaVersion { get; init; }
    public required string CoordinateSpace { get; init; }
    public required string TextIndexUnit { get; init; }
    public required Guid DocumentId { get; init; }
    public required PageDto[] Pages { get; init; }
    public required ResourceDescriptorDto[] Resources { get; init; }
}

internal sealed class PageDto
{
    public required Guid Id { get; init; }
    public required double WidthPt { get; init; }
    public required double HeightPt { get; init; }
    public required int Rotation { get; init; }
    public required BackgroundDto Background { get; init; }
    /// <summary>Annotation ids bottom to top. The stacking order and the set of expected envelopes.</summary>
    public required Guid[] AnnotationIds { get; init; }
}

internal sealed class BackgroundDto
{
    public required PageBackgroundKind Kind { get; init; }
    public required double SpacingMm { get; init; }
    public required string LineColor { get; init; }
}

internal sealed class ResourceDescriptorDto
{
    public required Guid Id { get; init; }
    public required string MediaType { get; init; }
    public required string Sha256 { get; init; }
    public required long Length { get; init; }
}

internal sealed class AnnotationEnvelopeDto
{
    public required string App { get; init; }
    public required int SchemaVersion { get; init; }
    public required Guid DocumentId { get; init; }
    public required Guid PageId { get; init; }
    public required AnnotationDto Annotation { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(TextBoxDto), "textBox")]
[JsonDerivedType(typeof(InkDto), "ink")]
[JsonDerivedType(typeof(FreeHighlightDto), "freeHighlight")]
[JsonDerivedType(typeof(LineDto), "line")]
[JsonDerivedType(typeof(PdfTextHighlightDto), "pdfTextHighlight")]
[JsonDerivedType(typeof(TextMarkupDto), "textMarkup")]
[JsonDerivedType(typeof(NoteDto), "note")]
[JsonDerivedType(typeof(ImageDto), "image")]
[JsonDerivedType(typeof(ShapeDto), "shape")]
internal abstract class AnnotationDto
{
    public required Guid Id { get; init; }
    public required bool Locked { get; init; }
    /// <summary>[a b c d e f] local → canonical page points, rigid.</summary>
    public required double[] Transform { get; init; }
}

internal abstract class StrokeDto : AnnotationDto
{
    /// <summary>Flat [x0, y0, x1, y1, ...] local points.</summary>
    public required double[] Points { get; init; }
    public required double WidthPt { get; init; }
    public required string Color { get; init; }
    public required double Opacity { get; init; }
    /// <summary>Schema v3 only: area-eraser piece as flat [x0, y0, ...] rings filled even-odd; Points is then empty.</summary>
    public double[][]? Outline { get; init; }
}

internal sealed class InkDto : StrokeDto;

internal sealed class FreeHighlightDto : StrokeDto;

internal sealed class LineDto : AnnotationDto
{
    public required LineKind LineKind { get; init; }
    public required double[] Start { get; init; }
    public required double[] End { get; init; }
    /// <summary>Flat control points: 0, 2 or 4 numbers.</summary>
    public required double[] Controls { get; init; }
    public required double WidthPt { get; init; }
    public required DashStyle Dash { get; init; }
    public required bool StartArrow { get; init; }
    public required bool EndArrow { get; init; }
    public required string Color { get; init; }
    public required double Opacity { get; init; }
}

internal sealed class PdfTextHighlightDto : AnnotationDto
{
    /// <summary>Flat quads, 8 numbers each: UL, UR, LL, LR in canonical page points.</summary>
    public required double[] Quads { get; init; }
    public string? SourceTextHint { get; init; }
    public required HighlightHeightMode HeightMode { get; init; }
    public double? ManualHeightPt { get; init; }
    public required string Color { get; init; }
    public required double Opacity { get; init; }
}

/// <summary>Schema v6: underline or strikeout on PDF glyph quads (spec 24.8).</summary>
internal sealed class TextMarkupDto : AnnotationDto
{
    /// <summary>Flat quads, 8 numbers each: UL, UR, LL, LR in canonical page points.</summary>
    public required double[] Quads { get; init; }
    public string? SourceTextHint { get; init; }
    public required TextMarkupKind MarkupKind { get; init; }
    public required double WidthPt { get; init; }
    public required string Color { get; init; }
    public required double Opacity { get; init; }
}

/// <summary>Schema v6: sticky note (spec 24.9); the icon size is fixed, the transform a translation.</summary>
internal sealed class NoteDto : AnnotationDto
{
    public required string Text { get; init; }
    public required string Color { get; init; }
}

internal sealed class TextBoxDto : AnnotationDto
{
    public required string Text { get; init; }
    public required TextRunDto[] Runs { get; init; }
    public required TextAlignment Alignment { get; init; }
    public required double WidthPt { get; init; }
    public required TextBoxStyleDto BoxStyle { get; init; }
    public required TextHighlightDto[] Highlights { get; init; }
}

internal sealed class TextRunDto
{
    public required int Start { get; init; }
    public required int Length { get; init; }
    public required string FontFamily { get; init; }
    public required double SizePt { get; init; }
    public required string Color { get; init; }
    public required bool Bold { get; init; }
    public required bool Italic { get; init; }
    public required bool Underline { get; init; }
    public required bool Strikethrough { get; init; }
}

internal sealed class TextBoxStyleDto
{
    public required string BackgroundColor { get; init; }
    public required double BackgroundOpacity { get; init; }
    public required bool BorderEnabled { get; init; }
    public required string BorderColor { get; init; }
    public required double BorderOpacity { get; init; }
}

internal sealed class TextHighlightDto
{
    public required int Start { get; init; }
    public required int Length { get; init; }
    public required HighlightHeightMode HeightMode { get; init; }
    public double? ManualHeightPt { get; init; }
    public required string Color { get; init; }
    public required double Opacity { get; init; }
}

/// <summary>Schema v5 only (D11 shapes).</summary>
internal sealed class ShapeDto : AnnotationDto
{
    public required ShapeKind ShapeKind { get; init; }
    /// <summary>[x, y, width, height] in local points for rectangle, ellipse and triangle; empty for polygons.</summary>
    public required double[] Box { get; init; }
    /// <summary>Flat [x0, y0, ...] local vertices of a polygon or polyline; empty for box shapes.</summary>
    public required double[] Points { get; init; }
    public required string FillColor { get; init; }
    public required double FillOpacity { get; init; }
    public required bool BorderEnabled { get; init; }
    public required string BorderColor { get; init; }
    public required double BorderWidthPt { get; init; }
    public required double BorderOpacity { get; init; }
    public required DashStyle BorderDash { get; init; }
}

internal sealed class ImageDto : AnnotationDto
{
    public required Guid ResourceId { get; init; }
    /// <summary>[x, y, width, height] in local points.</summary>
    public required double[] LocalBounds { get; init; }
}
