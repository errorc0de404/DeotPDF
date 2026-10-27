using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Services;
using PdfNotes.Text;
using DashStyle = PdfNotes.Core.Models.DashStyle;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>add text | mark | highlight-stroke | line | shape | ink | image | note</c> (cli_plan 4.6): one annotation of the
/// app's own kinds on a page, tagged for the run (author <c>ai</c>, batch id, optional source and group key, 6) and
/// written through <see cref="CliWrite"/>. Coordinates are canonical page points (3.2). A source key that an AI
/// annotation already has replaces that annotation: same ID, same stacking position (moved on top of the new page
/// when the page differs), new batch id.
/// </summary>
public static class AddCommand
{
    private static readonly string[] CommonValues = ["--page", "--source-key", "--group-key"];
    private static readonly string[] CommonFlags = ["--lock"];

    /// <summary>App defaults (spec 7.2, 9.2, 24.8, 24.9): highlighter yellow at 60 % transparency, underline red 1.5 pt.</summary>
    private static readonly RgbColor HighlightYellow = new(0xFF, 0xE6, 0x00), MarkupRed = new(0xE5, 0x39, 0x35), NoteYellow = new(0xFF, 0xD5, 0x4F);
    private const double HighlightOpacity = 0.4, MarkupWidth = 1.5;

    /// <summary>What a kind's builder makes: the annotation, an image resource, extra result fields and warnings.</summary>
    private sealed record Built(Annotation Annotation, Resource? Resource = null, JsonObject? Fields = null, JsonArray? Warnings = null);

    public static readonly CliOp Text = Op("add text", "Text box with formatted text, sized by the app's text engine", ["--rect", "--text", "--style", "--overflow"], ["--no-auto-height"], (args, doc, page) =>
        {
            var rect = Box(args.Required("--rect"), "--rect");
            if (rect.Width < 16) throw CliException.Invalid("--rect width must be at least 16 pt (the app's narrowest text box).");
            bool allow = Overflow(args);
            bool auto = !args.Flag("--no-auto-height");
            if (!auto && rect.Height <= 0) throw CliException.Invalid("--no-auto-height needs a --rect height.");
            var spans = RichText.ParseText(args.Required("--text"));
            var style = RichText.ParseStyle(args.Value("--style"), RichText.BoxStyle.Default);
            var box = RichText.Build(spans, style, rect.Width, Affine.Translation(rect.X, rect.Y));
            var report = MeasureCommand.Report(box, auto ? null : rect.Height, style);
            var warnings = (JsonArray)report["warnings"]!.DeepClone();
            report.Remove("warnings");
            var local = AnnotationGeometry.LocalBounds(box, TextLayout.Bounds);
            var final = new Rect2(rect.X + local.X, rect.Y + local.Y, local.Width, local.Height);
            if (!auto && local.Height > rect.Height + 1e-6)
                Overflowed(warnings, allow, "TEXT_OVERFLOW", "The text is taller than the box and --no-auto-height keeps its height.", new JsonObject
                {
                    ["rect"] = CliJson.Rect(rect), ["requiredHeight"] = CliJson.Num(local.Height), ["overflowHeight"] = CliJson.Num(local.Height - rect.Height),
                });
            PageOverflow(page, final, warnings, allow);
            return new(box, Fields: new JsonObject { ["rect"] = CliJson.Rect(final), ["layout"] = report }, Warnings: warnings);
        });

    public static readonly CliOp Mark = Op("add mark", "Highlight, underline or strikeout on PDF text by characters, words, quads or phrase", ["--chars", "--words", "--quads", "--find", "--within", "--occurrence", "--kind", "--color", "--opacity", "--height", "--width"], [],
        (args, doc, page) =>
        {
            string kind = args.Value("--kind") ?? "highlight";
            if (kind is not ("highlight" or "underline" or "strikeout")) throw CliException.Invalid("--kind takes highlight, underline or strikeout.");
            int given = new[] { "--chars", "--words", "--quads", "--find" }.Count(args.Has);
            if (given != 1) throw CliException.Invalid("Give the place with exactly one of --chars, --words, --quads or --find.");
            if ((args.Has("--within") || args.Has("--occurrence")) && !args.Has("--find")) throw CliException.Invalid("--within and --occurrence narrow --find.");
            var glyphs = doc.Glyphs(page);
            (int Start, int End)? range = null;
            ImmutableArray<Quad> quads;
            if (args.Value("--quads") is { } q) quads = Quads(q);
            else
            {
                range = args.Value("--chars") is { } c ? CharRange(c, glyphs.Length)
                    : args.Value("--words") is { } w ? WordRange(w, glyphs)
                    : Found(args, doc, page);
                quads = TextSelection.LineQuads(glyphs, range.Value.Start, range.Value.End);
                if (quads.IsEmpty)
                    throw new CliException("EMPTY_RANGE", "The characters have no visible glyphs to mark.", ExitCodes.Unsatisfiable,
                        new JsonObject { ["chars"] = Range(range.Value) });
            }
            string? hint = range is { } r ? TextSelection.TextOf(glyphs, r.Start, r.End) : null;
            if (hint is { Length: > ModelLimits.MaxHintLength }) hint = hint[..ModelLimits.MaxHintLength];
            Annotation mark;
            if (kind == "highlight")
            {
                if (args.Has("--width")) throw CliException.Invalid("--width is the line width of underline and strikeout; a highlight takes --height.");
                string height = args.Value("--height") ?? "auto";
                double? manual = height == "auto" ? null : Number(height, "--height", InputLimits.MinManualHighlightHeightPt, InputLimits.MaxManualHighlightHeightPt);
                mark = new PdfTextHighlight(Guid.NewGuid(), false, quads, hint, manual is null ? HighlightHeightMode.Auto : HighlightHeightMode.Manual, manual,
                    Color(args, "--color", HighlightYellow), args.Number("--opacity", HighlightOpacity, 0.01, 1));
            }
            else
            {
                if (args.Has("--height")) throw CliException.Invalid("--height is the height of a highlight; underline and strikeout take --width.");
                mark = new PdfTextMarkup(Guid.NewGuid(), false, quads, hint, kind == "underline" ? TextMarkupKind.Underline : TextMarkupKind.StrikeOut,
                    args.Number("--width", MarkupWidth, ModelValidator.MinMarkupWidthPt, ModelValidator.MaxMarkupWidthPt),
                    Color(args, "--color", MarkupRed), args.Number("--opacity", 1, 0.01, 1));
            }
            return new(mark, Fields: new JsonObject { ["chars"] = range is { } rr ? Range(rr) : null, ["text"] = hint });
        });

    public static readonly CliOp HighlightStroke = Op("add highlight-stroke", "Free highlighter stroke or filled highlight rectangle", ["--points", "--rect", "--color", "--width", "--opacity"], [], (args, doc, page) =>
        {
            if (args.Has("--points") == args.Has("--rect")) throw CliException.Invalid("Give --points (a stroke) or --rect (a filled rectangle).");
            double width = args.Number("--width", InputLimits.DefaultFreeHighlightWidthPt, InputLimits.MinFreeHighlightWidthPt, InputLimits.MaxFreeHighlightWidthPt);
            var color = Color(args, "--color", HighlightYellow);
            double opacity = args.Number("--opacity", HighlightOpacity, 0.01, 1);
            if (args.Value("--rect") is { } r)
            {
                // A filled rectangle is a stroke made only of its painted region, like an eraser piece (schema v3).
                var b = CliArgs.ParseRect(r, "--rect");
                ImmutableArray<Point2> ring = [new(b.X, b.Y), new(b.X + b.Width, b.Y), new(b.X + b.Width, b.Y + b.Height), new(b.X, b.Y + b.Height)];
                return new(new FreeHighlightStroke(Guid.NewGuid(), false, Affine.Identity, [], width, color, opacity) { Outline = [ring] });
            }
            return new(new FreeHighlightStroke(Guid.NewGuid(), false, Affine.Identity, Points(args.Value("--points")!, "--points", 1), width, color, opacity));
        });

    public static readonly CliOp Ink = Op("add ink", "Pen stroke through points", ["--points", "--color", "--width", "--opacity"], [], (args, doc, page) =>
            new(new InkStroke(Guid.NewGuid(), false, Affine.Identity, Points(args.Required("--points"), "--points", 1),
                args.Number("--width", InputLimits.DefaultPenWidthPt, InputLimits.MinPenWidthPt, InputLimits.MaxPenWidthPt),
                Color(args, "--color", RgbColor.Black), args.Number("--opacity", 1, 0.01, 1))));

    public static readonly CliOp Line = Op("add line", "Straight line or Bézier curve with optional arrow heads", ["--points", "--color", "--width", "--opacity", "--dash"], ["--start-arrow", "--end-arrow"], (args, doc, page) =>
        {
            var points = Points(args.Required("--points"), "--points", 2);
            if (points.Length > 4) throw CliException.Invalid("--points of a line: start, up to two Bézier control points, end (2-4 points).");
            if (points[0].DistanceTo(points[^1]) <= 1e-6 && points.Length == 2) throw CliException.Invalid("A straight line needs two different points.");
            var kind = points.Length switch { 2 => LineKind.Straight, 3 => LineKind.Quadratic, _ => LineKind.Cubic };
            return new(new LineAnnotation(Guid.NewGuid(), false, Affine.Identity, kind, points[0], points[^1], points[1..^1],
                args.Number("--width", InputLimits.DefaultLineWidthPt, InputLimits.MinLineWidthPt, InputLimits.MaxLineWidthPt), Dash(args),
                args.Flag("--start-arrow"), args.Flag("--end-arrow"), Color(args, "--color", RgbColor.Black), args.Number("--opacity", 1, 0.01, 1)));
        });

    public static readonly CliOp Shape = Op("add shape", "Rectangle, ellipse, triangle or polygon", ["--kind", "--rect", "--points", "--fill", "--fill-opacity", "--border-color", "--border-width", "--border-opacity", "--dash"],
            ["--no-border"], (args, doc, page) =>
        {
            var kind = args.Required("--kind") switch
            {
                "rect" => ShapeKind.Rectangle,
                "ellipse" => ShapeKind.Ellipse,
                "triangle" => ShapeKind.Triangle,
                "polygon" => ShapeKind.Polygon,
                _ => throw CliException.Invalid("--kind takes rect, ellipse, triangle or polygon."),
            };
            var d = ShapeStyle.Default;
            var style = new ShapeStyle(Color(args, "--fill", d.FillColor), args.Number("--fill-opacity", d.FillOpacity, 0, 1), !args.Flag("--no-border"),
                Color(args, "--border-color", d.BorderColor), args.Number("--border-width", d.BorderWidthPt, InputLimits.MinLineWidthPt, InputLimits.MaxLineWidthPt),
                args.Number("--border-opacity", d.BorderOpacity, 0.01, 1), args.Has("--dash") ? Dash(args) : d.BorderDash);
            if (style.FillOpacity <= 0 && !style.BorderEnabled) throw CliException.Invalid("A shape without fill and border is invisible.");
            // Drawn upright on the page as it is shown, like the app's shape tool (the frame of a turned page).
            var frame = page.Setup.Frame;
            if (kind == ShapeKind.Polygon)
            {
                if (args.Has("--rect")) throw CliException.Invalid("A polygon takes --points, not --rect.");
                var points = Points(args.Required("--points"), "--points", 3);
                return new(new ShapeAnnotation(Guid.NewGuid(), false, frame.DisplayToCanonical, kind, default,
                    [.. points.Select(p => frame.CanonicalToDisplay.Transform(p))], style));
            }
            if (args.Has("--points")) throw CliException.Invalid("Rectangles, ellipses and triangles take --rect, not --points.");
            var rect = CliArgs.ParseRect(args.Required("--rect"), "--rect");
            var corners = AnnotationGeometry.Corners(rect, frame.CanonicalToDisplay);
            double x0 = corners.Min(c => c.X), y0 = corners.Min(c => c.Y);
            var box = new Rect2(x0, y0, corners.Max(c => c.X) - x0, corners.Max(c => c.Y) - y0);
            return new(new ShapeAnnotation(Guid.NewGuid(), false, frame.DisplayToCanonical, kind, box, [], style));
        });

    public static readonly CliOp Image = Op("add image", "PNG, JPEG or BMP image", ["--file", "--rect"], [], (args, doc, page) =>
        {
            string path = CliPaths.Full(args.Required("--file"));
            if (!File.Exists(path)) throw new CliException("FILE_NOT_FOUND", $"The image {path} does not exist.", ExitCodes.InvalidInput, new JsonObject { ["path"] = path });
            byte[] bytes = File.ReadAllBytes(path);
            Resource resource;
            BitmapFrame frame;
            try
            {
                resource = ImageResources.Create(bytes);
                frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or FileFormatException or ArgumentException)
            {
                throw new CliException("INVALID_IMAGE", $"The file is not a PNG, JPEG or BMP image the app can show ({e.Message}).", ExitCodes.InvalidInput,
                    new JsonObject { ["path"] = path });
            }
            int pw = frame.PixelWidth, ph = frame.PixelHeight;
            if (args.Value("--rect") is not { } r)
                return new(ImagePlacement.Place(resource, pw, ph, frame.DpiX, page), resource, new JsonObject { ["pixelSize"] = new JsonArray(pw, ph) });
            var rect = Box(r, "--rect");
            double height = rect.Height > 0 ? rect.Height : rect.Width * ph / pw;
            return new(new ImageAnnotation(Guid.NewGuid(), false, Affine.Translation(rect.X, rect.Y), resource.Id, new(0, 0, rect.Width, height)), resource,
                new JsonObject { ["pixelSize"] = new JsonArray(pw, ph) });
        });

    public static readonly CliOp Note = Op("add note", "Sticky note icon with text", ["--at", "--text", "--color"], [], (args, doc, page) =>
        {
            var at = CliArgs.ParseNumbers(args.Required("--at"), 2, "--at");
            string text = args.Required("--text").ReplaceLineEndings("\n");
            if (string.IsNullOrWhiteSpace(text)) throw CliException.Invalid("--text of a note is empty.");
            return new(new StickyNote(Guid.NewGuid(), false, Affine.Translation(at[0], at[1]), text, Color(args, "--color", NoteYellow)));
        });

    public static readonly CliOp[] Ops = [Text, Mark, HighlightStroke, Line, Shape, Ink, Image, Note];

    /// <summary>
    /// Shared frame: the page, the kind's annotation, lock and tag, the add (or replacement of the AI annotation with the
    /// same source key), then the report.
    /// </summary>
    private static CliOp Op(string name, string summary, string[] values, string[] flags, Func<CliArgs, CliDocument, Page, Built> build) =>
        new(name, summary, [.. CommonValues, .. values], [.. CommonFlags, .. flags], (args, context, result) =>
        {
            var tag = Tag(context, args.Value("--source-key"), args.Value("--group-key"));
            var doc = context.Doc;
            var page = doc.ResolvePage(args.Value("--page") ?? throw CliException.Invalid("Give the page with -p <page>."));
            var built = build(args, doc, page);
            var annotation = args.Flag("--lock") ? built.Annotation with { Locked = true } : built.Annotation;
            var placed = Place(context, page, annotation, built.Resource, tag);
            Report(doc, result, placed);
            foreach (var (field, value) in built.Fields ?? []) result[field] = value?.DeepClone();
            result["warnings"] = built.Warnings?.DeepClone() ?? new JsonArray();
            return placed.Previous is null ? [page.Id] : [placed.Previous.Id, page.Id];
        });

    /// <summary>The tag of an annotation this run makes: author <c>ai</c>, the run's batch id and the keys given.</summary>
    public static AnnotationTag Tag(CliContext context, string? sourceKey, string? groupKey)
    {
        var tag = new AnnotationTag(AnnotationTag.AiAuthor, context.BatchId, sourceKey, groupKey);
        if (tag.Validate() is { } invalid) throw CliException.Invalid($"--source-key or --group-key is empty, too long or has control characters ({invalid})");
        return tag;
    }

    /// <summary>Where <see cref="Place"/> put an annotation: its page, the annotation as stored, and the page it left when a replacement moved it.</summary>
    public sealed record Placed(Page Page, Annotation Annotation, bool Replaced, Page? Previous);

    /// <summary>
    /// Puts <paramref name="annotation"/> on top of <paramref name="page"/> with <paramref name="tag"/>, as one edit. When an
    /// AI annotation already has the tag's source key it is replaced instead (cli_plan 6): same ID and stacking position,
    /// on top of the new page when the page differs; an image resource the replacement no longer uses is dropped.
    /// </summary>
    public static Placed Place(CliContext context, Page page, Annotation annotation, Resource? resource, AnnotationTag tag)
    {
        var doc = context.Doc;
        var model = doc.Model;
        var operations = new List<EditOperation>();
        Page? previous = null;
        bool replaced = false;
        if (tag.SourceKey is { } key && context.KeysThisRun is { } keys && !keys.Add(key))
            throw new CliException("DUPLICATE_SOURCE_KEY", $"Source key '{key}' is made twice in this apply; each key names one annotation.", ExitCodes.Unsatisfiable,
                new JsonObject { ["sourceKey"] = key });
        if (tag.SourceKey is { } sourceKey && Owner(doc, sourceKey) is var (oldPage, old))
        {
            if (AnnotListCommand.KindName(old) != AnnotListCommand.KindName(annotation))
                throw new CliException("SOURCE_KEY_KIND_MISMATCH", $"Source key '{sourceKey}' belongs to a {AnnotListCommand.KindName(old)} annotation; delete it to make a {AnnotListCommand.KindName(annotation)}.",
                    ExitCodes.Unsatisfiable, new JsonObject { ["sourceKey"] = sourceKey, ["id"] = old.Id.ToString(), ["kind"] = AnnotListCommand.KindName(old) });
            annotation = annotation with { Id = old.Id };
            replaced = true;
            if (resource is { } added && !model.Resources.ContainsKey(added.Id)) operations.Add(new AddResourceOperation(added));
            if (oldPage.Id == page.Id) operations.Add(new ReplaceAnnotationOperation(page.Id, old, annotation));
            else
            {
                operations.Add(new RemoveAnnotationOperation(oldPage.Id, oldPage.IndexOf(old.Id), old));
                operations.Add(new InsertAnnotationOperation(page.Id, page.Annotations.Count, annotation));
                previous = oldPage;
            }
            if (old is ImageAnnotation image && image.ResourceId != resource?.Id &&
                !model.AllAnnotations.Any(a => a.Id != old.Id && a is ImageAnnotation other && other.ResourceId == image.ResourceId))
                operations.Add(new RemoveResourceOperation(model.Resources[image.ResourceId]));
            operations.Add(new SetTagOperation(old.Id, model.TagOf(old.Id), tag));
        }
        else
        {
            if (resource is { } added && !model.Resources.ContainsKey(added.Id)) operations.Add(new AddResourceOperation(added));
            operations.Add(new InsertAnnotationOperation(page.Id, page.Annotations.Count, annotation));
            operations.Add(new SetTagOperation(annotation.Id, null, tag));
        }
        if (ModelValidator.ValidateAnnotation(annotation, id => id == resource?.Id || model.Resources.ContainsKey(id)) is { } error)
            throw CliException.Invalid($"The annotation is not valid: {error}");
        doc.Document.Session.Execute(new EditCommand("undo.addAnnotation", operations));
        var now = doc.Model.GetPage(page.Id);
        return new(now, now.Annotations.First(a => a.Id == annotation.Id), replaced, previous);
    }

    /// <summary>The result fields every add (and copy) reports: <c>annotation</c>, <c>replaced</c>, <c>previousPage</c>.</summary>
    public static void Report(CliDocument doc, JsonObject result, Placed placed)
    {
        result["annotation"] = AnnotListCommand.Describe(doc, placed.Page, placed.Annotation, placed.Page.IndexOf(placed.Annotation.Id));
        result["replaced"] = placed.Replaced;
        result["previousPage"] = placed.Previous is { } previous ? new JsonObject { ["page"] = doc.NumberOf(previous), ["pageId"] = previous.Id.ToString() } : null;
    }

    /// <summary>The AI annotation with <paramref name="key"/>, null when there is none; two or more are refused (cli_plan 6).</summary>
    public static (Page Page, Annotation Annotation)? Owner(CliDocument doc, string key)
    {
        var model = doc.Model;
        var found = model.Pages.SelectMany(p => p.Annotations.Select(a => (Page: p, Annotation: a)))
            .Where(x => model.TagOf(x.Annotation.Id) is { Author: AnnotationTag.AiAuthor } t && t.SourceKey == key).ToList();
        if (found.Count > 1)
            throw new CliException("DUPLICATE_SOURCE_KEY", $"Source key '{key}' is on {found.Count} annotations; delete the extra ones first.", ExitCodes.Unsatisfiable,
                new JsonObject { ["sourceKey"] = key, ["ids"] = CliJson.Array(found.Select(x => (JsonNode?)x.Annotation.Id.ToString())) });
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary><c>--find</c> on the page: exactly one exact candidate (after <c>--within</c> and <c>--occurrence</c>) or an error (cli_plan 4.6.1).</summary>
    private static (int Start, int End) Found(CliArgs args, CliDocument doc, Page page)
    {
        string query = args.Value("--find")!;
        string normalized = TextMatch.Normalize(query);
        if (normalized.Length == 0) throw CliException.Invalid("--find has no searchable characters.");
        var within = args.Value("--within") is { } w ? CliArgs.ParseRect(w, "--within") : (Rect2?)null;
        var candidates = FindCommand.Matches(doc, page, normalized, false, 1, 20, within);
        if (args.Has("--occurrence"))
        {
            int n = args.Integer("--occurrence", 1, 1, int.MaxValue);
            candidates = n <= candidates.Count ? [candidates[n - 1]] : [];
        }
        var details = new JsonObject
        {
            ["query"] = query, ["normalizedQuery"] = normalized, ["page"] = doc.NumberOf(page), ["pageId"] = page.Id.ToString(), ["count"] = candidates.Count,
            ["matches"] = CliJson.Array(candidates.Select(c => (JsonNode?)c.Json.DeepClone())),
        };
        if (candidates.Count == 0) throw new CliException("NOT_FOUND", $"'{query}' is not on page {doc.NumberOf(page)}.", ExitCodes.Unsatisfiable, details);
        if (candidates.Count > 1)
            throw new CliException("AMBIGUOUS_MATCH", $"'{query}' is on page {doc.NumberOf(page)} {candidates.Count} times; narrow it with --within or --occurrence, or mark the chosen candidate's --chars.",
                ExitCodes.Unsatisfiable, details);
        return (candidates[0].Start, candidates[0].End);
    }

    /// <summary><c>s:e</c> characters of the text layer (0-based, end excluded, cli_plan 3.4).</summary>
    internal static (int Start, int End) CharRange(string text, int count)
    {
        var (s, e) = Span(text, "--chars");
        if (e > count) throw new CliException("RANGE_OUT_OF_TEXT", $"--chars {text} is past the page's {count} characters.", ExitCodes.InvalidInput,
            new JsonObject { ["count"] = count });
        return (s, e);
    }

    /// <summary><c>s:e</c> words of the text layer (the <c>text</c> command's words) as their character range.</summary>
    private static (int Start, int End) WordRange(string text, IReadOnlyList<TextGlyph> glyphs)
    {
        var (s, e) = Span(text, "--words");
        var words = TextCommand.Words(glyphs).ToList();
        if (e > words.Count) throw new CliException("RANGE_OUT_OF_TEXT", $"--words {text} is past the page's {words.Count} words.", ExitCodes.InvalidInput,
            new JsonObject { ["count"] = words.Count });
        return (words[s].Start, words[e - 1].End);
    }

    private static (int Start, int End) Span(string text, string option)
    {
        var parts = text.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int s) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int e) || e <= s)
            throw CliException.Invalid($"{option} takes start:end (0-based, end excluded, end > start).");
        return (s, e);
    }

    private static JsonObject Range((int Start, int End) r) => new() { ["start"] = r.Start, ["end"] = r.End };

    /// <summary><c>x,y,w,h</c> where the height may be 0 (text: set by the layout, image: by the picture's aspect).</summary>
    internal static Rect2 Box(string text, string option)
    {
        var n = CliArgs.ParseNumbers(text, 4, option);
        if (n[2] <= 0 || n[3] < 0) throw CliException.Invalid($"Option {option} needs a positive width and a height of 0 or more.");
        return new(n[0], n[1], n[2], n[3]);
    }

    /// <summary>A JSON list of <c>[x, y]</c> points, inline or <c>@file</c>.</summary>
    internal static ImmutableArray<Point2> Points(string value, string option, int min)
    {
        var points = PointList(Json(value, option), option);
        if (points.Length < min) throw CliException.Invalid($"{option} needs at least {min} point{(min == 1 ? "" : "s")}.");
        if (points.Length > ModelLimits.MaxStrokePoints) throw CliException.Invalid($"{option} has too many points.");
        return points;
    }

    /// <summary>A JSON list of quads (each <c>[[x,y]×4]</c>: upper-left, upper-right, lower-left, lower-right), inline or <c>@file</c>.</summary>
    private static ImmutableArray<Quad> Quads(string value)
    {
        if (Json(value, "--quads") is not JsonArray list || list.Count == 0 || list.Count > ModelLimits.MaxQuads)
            throw CliException.Invalid("--quads must be a non-empty JSON list of quads [[x,y],[x,y],[x,y],[x,y]].");
        return [.. list.Select(q => PointList(q, "--quads") is { Length: 4 } p ? new Quad(p[0], p[1], p[2], p[3])
            : throw CliException.Invalid("Each quad of --quads has four points."))];
    }

    internal static JsonNode? Json(string value, string option)
    {
        if (value.StartsWith('@'))
        {
            string path = CliPaths.Full(value[1..]);
            if (!File.Exists(path)) throw new CliException("FILE_NOT_FOUND", $"The file {path} of {option} does not exist.");
            value = File.ReadAllText(path);
        }
        try { return JsonNode.Parse(value); }
        catch (JsonException e) { throw CliException.Invalid($"{option} is not valid JSON: {e.Message}"); }
    }

    private static ImmutableArray<Point2> PointList(JsonNode? node, string option)
    {
        if (node is not JsonArray list) throw CliException.Invalid($"{option} must be a JSON list of [x, y] points.");
        var points = ImmutableArray.CreateBuilder<Point2>(list.Count);
        foreach (var item in list)
        {
            if (item is not JsonArray { Count: 2 } xy || !TryNumber(xy[0], out double x) || !TryNumber(xy[1], out double y) ||
                Math.Abs(x) > ModelLimits.MaxCoordinatePt || Math.Abs(y) > ModelLimits.MaxCoordinatePt)
                throw CliException.Invalid($"{option} has a point that is not [x, y] with finite numbers.");
            points.Add(new(x, y));
        }
        return points.MoveToImmutable();
    }

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = node is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : double.NaN;
        return double.IsFinite(value);
    }

    internal static double Number(string text, string option, double min, double max) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) && v >= min && v <= max
            ? v : throw CliException.Invalid($"{option} must be auto or a number from {min} to {max}.");

    internal static RgbColor Color(CliArgs args, string option, RgbColor fallback) =>
        args.Value(option) is not { } text ? fallback
            : RgbColor.TryParse(text.ToUpperInvariant(), out var c) ? c : throw CliException.Invalid($"{option} must be a color #RRGGBB.");

    internal static DashStyle Dash(CliArgs args) => args.Value("--dash") switch
    {
        null or "solid" => DashStyle.Solid,
        "dash" => DashStyle.Dash,
        "dot" => DashStyle.Dot,
        "dash-dot" => DashStyle.DashDot,
        _ => throw CliException.Invalid("--dash takes solid, dash, dot or dash-dot."),
    };

    internal static bool Overflow(CliArgs args) => args.Value("--overflow") switch
    {
        null or "error" => false,
        "allow" => true,
        _ => throw CliException.Invalid("--overflow takes error or allow."),
    };

    /// <summary>
    /// cli_plan 4.6 overflow policy: the final rectangle off the page fails with <c>PAGE_OVERFLOW</c> (exit 2), or with
    /// <c>--overflow allow</c> is made and reported as a warning. Nothing is shrunk, cut or rewrapped.
    /// </summary>
    internal static void PageOverflow(Page page, Rect2 rect, JsonArray warnings, bool allow)
    {
        double w = page.Setup.WidthPt, h = page.Setup.HeightPt;
        double left = Math.Max(0, -rect.X), top = Math.Max(0, -rect.Y);
        double right = Math.Max(0, rect.X + rect.Width - w), bottom = Math.Max(0, rect.Y + rect.Height - h);
        if (left + top + right + bottom <= 1e-6) return;
        Overflowed(warnings, allow, "PAGE_OVERFLOW", "The final rectangle reaches past the page.", new JsonObject
        {
            ["rect"] = CliJson.Rect(rect), ["page"] = CliJson.Rect(0, 0, w, h),
            ["overflow"] = new JsonObject { ["left"] = CliJson.Num(left), ["top"] = CliJson.Num(top), ["right"] = CliJson.Num(right), ["bottom"] = CliJson.Num(bottom) },
        });
    }

    internal static void Overflowed(JsonArray warnings, bool allow, string code, string message, JsonObject details)
    {
        if (!allow) throw new CliException(code, message + " Use --overflow allow to make it anyway.", ExitCodes.Unsatisfiable, details);
        warnings.Add(new JsonObject { ["code"] = code, ["message"] = message, ["details"] = details });
    }
}
