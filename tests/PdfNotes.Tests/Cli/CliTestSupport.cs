using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.Cli;
using PdfNotes.Cli.Commands;
using PdfNotes.Core.Geometry;

namespace PdfNotes.Tests.Cli;

/// <summary>Runs CLI commands in-process and checks their JSON against the embedded schemas.</summary>
internal static class CliRunner
{
    public static (int Exit, JsonObject Json) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = CliApp.Run(args, stdout, stderr);
        var text = stdout.ToString();
        Assert.Equal(1, text.Count(c => c == '\n')); // exactly one JSON document on stdout
        return (exit, JsonNode.Parse(text)!.AsObject());
    }

    /// <summary>Runs a command that must succeed and match its output schema.</summary>
    public static JsonObject Ok(params string[] args)
    {
        var (exit, json) = Run(args);
        Assert.True(exit == 0, json.ToJsonString());
        Assert.True(json["ok"]!.GetValue<bool>());
        string command = json["command"]!.GetValue<string>();
        var errors = CliSchema.Validate(json, SchemaCommand.Load(SchemaCommand.FileName(command))["output"]!);
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        return json;
    }

    /// <summary>Runs a command that must fail with <paramref name="code"/> and <paramref name="exitCode"/>.</summary>
    public static JsonObject Fails(string code, int exitCode, params string[] args)
    {
        var (exit, json) = Run(args);
        Assert.Empty(CliSchema.Validate(json, SchemaCommand.Load("common")["$defs"]!["error"]!));
        Assert.Equal(code, json["error"]!["code"]!.GetValue<string>());
        Assert.Equal(exitCode, exit);
        return json;
    }

    public static Affine Matrix(JsonNode? node) => Affine.FromArray(node!.AsArray().Select(n => n!.GetValue<double>()).ToList());

    public static (int Width, int Height, byte[] Bgra) LoadPng(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return (bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    /// <summary>Centroid of the red registration-cross pixels within <paramref name="radius"/> of <paramref name="near"/>; null when none.</summary>
    public static Point2? RedCentroid((int Width, int Height, byte[] Bgra) image, Point2 near, int radius = 18)
    {
        double sx = 0, sy = 0; int n = 0;
        for (int y = Math.Max(0, (int)near.Y - radius); y <= Math.Min(image.Height - 1, (int)near.Y + radius); y++)
            for (int x = Math.Max(0, (int)near.X - radius); x <= Math.Min(image.Width - 1, (int)near.X + radius); x++)
            {
                int i = (y * image.Width + x) * 4;
                byte b = image.Bgra[i], g = image.Bgra[i + 1], r = image.Bgra[i + 2];
                if (r > 150 && g < 110 && b < 110) { sx += x + 0.5; sy += y + 0.5; n++; }
            }
        return n < 6 ? null : new Point2(sx / n, sy / n);
    }
}

/// <summary>
/// A small JSON Schema checker for the subset the CLI schemas use: $ref (local and common.json), type, const, enum,
/// required, properties, additionalProperties: false, items, minItems, maxItems, anyOf.
/// </summary>
internal static class CliSchema
{
    public static List<string> Validate(JsonNode? instance, JsonNode schema)
    {
        var errors = new List<string>();
        Check(instance, schema, schema, "$", errors);
        return errors;
    }

    private static void Check(JsonNode? value, JsonNode schema, JsonNode root, string path, List<string> errors)
    {
        if (schema["$ref"] is { } reference)
        {
            string target = reference.GetValue<string>();
            int hash = target.IndexOf('#');
            var document = hash > 0 ? SchemaCommand.Load(Path.GetFileNameWithoutExtension(target[..hash])) : root;
            var resolved = document;
            foreach (var part in target[(hash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries)) resolved = resolved![part]!;
            Check(value, resolved!, document, path, errors);
        }
        if (schema["anyOf"] is JsonArray any)
        {
            if (!any.Any(s => { var e = new List<string>(); Check(value, s!, root, path, e); return e.Count == 0; }))
                errors.Add($"{path}: matches none of anyOf");
        }
        if (schema["type"] is { } type)
        {
            var types = type is JsonArray list ? list.Select(t => t!.GetValue<string>()).ToArray() : [type.GetValue<string>()];
            if (!types.Any(t => Is(value, t))) { errors.Add($"{path}: expected {string.Join("|", types)}"); return; }
        }
        if (schema["const"] is { } constant && !JsonNode.DeepEquals(constant, value)) errors.Add($"{path}: expected {constant.ToJsonString()}");
        if (schema["enum"] is JsonArray choices && !choices.Any(c => JsonNode.DeepEquals(c, value))) errors.Add($"{path}: {value?.ToJsonString()} not in enum");
        if (value is JsonObject obj)
        {
            foreach (var name in (schema["required"] as JsonArray ?? []).Select(n => n!.GetValue<string>()))
                if (!obj.ContainsKey(name)) errors.Add($"{path}: missing {name}");
            var properties = schema["properties"] as JsonObject;
            foreach (var (name, child) in obj)
            {
                if (properties?[name] is { } sub) Check(child, sub, root, $"{path}.{name}", errors);
                else if (schema["additionalProperties"] is JsonValue extra && !extra.GetValue<bool>()) errors.Add($"{path}: unexpected {name}");
            }
        }
        if (value is JsonArray array)
        {
            if (schema["minItems"] is { } min && array.Count < min.GetValue<int>()) errors.Add($"{path}: fewer than {min} items");
            if (schema["maxItems"] is { } max && array.Count > max.GetValue<int>()) errors.Add($"{path}: more than {max} items");
            if (schema["items"] is { } items)
                for (int i = 0; i < array.Count; i++) Check(array[i], items, root, $"{path}[{i}]", errors);
        }
    }

    private static bool Is(JsonNode? value, string type) => type switch
    {
        "null" => value is null,
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => value is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => value is JsonValue n && n.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue i && i.GetValueKind() == JsonValueKind.Number && Math.Abs(i.GetValue<double>() % 1) < 1e-12,
        _ => throw new InvalidOperationException(type),
    };
}

/// <summary>
/// The semantic comparison of the C5 plan (cli_plan 8): two PDFs as <c>info</c> and <c>annot list</c> read them — page
/// make-up, annotation kinds, contents, geometry (±0.01 pt), style, stacking order and tags — ignoring annotation and page
/// IDs, batch IDs, hashes and paths, which regeneration does not keep.
/// </summary>
internal static class CliCompare
{
    public static List<string> Differences(string a, string b)
    {
        var differences = new List<string>();
        Diff(Snapshot(a), Snapshot(b), "$", differences);
        return differences;
    }

    private static JsonObject Snapshot(string pdf)
    {
        var info = CliRunner.Ok("info", pdf);
        var pages = info["pages"]!.DeepClone().AsArray();
        foreach (var page in pages) page!.AsObject().Remove("pageId");
        var annotations = CliRunner.Ok("annot", "list", pdf)["annotations"]!.DeepClone().AsArray();
        foreach (var item in annotations)
        {
            var o = item!.AsObject();
            o.Remove("id");
            o.Remove("pageId");
            if (o["tags"] is JsonObject tags) tags.Remove("batchId");
        }
        return new JsonObject { ["pageCount"] = info["pageCount"]!.DeepClone(), ["pages"] = pages, ["annotations"] = annotations };
    }

    private static void Diff(JsonNode? x, JsonNode? y, string path, List<string> differences)
    {
        switch (x, y)
        {
            case (JsonObject a, JsonObject b):
                foreach (var key in a.Select(p => p.Key).Union(b.Select(p => p.Key)))
                    Diff(a[key], b[key], $"{path}.{key}", differences);
                break;
            case (JsonArray a, JsonArray b):
                if (a.Count != b.Count) differences.Add($"{path}: {a.Count} items vs {b.Count}");
                else for (int i = 0; i < a.Count; i++) Diff(a[i], b[i], $"{path}[{i}]", differences);
                break;
            case (JsonValue a, JsonValue b) when a.GetValueKind() == JsonValueKind.Number && b.GetValueKind() == JsonValueKind.Number:
                if (Math.Abs(a.GetValue<double>() - b.GetValue<double>()) > 0.01) differences.Add($"{path}: {a} vs {b}");
                break;
            default:
                if (!JsonNode.DeepEquals(x, y)) differences.Add($"{path}: {x?.ToJsonString() ?? "null"} vs {y?.ToJsonString() ?? "null"}");
                break;
        }
    }
}
