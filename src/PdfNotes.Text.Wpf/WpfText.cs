using System.Collections.Immutable;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using Alignment = PdfNotes.Core.Models.TextAlignment;

namespace PdfNotes.Text;

/// <summary>
/// The WPF text engine (D06 FlowDocument layout, moved here in D14a without changes): editor documents, run formatting,
/// and the laid-out box as a frozen WPF drawing for the screen plus the neutral <see cref="TextPaint"/> for PDF.
/// </summary>
public static class WpfText
{
    /// <summary>Registers this engine for <see cref="TextLayout"/>; call once at startup (idempotent).</summary>
    public static void Register() { if (!TextLayout.HasEngine) TextLayout.Engine = new WpfTextEngine(); }

    /// <summary>The cached layout with its WPF drawings (the screen painter and the live editor's highlight layer).</summary>
    public static WpfTextLayout Measure(TextBoxAnnotation box) => (WpfTextLayout)TextLayout.Measure(box);

    public static SolidColorBrush Brush(RgbColor c, double opacity = 1) { var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), c.R, c.G, c.B)); b.Freeze(); return b; }
    private static readonly Lazy<HashSet<string>> FontNames = new(() => Fonts.SystemFontFamilies.SelectMany(f => f.FamilyNames.Values.Append(f.Source)).ToHashSet(StringComparer.OrdinalIgnoreCase));
    public static bool FontAvailable(string name) => FontNames.Value.Contains(name);
    public static void SetRun(Inline inline, TextRun r)
    {
        inline.FontFamily = new FontFamily(FontAvailable(r.FontFamily) ? r.FontFamily : "Malgun Gothic");
        inline.FontSize = r.SizePt; inline.Foreground = Brush(r.Color);
        inline.FontWeight = r.Bold ? FontWeights.Bold : FontWeights.Normal;
        inline.FontStyle = r.Italic ? FontStyles.Italic : FontStyles.Normal;
        var decorations = new TextDecorationCollection();
        if (r.Underline) decorations.Add(TextDecorations.Underline[0]);
        if (r.Strikethrough) decorations.Add(TextDecorations.Strikethrough[0]);
        inline.TextDecorations = decorations;
    }
    public static FlowDocument Document(TextBoxAnnotation box, TextRun? emptyStyle = null)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(4 * box.LayoutScale), PageWidth = Math.Max(16 * box.LayoutScale, box.WidthPt), ColumnWidth = double.PositiveInfinity,
            FontFamily = new FontFamily("Malgun Gothic"), FontSize = 12 * box.LayoutScale, Background = Brushes.Transparent };
        TextOptions.SetTextFormattingMode(doc, TextFormattingMode.Ideal);
        var paragraph = new Paragraph { Margin = new Thickness(0), TextAlignment = box.Alignment switch { Alignment.Center => System.Windows.TextAlignment.Center, Alignment.Right => System.Windows.TextAlignment.Right, _ => System.Windows.TextAlignment.Left } };
        // A single paragraph with explicit LineBreaks avoids hidden paragraph spacing and preserves exact newlines.
        foreach (var r in box.Runs)
        {
            string text = box.Text.Substring(r.Start, r.Length);
            var pieces = text.Split('\n');
            for (int i = 0; i < pieces.Length; i++)
            {
                if (i > 0) { var line = new LineBreak(); SetRun(line, r); paragraph.Inlines.Add(line); }
                var run = new Run(pieces[i]); SetRun(run, r); paragraph.Inlines.Add(run);
            }
        }
        if (box.Text.Length == 0) { var run = new Run(); SetRun(run, emptyStyle ?? TextEditing.Default); paragraph.Inlines.Add(run); }
        doc.Blocks.Add(paragraph); return doc;
    }
    public static FlowDocument EditorDocument(TextBoxAnnotation box, TextRun? emptyStyle = null) => Document(TextLayout.Normalized(box), emptyStyle is null ? null : emptyStyle with { SizePt = emptyStyle.SizePt / box.LayoutScale });

    /// <summary>
    /// The WPF drawing as flattened outlines (0.02pt absolute, D01), keeping its group transforms, clips and opacities in
    /// order — exactly what the PDF appearance wrote from the drawing before D14a.
    /// </summary>
    public static TextPaint ToPaint(Drawing drawing)
    {
        if (drawing is DrawingGroup group)
        {
            var m = group.Transform?.Value ?? Matrix.Identity;
            return new TextPaintGroup(new(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY), group.ClipGeometry is null ? null : Figures(group.ClipGeometry),
                group.Opacity, [.. group.Children.Select(ToPaint)]);
        }
        if (drawing is not GeometryDrawing geometry) throw new NotSupportedException("Unexpected text drawing.");
        TextColor? fill = geometry.Brush is SolidColorBrush f ? PaintColor(f) : null;
        TextColor? stroke = geometry.Pen is { Brush: SolidColorBrush s } ? PaintColor(s) : null;
        bool evenOdd = fill is not null && geometry.Geometry.GetFlattenedPathGeometry().FillRule == FillRule.EvenOdd;
        return new TextPaintShape(Figures(geometry.Geometry), evenOdd, fill, stroke, stroke is null ? 0 : geometry.Pen!.Thickness);
    }

    private static TextColor PaintColor(SolidColorBrush brush) => new(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A / 255d * brush.Opacity);

    private static ImmutableArray<TextFigure> Figures(Geometry geometry)
    {
        var path = geometry.GetFlattenedPathGeometry(.02, ToleranceType.Absolute);
        var m = path.Transform?.Value ?? Matrix.Identity;
        var figures = ImmutableArray.CreateBuilder<TextFigure>();
        foreach (var f in path.Figures)
        {
            var points = ImmutableArray.CreateBuilder<Point2>();
            var p = m.Transform(f.StartPoint); points.Add(new(p.X, p.Y));
            foreach (var seg in f.Segments)
            {
                IEnumerable<Point> segment = seg is PolyLineSegment poly ? poly.Points : seg is LineSegment line ? [line.Point] : [];
                foreach (var point in segment) { p = m.Transform(point); points.Add(new(p.X, p.Y)); }
            }
            figures.Add(new(points.ToImmutable(), f.IsClosed));
        }
        return figures.ToImmutable();
    }
}

/// <summary>A WPF layout: the neutral result plus the frozen screen drawing and its highlight layer (for the live editor).</summary>
public sealed class WpfTextLayout(DrawingGroup drawing, double height, ImmutableArray<TextLayout.Character> characters, DrawingGroup highlights)
    : TextLayout.Layout(height, characters)
{
    private readonly Lazy<TextPaint> paint = new(() => WpfText.ToPaint(drawing));
    public DrawingGroup Drawing { get; } = drawing;
    /// <summary>The highlight layer alone (also inside <see cref="Drawing"/>, behind the glyphs) for the live editor.</summary>
    public DrawingGroup Highlights { get; } = highlights;
    public override TextPaint Paint => paint.Value;
}

/// <summary>One FlowDocument layout contract for editor, lightweight display, hit test and PDF outlines (D06).</summary>
internal sealed class WpfTextEngine : ITextLayoutEngine
{
    private static readonly Lazy<Dispatcher> Worker = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() => { ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }) { IsBackground = true, Name = "Text layout" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });

    public TextLayout.Layout Build(TextBoxAnnotation box) => Worker.Value.Invoke(() => BuildOnWorker(box));
    public bool FontAvailable(string name) => WpfText.FontAvailable(name);
    public IReadOnlyList<TextFontUse> Fonts(TextBoxAnnotation box) => Worker.Value.Invoke(() => FontsOnWorker(TextLayout.Normalized(box)));
    public IReadOnlyList<string> FontFamilies() => System.Windows.Media.Fonts.SystemFontFamilies
        .Select(f => f.FamilyNames.TryGetValue(English, out var name) ? name : f.Source)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static readonly System.Windows.Markup.XmlLanguage English = System.Windows.Markup.XmlLanguage.GetLanguage("en-us");

    /// <summary>
    /// Walks the glyph runs WPF drew for the box, in drawing order, and matches their characters to the box text: the
    /// family of each run's glyph typeface, and glyph 0 (.notdef) for a character no font had.
    /// </summary>
    private static TextFontUse[] FontsOnWorker(TextBoxAnnotation box)
    {
        var editor = Arrange(box, out _);
        new System.Windows.Media.Imaging.RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32).Render(editor);
        var runs = new List<GlyphRun>();
        CollectGlyphRuns(editor, runs);
        var found = new Dictionary<int, (string Font, bool Missing)>();
        int cursor = 0;
        foreach (var run in runs)
        {
            if (run.Characters is not { Count: > 0 } chars) continue;
            string text = new([.. chars]);
            int at = box.Text.IndexOf(text, cursor, StringComparison.Ordinal);
            if (at < 0) continue;
            var typeface = run.GlyphTypeface;
            string family = typeface.FamilyNames.TryGetValue(System.Globalization.CultureInfo.GetCultureInfo("en-US"), out var name) ? name : typeface.FamilyNames.Values.FirstOrDefault() ?? "";
            for (int i = 0; i < text.Length; i++)
            {
                bool missing = run.ClusterMap is { } map && i < map.Count && map[i] < run.GlyphIndices.Count && run.GlyphIndices[map[i]] == 0 &&
                    !char.IsWhiteSpace(text[i]);
                found.TryAdd(at + i, (family, missing));
            }
            cursor = at + text.Length;
        }
        var result = new List<TextFontUse>();
        foreach (var r in box.Runs)
        {
            string effective = WpfText.FontAvailable(r.FontFamily) ? r.FontFamily : TextLayout.FallbackFont;
            for (int offset = r.Start; offset < r.Start + r.Length;)
            {
                int length = System.Text.Rune.GetRuneAt(box.Text, offset).Utf16SequenceLength;
                if (box.Text[offset] != '\n')
                {
                    var (font, missing) = found.TryGetValue(offset, out var f) ? f : (effective, false);
                    result.Add(new(offset, length, box.Text.Substring(offset, length), r.FontFamily, font, missing));
                }
                offset += length;
            }
        }
        return [.. result];
    }

    private static void CollectGlyphRuns(Visual visual, List<GlyphRun> runs)
    {
        if (VisualTreeHelper.GetDrawing(visual) is { } drawing) CollectGlyphRuns(drawing, runs);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
            if (VisualTreeHelper.GetChild(visual, i) is Visual child) CollectGlyphRuns(child, runs);
    }

    private static void CollectGlyphRuns(Drawing drawing, List<GlyphRun> runs)
    {
        if (drawing is GlyphRunDrawing glyph) runs.Add(glyph.GlyphRun);
        else if (drawing is DrawingGroup group) foreach (var child in group.Children) CollectGlyphRuns(child, runs);
    }

    /// <summary>The live editor laid out for <paramref name="box"/> and the box height (D06).</summary>
    private static PageTextEditor Arrange(TextBoxAnnotation box, out double height)
    {
        var editor = new PageTextEditor { LayoutScale = box.LayoutScale, Document = WpfText.Document(box), Width = box.WidthPt,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Hidden, IsUndoEnabled = false };
        TextOptions.SetTextFormattingMode(editor, TextFormattingMode.Ideal);
        editor.Measure(new Size(box.WidthPt, double.PositiveInfinity));
        height = Math.Max(20 * box.LayoutScale, editor.DesiredSize.Height);
        editor.Arrange(new Rect(0, 0, box.WidthPt, height)); editor.UpdateLayout();
        height = Math.Max(Math.Max(20 * box.LayoutScale, editor.DesiredSize.Height), editor.Document.ContentEnd.GetNextInsertionPosition(LogicalDirection.Backward)!.GetCharacterRect(LogicalDirection.Backward).Bottom + 4 * box.LayoutScale);
        editor.Arrange(new Rect(0, 0, box.WidthPt, height)); editor.UpdateLayout();
        return editor;
    }

    private static WpfTextLayout BuildOnWorker(TextBoxAnnotation box)
    {
        // Lay out in the original typographic units, then scale the complete result. WPF's fixed precision
        // formatter must not choose a new line break merely because a page was uniformly reduced.
        if (box.LayoutScale != 1)
        {
            double s = box.LayoutScale; var original = BuildOnWorker(TextLayout.Normalized(box));
            DrawingGroup Scale(DrawingGroup input) { var group = new DrawingGroup { Transform = new ScaleTransform(s, s) }; group.Children.Add(input); group.Freeze(); return group; }
            return new(Scale(original.Drawing), original.Height * s, original.Characters.Select(c => c with
                { Bounds = new(c.Bounds.X * s, c.Bounds.Y * s, c.Bounds.Width * s, c.Bounds.Height * s) }).ToImmutableArray(), Scale(original.Highlights));
        }
        var editor = Arrange(box, out double height);
        // Force WPF's deferred OnRender without retaining a live editor or a bitmap in the returned layout.
        new System.Windows.Media.Imaging.RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32).Render(editor);
        var characters = ImmutableArray.CreateBuilder<TextLayout.Character>();
        int textOffset = 0;
        foreach (var inline in ((Paragraph)editor.Document.Blocks.FirstBlock).Inlines)
        {
            if (inline is LineBreak) { textOffset++; continue; }
            if (inline is not Run run) continue;
            int offset = 0;
            foreach (var rune in run.Text.EnumerateRunes())
            {
                int length = rune.Utf16SequenceLength;
                // Forward: at a wrap the position before the line's first character belongs to that line, not to the
                // end of the previous one (D14a fix — highlights over a wrap were one band across both lines).
                var begin = run.ContentStart.GetPositionAtOffset(offset, LogicalDirection.Forward)!;
                var end = run.ContentStart.GetPositionAtOffset(offset + length)!;
                var left = begin.GetCharacterRect(LogicalDirection.Forward);
                var right = end.GetCharacterRect(LogicalDirection.Backward);
                double x = Math.Min(left.X, right.X), width = Math.Abs(right.X - left.X);
                if (width < .001) width = Math.Max(left.Width, right.Width);
                int line = editor.Document.ContentStart.GetOffsetToPosition(begin.GetLineStartPosition(0)!);
                characters.Add(new(rune.ToString(), new(x, left.Y, Math.Max(.001, width), Math.Max(.001, left.Height)), line, textOffset + offset));
                offset += length;
            }
            textOffset += run.Text.Length;
        }
        var highlights = HighlightDrawing(box, characters);
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            if (box.BoxStyle.BackgroundOpacity > 0) context.DrawRectangle(WpfText.Brush(box.BoxStyle.BackgroundColor, box.BoxStyle.BackgroundOpacity), null, new Rect(0, 0, box.WidthPt, height));
            double border = box.LayoutScale;
            if (box.BoxStyle.BorderEnabled) context.DrawRectangle(null, new Pen(WpfText.Brush(box.BoxStyle.BorderColor, box.BoxStyle.BorderOpacity), border), new Rect(border / 2, border / 2, Math.Max(0, box.WidthPt - border), Math.Max(0, height - border)));
            context.DrawDrawing(highlights); // behind the glyphs, like a marker on paper
            Copy(editor, context);
        }
        group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, box.WidthPt, height));
        group.Freeze(); return new(group, height, characters.ToImmutable(), highlights);
    }

    private static DrawingGroup HighlightDrawing(TextBoxAnnotation box, IEnumerable<TextLayout.Character> characters)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
            foreach (var (h, lines) in TextLayout.HighlightLines(box, characters))
            {
                var geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
                foreach (var r in lines) geometry.Children.Add(new RectangleGeometry(new Rect(r.X, r.Y, r.Width, r.Height)));
                context.DrawGeometry(WpfText.Brush(h.Color, h.Opacity), null, geometry);
            }
        drawing.Freeze();
        return drawing;
    }
    private static void Copy(Visual visual, DrawingContext context)
    {
        Vector offset = VisualTreeHelper.GetOffset(visual);
        context.PushTransform(new TranslateTransform(offset.X, offset.Y));
        var transform = VisualTreeHelper.GetTransform(visual);
        if (transform is not null) context.PushTransform(transform);
        var clip = VisualTreeHelper.GetClip(visual); if (clip is not null) context.PushClip(clip);
        if (VisualTreeHelper.GetDrawing(visual) is { } drawing) Outline(drawing, context);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
            if (VisualTreeHelper.GetChild(visual, i) is Visual child) Copy(child, context);
        if (clip is not null) context.Pop();
        if (transform is not null) context.Pop(); context.Pop();
    }
    private static void Outline(Drawing drawing, DrawingContext context)
    {
        switch (drawing)
        {
            case DrawingGroup group:
                context.PushTransform(group.Transform ?? Transform.Identity); context.PushOpacity(group.Opacity);
                if (group.ClipGeometry is not null) context.PushClip(group.ClipGeometry);
                foreach (var child in group.Children) Outline(child, context);
                if (group.ClipGeometry is not null) context.Pop(); context.Pop(); context.Pop(); break;
            case GlyphRunDrawing glyph: context.DrawGeometry(glyph.ForegroundBrush, null, glyph.GlyphRun.BuildGeometry()); break;
            default: context.DrawDrawing(drawing); break;
        }
    }
}
