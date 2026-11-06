using System.Globalization;
using System.Text.Json.Nodes;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>annot update | delete | order | lock | copy</c> (cli_plan 4.7). Only annotations the CLI made (tag author
/// <c>ai</c>) are changed or deleted; the user's writing and other annotations are read-only (<c>FORBIDDEN</c>) unless
/// <c>--allow-untagged</c>. An annotation is named by <c>--id</c> or by its source key. A changed AI annotation takes the
/// run's batch id. Locked annotations are not changed or deleted (<c>LOCKED_ANNOTATION</c>) until unlocked, except that
/// <c>delete --include-locked</c> takes them too.
/// </summary>
public static class AnnotCommand
{
    private static readonly string[] Geometry = ["--scale", "--rotate", "--move", "--at"];

    public static readonly CliOp Update = new("annot update", "Changes text, style, size, position or rotation of an AI annotation",
        ["--id", "--source-key", "--text", "--style", "--width", "--color", "--opacity", "--height", "--dash", "--fill", "--fill-opacity", "--border",
         "--border-color", "--border-width", "--border-opacity", "--start-arrow", "--end-arrow", "--size", .. Geometry, "--overflow"],
        ["--allow-untagged"], UpdateEdit);

    public static readonly CliOp Delete = new("annot delete", "Deletes AI annotations by ID, batch, source key or group key",
        ["--id", "--batch-id", "--source-key", "--group-key", "--page", "--pages"], ["--allow-untagged", "--include-locked"], DeleteEdit);

    public static readonly CliOp Order = new("annot order", "Moves an AI annotation in the stacking order of its page",
        ["--id", "--source-key", "--to"], ["--allow-untagged"], OrderEdit);

    public static readonly CliOp Lock = new("annot lock", "Locks or unlocks an AI annotation",
        ["--id", "--source-key"], ["--off", "--allow-untagged"], LockEdit);

    public static readonly CliOp Copy = new("annot copy", "Copies an annotation to a page as a new AI annotation",
        ["--id", "--from-key", "--to", "--at", "--source-key", "--group-key"], ["--lock"], CopyEdit);

    public static readonly CliOp[] Ops = [Update, Delete, Order, Lock, Copy];

    /// <summary>
    /// The options <c>annot update</c> takes for each kind (besides <c>--id</c>, <c>--source-key</c>, <c>--allow-untagged</c>).
    /// <c>--width</c> is the box width of a text box and the line width of the others.
    /// </summary>
    private static string[] UpdateOptions(Annotation a) => a switch
    {
        TextBoxAnnotation => ["--text", "--style", "--width", "--overflow", .. Geometry],
        StickyNote => ["--text", "--color", "--move", "--at"],
        PdfTextHighlight => ["--color", "--opacity", "--height"],
        PdfTextMarkup => ["--color", "--opacity", "--width"],
        StrokeAnnotation => ["--color", "--opacity", "--width", .. Geometry],
        LineAnnotation => ["--color", "--opacity", "--width", "--dash", "--start-arrow", "--end-arrow", .. Geometry],
        ShapeAnnotation => ["--fill", "--fill-opacity", "--border", "--border-color", "--border-width", "--border-opacity", "--dash", "--size", .. Geometry],
        ImageAnnotation => ["--size", .. Geometry],
        _ => [],
    };

    private static IEnumerable<Guid> UpdateEdit(CliArgs args, CliContext context, JsonObject result)
    {
        var doc = context.Doc;
        var (page, old) = Target(args, doc, "--source-key");
        Protect(doc, old, args);
        string kind = AnnotListCommand.KindName(old);
        var allowed = UpdateOptions(old);
        var given = Update.Values.Where(o => o is not ("--id" or "--source-key") && args.Has(o)).ToList();
        if (given.Count == 0) throw CliException.Invalid("Nothing to change: give the options to change (see deot schema annot update).");
        foreach (string option in given)
            if (!allowed.Contains(option))
                throw Geometry.Contains(option) || option == "--size"
                    ? new CliException("NOT_TRANSFORMABLE", $"A {kind} annotation cannot take {option}: {Why(old)}", ExitCodes.InvalidInput, new JsonObject { ["kind"] = kind, ["option"] = option })
                    : CliException.Invalid($"{option} does not apply to a {kind} annotation; it takes {string.Join(", ", allowed)}.", new JsonObject { ["kind"] = kind, ["option"] = option });
        if (old.Locked) throw new CliException("LOCKED_ANNOTATION", "The annotation is locked; unlock it first (annot lock --off).", ExitCodes.Unsatisfiable,
            new JsonObject { ["id"] = old.Id.ToString() });
        if (args.Has("--move") && args.Has("--at")) throw CliException.Invalid("Give --move or --at, not both.");

        var warnings = new JsonArray();
        var updated = Content(args, old, warnings, result);
        updated = Transformed(args, updated);
        if (updated is TextBoxAnnotation box)
        {
            var bounds = AnnotListCommand.Bounds(box);
            AddCommand.PageOverflow(page, bounds, warnings, AddCommand.Overflow(args));
            result["rect"] = CliJson.Rect(bounds);
        }
        if (ModelValidator.ValidateAnnotation(updated, id => doc.Model.Resources.ContainsKey(id)) is { } error)
            throw CliException.Invalid($"The changed annotation is not valid: {error}");

        bool changed = !updated.Equals(old);
        if (changed)
        {
            var operations = new List<EditOperation> { new ReplaceAnnotationOperation(page.Id, old, updated) };
            if (doc.Model.TagOf(old.Id) is { } tag) operations.Add(new SetTagOperation(old.Id, tag, tag with { BatchId = context.BatchId }));
            doc.Document.Session.Execute(new EditCommand("undo.changeProperties", operations));
        }
        var now = doc.Model.GetPage(page.Id);
        result["annotation"] = AnnotListCommand.Describe(doc, now, now.Annotations[now.IndexOf(old.Id)], now.IndexOf(old.Id));
        result["changed"] = changed;
        result["warnings"] = warnings;
        return changed ? [page.Id] : [];
    }

    /// <summary>The kind's own fields changed by the options given (text, style, colors, widths, arrows, dash).</summary>
    private static Annotation Content(CliArgs args, Annotation a, JsonArray warnings, JsonObject result)
    {
        switch (a)
        {
            case TextBoxAnnotation t:
            {
                double width = args.Number("--width", t.WidthPt, 16, ModelLimits.MaxLengthPt);
                var box = t with { WidthPt = width };
                RichText.BoxStyle style;
                if (args.Value("--text") is { } text)
                {
                    style = RichText.ParseStyle(args.Value("--style"), RichText.StyleOf(t));
                    box = RichText.Build(RichText.ParseText(text), style, width, t.Transform, t.Id) with { LayoutScale = t.LayoutScale };
                }
                else
                {
                    if (args.Value("--style") is { } s) box = RichText.Restyle(box, RichText.StyleObject(s));
                    style = RichText.StyleOf(box);
                }
                if (args.Has("--text") || args.Has("--style") || args.Has("--width"))
                {
                    var report = MeasureCommand.Report(box, null, style);
                    foreach (var w in (JsonArray)report["warnings"]!) warnings.Add(w!.DeepClone());
                    report.Remove("warnings");
                    result["layout"] = report;
                }
                return box;
            }
            case StickyNote n:
            {
                string text = args.Value("--text")?.ReplaceLineEndings("\n") ?? n.Text;
                if (string.IsNullOrWhiteSpace(text)) throw CliException.Invalid("--text of a note is empty.");
                return n with { Text = text, Color = AddCommand.Color(args, "--color", n.Color) };
            }
            case PdfTextHighlight h:
            {
                var (mode, manual) = (h.HeightMode, h.ManualHeightPt);
                if (args.Value("--height") is { } height)
                    (mode, manual) = height == "auto" ? (HighlightHeightMode.Auto, (double?)null)
                        : (HighlightHeightMode.Manual, AddCommand.Number(height, "--height", InputLimits.MinManualHighlightHeightPt, InputLimits.MaxManualHighlightHeightPt));
                return h with { Color = AddCommand.Color(args, "--color", h.Color), Opacity = args.Number("--opacity", h.Opacity, 0.01, 1), HeightMode = mode, ManualHeightPt = manual };
            }
            case PdfTextMarkup m:
                return m with
                {
                    Color = AddCommand.Color(args, "--color", m.Color), Opacity = args.Number("--opacity", m.Opacity, 0.01, 1),
                    WidthPt = args.Number("--width", m.WidthPt, ModelValidator.MinMarkupWidthPt, ModelValidator.MaxMarkupWidthPt),
                };
            case FreeHighlightStroke s:
                return s with
                {
                    Color = AddCommand.Color(args, "--color", s.Color), Opacity = args.Number("--opacity", s.Opacity, 0.01, 1),
                    WidthPt = args.Number("--width", s.WidthPt, InputLimits.MinFreeHighlightWidthPt, InputLimits.MaxFreeHighlightWidthPt),
                };
            case InkStroke s:
                return s with
                {
                    Color = AddCommand.Color(args, "--color", s.Color), Opacity = args.Number("--opacity", s.Opacity, 0.01, 1),
                    WidthPt = args.Number("--width", s.WidthPt, InputLimits.MinPenWidthPt, InputLimits.MaxPenWidthPt),
                };
            case LineAnnotation l:
                return l with
                {
                    Color = AddCommand.Color(args, "--color", l.Color), Opacity = args.Number("--opacity", l.Opacity, 0.01, 1),
                    WidthPt = args.Number("--width", l.WidthPt, InputLimits.MinLineWidthPt, InputLimits.MaxLineWidthPt),
                    Dash = args.Has("--dash") ? AddCommand.Dash(args) : l.Dash,
                    StartArrow = OnOff(args, "--start-arrow") ?? l.StartArrow, EndArrow = OnOff(args, "--end-arrow") ?? l.EndArrow,
                };
            case ShapeAnnotation sh:
            {
                var st = sh.Style;
                var style = new ShapeStyle(AddCommand.Color(args, "--fill", st.FillColor), args.Number("--fill-opacity", st.FillOpacity, 0, 1),
                    OnOff(args, "--border") ?? st.BorderEnabled, AddCommand.Color(args, "--border-color", st.BorderColor),
                    args.Number("--border-width", st.BorderWidthPt, InputLimits.MinLineWidthPt, InputLimits.MaxLineWidthPt),
                    args.Number("--border-opacity", st.BorderOpacity, 0.01, 1), args.Has("--dash") ? AddCommand.Dash(args) : st.BorderDash);
                if (style.FillOpacity <= 0 && !style.BorderEnabled) throw CliException.Invalid("A shape without fill and border is invisible.");
                var shape = sh with { Style = style };
                if (args.Value("--size") is { } size)
                {
                    // The outline's own box (without the border), stretched from its top-left corner in the shape's frame.
                    var (w, h) = Size(size);
                    var outline = ShapeGeometry.Bounds(shape with { Style = style with { BorderEnabled = false } });
                    shape = AnnotationGeometry.ResizeShape(shape, new(outline.X, outline.Y), w / outline.Width, h / outline.Height);
                }
                return shape;
            }
            case ImageAnnotation img:
            {
                if (args.Value("--size") is not { } size) return img;
                var (w, h) = Size(size);
                var b = img.LocalBounds;
                return AnnotationGeometry.ResizeImage(img, new(b.X, b.Y), w / b.Width, h / b.Height);
            }
            default:
                return a;
        }
    }

    /// <summary><c>--scale</c> from the bounds' top-left, <c>--rotate</c> about their center, then <c>--move</c> or <c>--at</c> (the bounds' new top-left).</summary>
    private static Annotation Transformed(CliArgs args, Annotation a)
    {
        if (args.Has("--scale"))
        {
            var b = AnnotListCommand.Bounds(a);
            a = AnnotationGeometry.Similarity(a, new(b.X, b.Y), args.Number("--scale", 1, 0.01, 100));
        }
        if (args.Has("--rotate"))
        {
            var b = AnnotListCommand.Bounds(a);
            a = AnnotationGeometry.Similarity(a, new(b.X + b.Width / 2, b.Y + b.Height / 2), 1, args.Number("--rotate", 0, -360, 360) * Math.PI / 180);
        }
        if (args.Value("--move") is { } move)
        {
            var d = CliArgs.ParseNumbers(move, 2, "--move");
            a = AnnotationGeometry.Similarity(a, default, move: new(d[0], d[1]));
        }
        if (args.Value("--at") is { } at) a = MovedTo(a, CliArgs.ParseNumbers(at, 2, "--at"));
        return a;
    }

    private static Annotation MovedTo(Annotation a, double[] at)
    {
        var b = AnnotListCommand.Bounds(a);
        return AnnotationGeometry.Similarity(a, default, move: new(at[0] - b.X, at[1] - b.Y));
    }

    private static IEnumerable<Guid> DeleteEdit(CliArgs args, CliContext context, JsonObject result)
    {
        var doc = context.Doc;
        var model = doc.Model;
        string[] selectors = ["--id", "--batch-id", "--source-key", "--group-key"];
        if (selectors.Count(args.Has) != 1) throw CliException.Invalid("Give what to delete with exactly one of --id, --batch-id, --source-key or --group-key.");
        var targets = new List<(Page Page, Annotation Annotation)>();
        if (args.Value("--id") is { } ids)
        {
            if (args.Has("--page") || args.Has("--pages")) throw CliException.Invalid("-p and --pages narrow --batch-id, --source-key and --group-key, not --id.");
            foreach (string id in ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct())
            {
                var found = Find(doc, id);
                Protect(doc, found.Annotation, args);
                targets.Add(found);
            }
        }
        else
        {
            if (args.Flag("--allow-untagged")) throw CliException.Invalid("--allow-untagged is for --id: the other selectors only find AI annotations.");
            var pages = args.Has("--page") || args.Has("--pages") ? doc.SelectPages(args).Select(p => p.Id).ToHashSet() : null;
            string? batch = args.Value("--batch-id"), source = args.Value("--source-key"), group = args.Value("--group-key");
            foreach (var page in model.Pages.Where(p => pages is null || pages.Contains(p.Id)))
                foreach (var a in page.Annotations)
                    if (model.TagOf(a.Id) is { Author: AnnotationTag.AiAuthor } t &&
                        (batch is null || t.BatchId == batch) && (source is null || t.SourceKey == source) && (group is null || t.GroupKey == group))
                        targets.Add((page, a));
        }
        var locked = targets.Where(t => t.Annotation.Locked).ToList();
        if (locked.Count > 0 && !args.Flag("--include-locked"))
            throw new CliException("LOCKED_ANNOTATION", $"{locked.Count} of the annotations are locked; unlock them or add --include-locked.", ExitCodes.Unsatisfiable,
                new JsonObject { ["ids"] = CliJson.Array(locked.Select(t => (JsonNode?)t.Annotation.Id.ToString())) });

        var deleted = CliJson.Array(targets.Select(t => (JsonNode?)Ref(doc, t.Page, t.Annotation)));
        var operations = new List<EditOperation>();
        foreach (var (page, a) in targets.OrderBy(t => model.IndexOfPage(t.Page.Id)).ThenByDescending(t => t.Page.IndexOf(t.Annotation.Id)))
            operations.Add(new RemoveAnnotationOperation(page.Id, page.IndexOf(a.Id), a));
        var removed = targets.Select(t => t.Annotation.Id).ToHashSet();
        var stillUsed = model.AllAnnotations.Where(a => !removed.Contains(a.Id)).OfType<ImageAnnotation>().Select(i => i.ResourceId).ToHashSet();
        foreach (var resource in targets.Select(t => t.Annotation).OfType<ImageAnnotation>().Select(i => i.ResourceId).Distinct())
            if (!stillUsed.Contains(resource) && model.Resources.TryGetValue(resource, out var r)) operations.Add(new RemoveResourceOperation(r));
        foreach (var id in removed)
            if (model.TagOf(id) is { } tag) operations.Add(new SetTagOperation(id, tag, null));
        if (operations.Count > 0) doc.Document.Session.Execute(new EditCommand("undo.delete", operations));
        result["count"] = targets.Count;
        result["deleted"] = deleted;
        return targets.Select(t => t.Page.Id).Distinct();
    }

    private static IEnumerable<Guid> OrderEdit(CliArgs args, CliContext context, JsonObject result)
    {
        var doc = context.Doc;
        var (page, a) = Target(args, doc, "--source-key");
        Protect(doc, a, args);
        string to = args.Required("--to");
        EditCommand command = to switch
        {
            "front" => DocumentEdits.ChangeZOrder(doc.Model, page.Id, [a.Id], ZOrderChange.ToFront),
            "back" => DocumentEdits.ChangeZOrder(doc.Model, page.Id, [a.Id], ZOrderChange.ToBack),
            "forward" => DocumentEdits.ChangeZOrder(doc.Model, page.Id, [a.Id], ZOrderChange.Forward),
            "backward" => DocumentEdits.ChangeZOrder(doc.Model, page.Id, [a.Id], ZOrderChange.Backward),
            _ when int.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out int z) && z < page.Annotations.Count =>
                z == page.IndexOf(a.Id) ? new EditCommand("undo.reorder", [])
                    : new EditCommand("undo.reorder", [new RemoveAnnotationOperation(page.Id, page.IndexOf(a.Id), a), new InsertAnnotationOperation(page.Id, z, a)]),
            _ => throw CliException.Invalid($"--to takes front, back, forward, backward or a stacking position 0-{page.Annotations.Count - 1} (0 = bottom)."),
        };
        if (!command.IsEmpty) doc.Document.Session.Execute(command);
        var now = doc.Model.GetPage(page.Id);
        result["annotation"] = AnnotListCommand.Describe(doc, now, a, now.IndexOf(a.Id));
        result["changed"] = !command.IsEmpty;
        return command.IsEmpty ? [] : [page.Id];
    }

    private static IEnumerable<Guid> LockEdit(CliArgs args, CliContext context, JsonObject result)
    {
        var doc = context.Doc;
        var (page, a) = Target(args, doc, "--source-key");
        Protect(doc, a, args);
        var command = DocumentEdits.SetLocked(doc.Model, [a.Id], !args.Flag("--off"));
        if (!command.IsEmpty) doc.Document.Session.Execute(command);
        var now = doc.Model.GetPage(page.Id);
        result["annotation"] = AnnotListCommand.Describe(doc, now, now.Annotations[now.IndexOf(a.Id)], now.IndexOf(a.Id));
        result["changed"] = !command.IsEmpty;
        return command.IsEmpty ? [] : [page.Id];
    }

    /// <summary>
    /// Like the app's paste (spec 5.7): on its own page the copy steps aside by 10 pt, on another page it keeps where it
    /// shows on the paper; <c>--at</c> puts its bounds' top-left there instead. The copy is unlocked (unless
    /// <c>--lock</c>), tagged for this run and keeps the original's group key unless <c>--group-key</c> is given. A source
    /// key an AI annotation already has is replaced, as with <c>add</c>.
    /// </summary>
    private static IEnumerable<Guid> CopyEdit(CliArgs args, CliContext context, JsonObject result)
    {
        var doc = context.Doc;
        var (from, source) = Target(args, doc, "--from-key");
        if (!AnnotationGeometry.CanTransform(source))
            throw new CliException("NOT_TRANSFORMABLE", $"A {AnnotListCommand.KindName(source)} annotation cannot be copied: {Why(source)}", ExitCodes.InvalidInput,
                new JsonObject { ["id"] = source.Id.ToString(), ["kind"] = AnnotListCommand.KindName(source) });
        var page = args.Value("--to") is { } to ? doc.ResolvePage(to) : from;
        Annotation copy = source with { Id = Guid.NewGuid(), Locked = false };
        Resource? resource = null;
        if (copy is ImageAnnotation image)
        {
            resource = doc.Model.Resources[image.ResourceId] with { Id = Guid.NewGuid() };
            copy = image with { ResourceId = resource.Id };
        }
        if (page.Id != from.Id) copy = AnnotationClipboard.Moved(copy, from.Setup.Frame.CanonicalToDisplay.Then(page.Setup.Frame.DisplayToCanonical));
        if (args.Value("--at") is { } at) copy = MovedTo(copy, CliArgs.ParseNumbers(at, 2, "--at"));
        else if (page.Id == from.Id) copy = AnnotationGeometry.Similarity(copy, default, move: new(AnnotationClipboard.StepPt, AnnotationClipboard.StepPt));
        if (args.Flag("--lock")) copy = copy with { Locked = true };
        var tag = AddCommand.Tag(context, args.Value("--source-key"), args.Value("--group-key") ?? doc.Model.TagOf(source.Id)?.GroupKey);
        var placed = AddCommand.Place(context, page, copy, resource, tag);
        AddCommand.Report(doc, result, placed);
        result["source"] = Ref(doc, from, source);
        return placed.Previous is null ? [page.Id] : [placed.Previous.Id, page.Id];
    }

    /// <summary>The annotation of <c>--id</c> or of the key option (the AI annotation with that source key).</summary>
    private static (Page Page, Annotation Annotation) Target(CliArgs args, CliDocument doc, string keyOption)
    {
        if (args.Has("--id") == args.Has(keyOption)) throw CliException.Invalid($"Give the annotation with --id or {keyOption}.");
        if (args.Value("--id") is { } id) return Find(doc, id);
        string key = args.Value(keyOption)!;
        return AddCommand.Owner(doc, key) ?? throw new CliException("ANNOTATION_NOT_FOUND", $"No AI annotation has source key '{key}'.", ExitCodes.InvalidInput,
            new JsonObject { ["sourceKey"] = key });
    }

    private static (Page Page, Annotation Annotation) Find(CliDocument doc, string id) =>
        Guid.TryParse(id, out var guid) && doc.Model.FindAnnotation(guid) is { } found ? (found.Page, found.Annotation)
            : throw new CliException("ANNOTATION_NOT_FOUND", $"No annotation has ID '{id}'.", ExitCodes.InvalidInput, new JsonObject { ["id"] = id });

    /// <summary>cli_plan 4.7: only the CLI's own annotations change unless <c>--allow-untagged</c>.</summary>
    private static void Protect(CliDocument doc, Annotation a, CliArgs args)
    {
        var tag = doc.Model.TagOf(a.Id);
        if (tag is { Author: AnnotationTag.AiAuthor } || args.Flag("--allow-untagged")) return;
        throw new CliException("FORBIDDEN", "The annotation is not one the CLI made (no ai tag); the user's writing and other annotations are read-only. --allow-untagged lifts this.",
            ExitCodes.Unsatisfiable, new JsonObject { ["id"] = a.Id.ToString(), ["kind"] = AnnotListCommand.KindName(a), ["tags"] = AnnotListCommand.Tags(tag) });
    }

    private static string Why(Annotation a) => a is StickyNote
        ? "a note's icon only moves (--move, --at)."
        : "text marks are bound to the PDF glyphs of their page; make a new one with add mark.";

    private static JsonObject Ref(CliDocument doc, Page page, Annotation a)
    {
        var tag = doc.Model.TagOf(a.Id);
        return new()
        {
            ["id"] = a.Id.ToString(), ["page"] = doc.NumberOf(page), ["pageId"] = page.Id.ToString(), ["kind"] = AnnotListCommand.KindName(a),
            ["sourceKey"] = tag?.SourceKey, ["groupKey"] = tag?.GroupKey,
        };
    }

    private static bool? OnOff(CliArgs args, string option) => args.Value(option) switch
    {
        null => null,
        "on" or "true" => true,
        "off" or "false" => false,
        _ => throw CliException.Invalid($"{option} takes on or off."),
    };

    /// <summary><c>w,h</c>, both positive.</summary>
    private static (double W, double H) Size(string text)
    {
        var n = CliArgs.ParseNumbers(text, 2, "--size");
        if (n[0] <= 0 || n[1] <= 0 || n[0] > ModelLimits.MaxLengthPt || n[1] > ModelLimits.MaxLengthPt) throw CliException.Invalid("--size needs a positive width and height.");
        return (n[0], n[1]);
    }
}
