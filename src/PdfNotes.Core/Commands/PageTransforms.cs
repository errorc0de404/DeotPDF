using System.Collections.Immutable;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Commands;

public enum PageResizeMode { FitContent, ClipContent }
public enum PageApplyScope { Current, Selected, All }

/// <summary>Displayed-page transforms (spec 11.6–11.8). One atomic command for all targets, including locked content.</summary>
public static class PageTransforms
{
    public static EditCommand Shrink(DocumentModel model, IEnumerable<Guid> ids, double percent, int alignment = 4, Point2? freePosition = null) =>
        ShrinkWithChanges(model, ids, percent, alignment, freePosition).Command;

    public static EditCommand Resize(DocumentModel model, IEnumerable<Guid> ids, double widthMm, double heightMm,
        PageResizeMode mode, int alignment = 4, Point2? freePosition = null) =>
        ResizeWithChanges(model, ids, widthMm, heightMm, mode, alignment, freePosition).Command;

    /// <summary>
    /// <see cref="Shrink"/> with each changed page's placement change: old canonical points to new canonical points
    /// (uniform scale + translation; the CLI reports it as <c>pageTransform</c>). Unchanged pages are left out.
    /// </summary>
    public static (EditCommand Command, IReadOnlyDictionary<Guid, Affine> Changes) ShrinkWithChanges(DocumentModel model, IEnumerable<Guid> ids,
        double percent, int alignment = 4, Point2? freePosition = null)
    {
        if (!double.IsFinite(percent) || percent <= 0 || percent > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        return Build(model, ids, alignment, freePosition, "undo.shrinkContent", p => (p.Frame.DisplayWidthPt, p.Frame.DisplayHeightPt, percent / 100));
    }

    /// <summary><see cref="Resize"/> with each changed page's placement change, as <see cref="ShrinkWithChanges"/>.</summary>
    public static (EditCommand Command, IReadOnlyDictionary<Guid, Affine> Changes) ResizeWithChanges(DocumentModel model, IEnumerable<Guid> ids,
        double widthMm, double heightMm, PageResizeMode mode, int alignment = 4, Point2? freePosition = null)
    {
        if (!double.IsFinite(widthMm) || !double.IsFinite(heightMm) || widthMm < 10 || widthMm > 2000 || heightMm < 10 || heightMm > 2000 || !Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(widthMm));
        double w = Units.MillimetersToPoints(widthMm), h = Units.MillimetersToPoints(heightMm);
        return Build(model, ids, alignment, freePosition, "undo.resizePages", p => (w, h, mode == PageResizeMode.FitContent
            ? Math.Min(1, Math.Min(w / p.Frame.DisplayWidthPt, h / p.Frame.DisplayHeightPt)) : 1));
    }

    private static (EditCommand Command, IReadOnlyDictionary<Guid, Affine> Changes) Build(DocumentModel model, IEnumerable<Guid> ids, int alignment, Point2? freePosition, string label,
        Func<PageSetup, (double W, double H, double Scale)> target)
    {
        if (alignment < 0 || alignment > 8) throw new ArgumentOutOfRangeException(nameof(alignment));
        if (freePosition is { } position && (!double.IsFinite(position.X) || !double.IsFinite(position.Y))) throw new ArgumentOutOfRangeException(nameof(freePosition));
        var operations = new List<EditOperation>();
        var changes = new Dictionary<Guid, Affine>();
        foreach (var id in ids.Distinct())
        {
            var page = model.GetPage(id); var old = page.Setup; var (w, h, scale) = target(old);
            var next = old with { WidthPt = old.Rotation is 90 or 270 ? h : w, HeightPt = old.Rotation is 90 or 270 ? w : h };
            double dx = freePosition is { } free ? free.X * w : (alignment % 3) / 2.0 * (w - scale * old.Frame.DisplayWidthPt);
            double dy = freePosition is { } freeY ? freeY.Y * h : (alignment / 3) / 2.0 * (h - scale * old.Frame.DisplayHeightPt);
            if (!double.IsFinite(dx) || !double.IsFinite(dy)) throw new ArgumentOutOfRangeException(nameof(freePosition));
            if (Math.Abs(w - old.Frame.DisplayWidthPt) < 1e-8 && Math.Abs(h - old.Frame.DisplayHeightPt) < 1e-8 && scale == 1 && dx == 0 && dy == 0) continue;
            var change = old.Frame.CanonicalToDisplay.Then(Affine.Scale(scale)).Then(Affine.Translation(dx, dy)).Then(next.Frame.DisplayToCanonical);
            // Quarter-turn conjugation yields a scale + translation; normalize exact zero coefficients.
            change = new(scale, 0, 0, scale, change.OffsetX, change.OffsetY);
            changes[id] = change;
            if (old.Source.Kind == PageSourceKind.Pdf)
            {
                var original = old.Content ?? new(old.WidthPt, old.HeightPt, Affine.Identity);
                next = next with { Content = original with { Transform = original.Transform.Then(change) } };
            }
            operations.Add(new ReplacePageSetupOperation(id, old, next));
            foreach (var a in page.Annotations) operations.Add(new ReplaceAnnotationOperation(id, a, Transform(a, change, scale)));
        }
        return (new(label, operations), changes);
    }

    /// <summary>
    /// One annotation through a page placement change (uniform <paramref name="scale"/> + translation in
    /// <paramref name="change"/>): geometry, lengths and text metrics once, lock kept. Also used to place annotations
    /// converted from the source page on a page whose content was moved (D11, spec 14.6).
    /// </summary>
    public static Annotation Transform(Annotation a, Affine change, double scale)
    {
        if (a is PdfTextHighlight h) return h with { Quads = h.Quads.Select(q => q.Transform(change)).ToImmutableArray(), ManualHeightPt = h.ManualHeightPt * scale };
        if (a is PdfTextMarkup m) return m with { Quads = m.Quads.Select(q => q.Transform(change)).ToImmutableArray(),
            WidthPt = Math.Clamp(m.WidthPt * scale, ModelValidator.MinMarkupWidthPt, ModelValidator.MaxMarkupWidthPt) };
        var result = AnnotationGeometry.Similarity(a with { Locked = false }, new(0, 0), scale, move: new(change.OffsetX, change.OffsetY));
        if (result is TextBoxAnnotation t) result = t with { LayoutScale = t.LayoutScale * scale,
            Highlights = t.Highlights.Select(h => h with { ManualHeightPt = h.ManualHeightPt * scale }).ToImmutableArray() };
        return result with { Locked = a.Locked };
    }
}
