using System.Collections.Immutable;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Geometry;

/// <summary>Shared painted bounds, picking and normalized transforms. Register new kinds here with their painter.</summary>
public static class AnnotationGeometry
{
    public static Rect2 LocalBounds(Annotation a, Func<TextBoxAnnotation, Rect2>? textBounds = null) => a switch
    {
        StrokeAnnotation { IsOutlined: true } s => StrokeRegion.Bounds(s.Outline),
        StrokeAnnotation s => StrokeGeometry.Bounds(s.Points, s.WidthPt),
        ImageAnnotation i => i.LocalBounds,
        LineAnnotation l => LineGeometry.Bounds(l),
        PdfTextHighlight h => TextSelection.Bounds(TextSelection.PaintedQuads(h)),
        PdfTextMarkup m => TextSelection.Bounds(TextSelection.PaintedQuads(m)),
        StickyNote => new(0, 0, StickyNote.IconPt, StickyNote.IconPt),
        ShapeAnnotation s => ShapeGeometry.Bounds(s),
        TextBoxAnnotation t => textBounds?.Invoke(t) ?? throw new NotSupportedException("Text bounds require a layout provider."),
        _ => throw new NotSupportedException($"{a.Kind} geometry is not implemented.")
    };

    public static Point2[] Corners(Rect2 b, Affine m) =>
        [m.Transform(new(b.X, b.Y)), m.Transform(new(b.X + b.Width, b.Y)),
         m.Transform(new(b.X + b.Width, b.Y + b.Height)), m.Transform(new(b.X, b.Y + b.Height))];

    public static Rect2 Bounds(IEnumerable<Annotation> annotations, Func<TextBoxAnnotation, Rect2>? textBounds = null)
    {
        var points = annotations.SelectMany(a => Corners(LocalBounds(a, textBounds), a.Transform)).ToArray();
        if (points.Length == 0) return new(0, 0, 0, 0);
        return new(points.Min(p => p.X), points.Min(p => p.Y),
            points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));
    }

    public static bool Hit(Annotation a, Point2 point, double tolerance, Func<TextBoxAnnotation, Rect2>? textBounds = null)
    {
        var local = a.Transform.Invert().Transform(point);
        return a switch
        {
            StrokeAnnotation { IsOutlined: true } s => StrokeRegion.Contains(s.Outline, local) || StrokeRegion.DistanceToEdges(s.Outline, local, local) <= tolerance,
            StrokeAnnotation s => StrokeGeometry.DistanceToPolyline(s.Points, local) <= s.WidthPt / 2 + tolerance,
            ImageAnnotation i => local.X >= i.LocalBounds.X && local.X <= i.LocalBounds.X + i.LocalBounds.Width &&
                local.Y >= i.LocalBounds.Y && local.Y <= i.LocalBounds.Y + i.LocalBounds.Height,
            TextBoxAnnotation t => Contains(LocalBounds(t, textBounds), local),
            LineAnnotation l => LineGeometry.Hit(l, local, tolerance),
            ShapeAnnotation s => ShapeGeometry.Hit(s, local, tolerance),
            PdfTextHighlight h => TextSelection.PaintedQuads(h).Any(q => TextSelection.DistanceToQuad(q, local) <= tolerance),
            // The glyphs a markup line belongs to count too, so a thin line is easy to click (spec 24.8).
            PdfTextMarkup m => m.Quads.Concat(TextSelection.PaintedQuads(m)).Any(q => TextSelection.DistanceToQuad(q, local) <= tolerance),
            StickyNote => Contains(new(0, 0, StickyNote.IconPt, StickyNote.IconPt), local),
            _ => false
        };
    }

    private static bool Contains(Rect2 b, Point2 p) => p.X >= b.X && p.X <= b.X + b.Width && p.Y >= b.Y && p.Y <= b.Y + b.Height;

    /// <summary>False for annotations bound to PDF glyphs (spec 9.3): they are never moved, rotated or scaled on their own.</summary>
    public static bool CanTransform(Annotation a) => a is not (PdfTextHighlight or PdfTextMarkup);

    /// <summary>Sticky notes only move: their icon is never scaled or rotated (spec 24.9).</summary>
    public static bool MoveOnly(Annotation a) => a is StickyNote;

    /// <summary>Positive uniform scale is baked into geometry once; the saved transform stays rigid.</summary>
    public static Annotation Similarity(Annotation a, Point2 center, double scale = 1, double angle = 0, Point2 move = default)
    {
        if (a.Locked) throw new Commands.AnnotationLockedException();
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        var change = Affine.Translation(-center.X, -center.Y).Then(Affine.Scale(scale)).Then(Affine.Rotation(angle))
            .Then(Affine.Translation(center.X + move.X, center.Y + move.Y));
        var combined = a.Transform.Then(change);
        var rigid = new Affine(combined.M11 / scale, combined.M12 / scale, combined.M21 / scale, combined.M22 / scale,
            combined.OffsetX, combined.OffsetY);
        return a switch
        {
            StrokeAnnotation s => s with
            {
                Transform = rigid, Points = s.Points.Select(p => Scaled(p, scale)).ToImmutableArray(), WidthPt = s.WidthPt * scale,
                Outline = s.IsOutlined ? s.Outline.Select(ring => ring.Select(p => Scaled(p, scale)).ToImmutableArray()).ToImmutableArray() : s.Outline
            },
            ImageAnnotation i => i with { Transform = rigid, LocalBounds = new(i.LocalBounds.X * scale, i.LocalBounds.Y * scale, i.LocalBounds.Width * scale, i.LocalBounds.Height * scale) },
            LineAnnotation l => l with { Transform = rigid, Start = Scaled(l.Start, scale), End = Scaled(l.End, scale), Controls = l.Controls.Select(p => Scaled(p, scale)).ToImmutableArray(), WidthPt = l.WidthPt * scale },
            TextBoxAnnotation t => t with { Transform = rigid, WidthPt = t.WidthPt * scale, Runs = t.Runs.Select(r => r with { SizePt = r.SizePt * scale }).ToImmutableArray() },
            ShapeAnnotation s => ShapeGeometry.Scaled(s, scale) with { Transform = rigid },
            // The icon keeps its size and stays upright: only where its top-left corner goes counts.
            StickyNote n => n with { Transform = Affine.Translation(combined.OffsetX, combined.OffsetY) },
            _ => throw new NotSupportedException($"{a.Kind} cannot be transformed yet.")
        };
    }

    private static Point2 Scaled(Point2 p, double scale) => new(p.X * scale, p.Y * scale);

    /// <summary>
    /// Single-shape free resize in its own rotated frame (D11, like presentation apps): box and corners stretch from the
    /// fixed <paramref name="anchor"/>; the border width stays.
    /// </summary>
    public static ShapeAnnotation ResizeShape(ShapeAnnotation shape, Point2 anchor, double sx, double sy)
    {
        if (shape.Locked) throw new Commands.AnnotationLockedException();
        if (!double.IsFinite(sx) || !double.IsFinite(sy) || sx <= 0 || sy <= 0) throw new ArgumentOutOfRangeException(nameof(sx));
        // The fixed corner of the selection frame lies half a border outside the outline; the outline's own edge stays put.
        var outline = ShapeGeometry.Bounds(shape with { Style = shape.Style with { BorderEnabled = false } });
        anchor = new(Math.Clamp(anchor.X, outline.X, outline.X + outline.Width), Math.Clamp(anchor.Y, outline.Y, outline.Y + outline.Height));
        Point2 Stretch(Point2 p) => new(anchor.X + (p.X - anchor.X) * sx, anchor.Y + (p.Y - anchor.Y) * sy);
        var b = shape.Box;
        return shape with
        {
            Box = shape.UsesBox ? new(anchor.X + (b.X - anchor.X) * sx, anchor.Y + (b.Y - anchor.Y) * sy, b.Width * sx, b.Height * sy) : default,
            Points = shape.Points.Select(Stretch).ToImmutableArray()
        };
    }

    /// <summary>Single-image free resize in its own rotated frame, keeping the opposite corner fixed.</summary>
    public static ImageAnnotation ResizeImage(ImageAnnotation image, Point2 anchor, double sx, double sy)
    {
        if (image.Locked) throw new Commands.AnnotationLockedException();
        if (!double.IsFinite(sx) || !double.IsFinite(sy) || sx <= 0 || sy <= 0) throw new ArgumentOutOfRangeException(nameof(sx));
        var b = image.LocalBounds;
        return image with { LocalBounds = new(anchor.X + (b.X - anchor.X) * sx, anchor.Y + (b.Y - anchor.Y) * sy, b.Width * sx, b.Height * sy) };
    }
}
