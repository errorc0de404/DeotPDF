using System.Text.Json.Nodes;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>schema [command]</c> (cli_plan 3.1): without a command, the command list; with one, its JSON Schemas
/// (<c>input</c> options, <c>output</c> result) plus the shared definitions they refer to (<c>common</c>).
/// </summary>
public static class SchemaCommand
{
    public static JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, [], []);
        var result = CliJson.Result("schema");
        result["version"] = 1;
        if (args.Positionals.Count == 0)
        {
            result["commands"] = CliJson.Array(CliApp.Commands.Select(c => (JsonNode?)new JsonObject { ["name"] = c.Name, ["summary"] = c.Summary }));
            result["exitCodes"] = new JsonObject { ["0"] = "ok", ["1"] = "invalid input", ["2"] = "request cannot be satisfied", ["3"] = "file locked or changed", ["4"] = "internal error" };
            result["errorCodes"] = CliJson.Array(ErrorCodes.Select(e => (JsonNode?)new JsonObject { ["code"] = e.Code, ["exit"] = e.Exit, ["meaning"] = e.Meaning }));
            return result;
        }
        string name = string.Join(' ', args.Positionals);
        if (CliApp.Commands.All(c => c.Name != name))
            throw new CliException("UNKNOWN_COMMAND", $"Unknown command '{name}'.", ExitCodes.InvalidInput, new JsonObject { ["command"] = name });
        result["name"] = name;
        result["schema"] = Load(FileName(name));
        result["common"] = Load("common");
        return result;
    }

    /// <summary>Every error code a command can give (cli_plan 3.1), its exit code and when.</summary>
    public static readonly (string Code, int Exit, string Meaning)[] ErrorCodes =
    [
        ("INVALID_ARGUMENT", 1, "an option, field or value is missing, unknown or out of range"),
        ("UNKNOWN_COMMAND", 1, "no such command"),
        ("FILE_NOT_FOUND", 1, "an input, image, text, style, rules or operations file does not exist"),
        ("PDF_OPEN_FAILED", 1, "the PDF cannot be opened (damaged, encrypted, not a PDF)"),
        ("PAGE_NOT_FOUND", 1, "no such page number, page ID or $name (in apply also: deleted by an earlier operation)"),
        ("ANNOTATION_NOT_FOUND", 1, "no annotation with that ID or source key"),
        ("RANGE_OUT_OF_TEXT", 1, "--chars or --words past the page's text"),
        ("NOT_TRANSFORMABLE", 1, "a text mark (or a note's icon) cannot be moved, turned, resized or copied that way"),
        ("INVALID_IMAGE", 1, "not a PNG, JPEG or BMP image"),
        ("IMAGE_TOO_LARGE", 1, "the rendered image would be too large; lower --dpi or use --region"),
        ("SAME_AS_INPUT", 1, "the output (or a preview) is the input file"),
        ("OUTPUT_EXISTS", 1, "the output exists and --overwrite is not given"),
        ("NOT_FOUND", 2, "--find found no exact candidate"),
        ("AMBIGUOUS_MATCH", 2, "--find found two or more candidates (details.matches); narrow it or use --chars"),
        ("EMPTY_RANGE", 2, "the characters have no visible glyphs"),
        ("NO_SPACE", 2, "space found no empty rectangle"),
        ("REGION_OUTSIDE_PAGE", 2, "--region does not overlap the page"),
        ("PAGE_OVERFLOW", 2, "a text box would reach past the page (--overflow allow makes it with a warning)"),
        ("TEXT_OVERFLOW", 2, "with --no-auto-height the text is taller than the box"),
        ("LAST_PAGE", 2, "a document keeps at least one page"),
        ("SOURCE_KEY_KIND_MISMATCH", 2, "the source key belongs to an annotation of another kind"),
        ("DUPLICATE_SOURCE_KEY", 2, "a source key is on two annotations, or made twice in one apply"),
        ("FORBIDDEN", 2, "the annotation is not one the CLI made (--allow-untagged lifts this)"),
        ("LOCKED_ANNOTATION", 2, "a locked annotation would change (annot lock --off, or delete --include-locked)"),
        ("CHECK_FAILED", 2, "an apply guard found error-level problems (details.problems); nothing was written"),
        ("HASH_MISMATCH", 3, "the input changed since --expect-hash was taken"),
        ("FILE_OPEN_IN_APP", 3, "a DeotPDF window has the input or output open"),
        ("FILE_LOCKED", 3, "the input cannot be read (locked by another program)"),
        ("SAVE_FAILED", 4, "the output could not be written (3 when it is locked)"),
        ("INTERNAL", 4, "an unexpected error (details.type; the trace is on standard error)"),
    ];

    public static string FileName(string command) => command.Replace(' ', '-');

    /// <summary>The embedded schema <c>Schemas/&lt;name&gt;.json</c>.</summary>
    public static JsonNode Load(string name)
    {
        using var stream = typeof(SchemaCommand).Assembly.GetManifestResourceStream($"PdfNotes.Cli.Schemas.{name}.json")
            ?? throw new InvalidOperationException($"Schema {name} is not embedded.");
        return JsonNode.Parse(stream)!;
    }
}
