using System.Collections.ObjectModel;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.ViewModels;

/// <summary>What the resize and shrink dialog asks for (spec 11.6–11.8, D10d); the target IDs are fixed when it opens.</summary>
public sealed record PageTransformRequest(PageApplyScope Scope, double Percent, double WidthMm, double HeightMm, PageResizeMode Mode, int Alignment)
{
    public Point2? FreePosition { get; init; }
    public Guid[]? TargetPageIds { get; init; }
}
