using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using PdfNotes.Cli.Commands;

namespace PdfNotes.Cli.Mcp;

/// <summary>
/// The tools of <c>deot mcp</c>: every reading command as a tool of its own (<c>annot list</c> → <c>annot_list</c>), and
/// for writing <c>apply</c> plus <c>page_export</c>, the one writing command <c>apply</c> cannot run. A tool's fields are
/// the command's options in camelCase as its <c>schema</c> input gives them, so a call becomes the same command line.
/// </summary>
public static class McpTools
{
    /// <summary>One tool: the command it runs and its input fields (name → JSON Schema).</summary>
    public sealed record Entry(string Name, string Command, Tool Tool, JsonObject Fields);

    private static readonly string[] Reading = ["schema", "info", "render", "text", "annot list", "find", "layout", "ink", "space", "measure", "fonts", "check"];

    /// <summary>Fields of the command line that a server does not offer: the settings folder is always the app's.</summary>
    private static readonly string[] Hidden = ["appFolder"];

    public static readonly IReadOnlyList<Entry> All = Build();

    public static Entry? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    private static List<Entry> Build()
    {
        var list = new List<Entry>();
        foreach (string command in Reading) list.Add(FromSchema(command, readOnly: command != "render"));
        list.Add(FromSchema("page export", readOnly: false));
        list.Add(Apply());
        return list;
    }

    private static Entry FromSchema(string command, bool readOnly)
    {
        var input = (JsonObject)SchemaCommand.Load(SchemaCommand.FileName(command))["input"]!.DeepClone();
        string usage = input["description"]?.GetValue<string>() ?? "";
        input.Remove("description");
        var fields = (JsonObject)input["properties"]!;
        foreach (string hidden in Hidden) fields.Remove(hidden);
        if (command == "render")
        {
            // The PNG comes back as an image; a path is only needed to keep the file.
            input["required"] = new JsonArray("pdf");
            fields["out"] = new JsonObject { ["type"] = "string", ["description"] = "PNG to keep (optional; the image is returned either way)" };
        }
        string summary = CliApp.Commands.First(c => c.Name == command).Summary;
        return Make(command, $"{summary}. {usage}", input, fields, readOnly);
    }

    private static Entry Apply()
    {
        var input = (JsonObject)SchemaCommand.Load("apply")["input"]!.DeepClone();
        string usage = input["description"]!.GetValue<string>();
        int rules = usage.IndexOf("The operations run", StringComparison.Ordinal);
        if (rules < 0) throw new InvalidOperationException("The apply schema's description changed.");
        var given = (JsonObject)input["properties"]!;
        var fields = new JsonObject
        {
            ["pdf"] = new JsonObject { ["type"] = "string" },
            ["plan"] = new JsonObject
            {
                ["type"] = new JsonArray("object", "array"),
                ["description"] = "{ batchId?, expectHash?, defaults?, ops: [{ \"op\": \"add.text\", …fields }], guards? } or just the list of ops. " +
                    "An op's fields are its command's options in camelCase: the schema tool with that command (e.g. \"add text\") lists them.",
            },
            ["previews"] = new JsonObject { ["type"] = "boolean", ["description"] = "return PNGs of the changed pages (kept in previewDir when given)" },
        };
        foreach (var (key, value) in given)
            if (key is not ("pdf" or "opsFile" or "appFolder")) fields[key] = value?.DeepClone();
        var schema = new JsonObject { ["type"] = "object", ["required"] = new JsonArray("pdf", "plan"), ["properties"] = fields };
        string summary = CliApp.Commands.First(c => c.Name == "apply").Summary;
        return Make("apply", $"{summary}. Give out, or dryRun to only see the result. {usage[rules..]}", schema, fields, readOnly: false);
    }

    private static Entry Make(string command, string description, JsonObject schema, JsonObject fields, bool readOnly)
    {
        InlineCommonDefinitions(schema);
        string name = command.Replace(' ', '_');
        var tool = new Tool
        {
            Name = name,
            Description = description,
            InputSchema = JsonSerializer.SerializeToElement(schema),
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = readOnly,
                DestructiveHint = command is "apply" or "page export", // with overwrite they replace an existing output
                IdempotentHint = true,
                OpenWorldHint = false,
            },
        };
        return new Entry(name, command, tool, fields);
    }

    /// <summary>A tool's schema stands alone: <c>common.json#/$defs/x</c> becomes <c>#/$defs/x</c> with the definitions it needs.</summary>
    private static void InlineCommonDefinitions(JsonObject schema)
    {
        var common = (JsonObject)SchemaCommand.Load("common")["$defs"]!;
        var defs = new SortedDictionary<string, JsonNode>(StringComparer.Ordinal);
        var queue = new Queue<JsonNode>([schema]);
        while (queue.TryDequeue(out var node))
        {
            if (node is JsonArray array)
                foreach (var item in array) { if (item is not null) queue.Enqueue(item); }
            if (node is not JsonObject o) continue;
            if (o["$ref"] is JsonValue r && DefinitionName(r.GetValue<string>()) is { } name)
            {
                o["$ref"] = "#/$defs/" + name;
                if (!defs.ContainsKey(name))
                {
                    var copy = common[name]?.DeepClone() ?? throw new InvalidOperationException($"common.json has no {name}.");
                    defs[name] = copy;
                    queue.Enqueue(copy);
                }
            }
            foreach (var (_, value) in o) if (value is not null) queue.Enqueue(value);
        }
        if (defs.Count > 0) schema["$defs"] = new JsonObject(defs.Select(d => KeyValuePair.Create(d.Key, (JsonNode?)d.Value)));
    }

    private static string? DefinitionName(string target) =>
        target.StartsWith("common.json#/$defs/", StringComparison.Ordinal) ? target["common.json#/$defs/".Length..]
        : target.StartsWith("#/$defs/", StringComparison.Ordinal) ? target["#/$defs/".Length..] : null;

    /// <summary>
    /// The command line of a call: positional values first (<c>pdf</c>; <c>command</c> of <c>schema</c>), then
    /// <c>--option value</c> per field and <c>--flag</c> per true boolean, as <c>apply</c> maps op fields.
    /// </summary>
    public static List<string> Tokens(Entry tool, IReadOnlyDictionary<string, JsonElement>? arguments, IReadOnlySet<string>? skip = null)
    {
        var tokens = new List<string>(tool.Command.Split(' '));
        var options = new List<string>();
        foreach (var (key, element) in arguments ?? new Dictionary<string, JsonElement>())
        {
            if (skip?.Contains(key) == true) continue;
            if (tool.Fields[key] is not JsonObject field)
                throw CliException.Invalid($"{tool.Name} has no field \"{key}\".", new JsonObject { ["field"] = key });
            var value = JsonSerializer.SerializeToNode(element);
            if (value is null) continue;
            if (key == "pdf") { tokens.Add(ApplyCommand.Value(value)); continue; }
            if (key == "command" && tool.Command == "schema")
            {
                tokens.AddRange(ApplyCommand.Value(value).Replace('_', ' ').Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
                continue;
            }
            if (field["type"] is JsonValue t && t.GetValue<string>() == "boolean")
            {
                if (value is not JsonValue b || b.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                    throw CliException.Invalid($"\"{key}\" of {tool.Name} is true or false.", new JsonObject { ["field"] = key });
                if (b.GetValue<bool>()) options.Add(ApplyCommand.Option(key));
                continue;
            }
            options.Add(ApplyCommand.Option(key));
            options.Add(ApplyCommand.Value(value));
        }
        tokens.AddRange(options);
        return tokens;
    }
}
