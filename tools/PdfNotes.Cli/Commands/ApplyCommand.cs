using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PdfNotes.Core.Models;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>apply &lt;pdf&gt; &lt;ops.json | -&gt; -o &lt;pdf&gt; [--dry-run] [--preview-dir &lt;folder&gt;]</c> (cli_plan 5): the writing
/// operations of a list run in turn on one document in memory, and the result is written once. One failing operation
/// fails the whole run and nothing is written (atomic). Each operation is <c>{ "op": "add.text", …fields }</c>, the
/// fields being that command's options in camelCase (<c>sourceKey</c> for <c>--source-key</c>; lists of numbers for
/// <c>x,y,w,h</c>, <c>{start, end}</c> for <c>s:e</c>, JSON for JSON options, true for a flag). Page numbers mean the
/// input's pages (before any operation); pages an operation makes are named with <c>"as"</c> and referenced as
/// <c>$name</c>. Coordinates are those of the page when the operation runs (5.1). <c>defaults</c> gives fields per op
/// name (a <c>style</c> object merges field by field). <c>guards</c> are <c>check</c> rules on the result (5.2).
/// </summary>
public static class ApplyCommand
{
    private static readonly Regex PageName = new("^[A-Za-z0-9_]{1,64}$");

    public static JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, [.. CliWrite.ValueOptions.Where(o => o != "--preview"), "--preview-dir"], CliWrite.FlagOptions);
        string input = args.InputPath(extraPositionals: 1);
        if (args.Positionals.Count < 2) throw CliException.Invalid("Give the operations file: deot apply <pdf> <ops.json> -o <pdf> (- reads standard input).");
        var plan = Load(args.Positionals[1]);
        foreach (var (key, _) in plan)
            if (key is not ("expectHash" or "batchId" or "defaults" or "ops" or "guards"))
                throw CliException.Invalid($"The operations file has no field \"{key}\" (expectHash, batchId, defaults, ops, guards).", new JsonObject { ["field"] = key });
        var ops = plan["ops"] as JsonArray ?? throw CliException.Invalid("The operations file needs \"ops\": a list of operations.");
        var defaults = Defaults(plan["defaults"]);
        var guards = plan["guards"] is { } g ? CheckCommand.ParseRules(g) : null;
        var write = CliWrite.Parse(args, input, args.Value("--preview-dir"), String(plan, "expectHash"), String(plan, "batchId"));

        using var doc = CliDocument.Open(input);
        write.CheckHash(doc);
        var initial = doc.Model;
        doc.NumberedPages = [.. initial.Pages.Select(p => p.Id)];
        var context = new CliContext(doc, write.BatchId!) { KeysThisRun = new(StringComparer.Ordinal) };
        var results = new JsonArray();
        var changed = new List<Guid>();
        var replaced = new HashSet<Guid>();
        var reordered = new HashSet<Guid>();
        var transformed = new List<Guid>();
        for (int i = 0; i < ops.Count; i++)
        {
            string name = "?";
            try
            {
                if (ops[i] is not JsonObject op) throw CliException.Invalid("An operation is a JSON object with \"op\".");
                name = op["op"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : throw CliException.Invalid("An operation needs \"op\", e.g. \"add.text\".");
                if (!CliApp.Ops.TryGetValue(name, out var definition))
                    throw CliException.Invalid($"Unknown op \"{name}\"; ops are {string.Join(", ", CliApp.Ops.Keys)}.", new JsonObject { ["op"] = name });
                var names = Names(op["as"]);
                var fields = defaults.TryGetValue(name, out var preset) ? Merge(preset, op) : op;
                var opArgs = CliArgs.Parse(Tokens(name, fields, definition), definition.Values, definition.Flags);
                var result = new JsonObject { ["index"] = i, ["op"] = name };
                changed.AddRange(definition.Execute(opArgs, context, result));
                if (names.Count > 0) NamePages(doc, name, names, result);
                if (result["replaced"] is JsonValue r && r.GetValue<bool>()) replaced.Add(Guid.Parse(result["annotation"]!["id"]!.GetValue<string>()));
                if (name == "annot.order" && result["changed"]!.GetValue<bool>()) reordered.Add(Guid.Parse(result["annotation"]!["id"]!.GetValue<string>()));
                if (result["pageTransforms"] is JsonArray pts) transformed.AddRange(pts.Select(t => Guid.Parse(t!["pageId"]!.GetValue<string>())));
                results.Add(result);
            }
            catch (CliException e)
            {
                var details = new JsonObject { ["opIndex"] = i, ["op"] = name };
                foreach (var (key, value) in e.Details ?? []) if (!details.ContainsKey(key)) details[key] = value?.DeepClone();
                throw new CliException(e.Code, $"Operation {i} ({name}): {e.Message} Nothing was written.", e.ExitCode, details);
            }
        }

        var output = CliJson.Result("apply", doc.SourceHash);
        output["batchId"] = write.BatchId;
        output["ops"] = results;
        output["summary"] = Summary(doc, initial, replaced, reordered, transformed);
        if (guards is not null)
        {
            var problems = CheckCommand.Evaluate(doc, doc.Model.Pages, guards);
            var errors = problems.Where(p => p["level"]!.GetValue<string>() == "error").ToList();
            if (errors.Count > 0)
                throw new CliException("CHECK_FAILED", $"{errors.Count} guard problem(s); nothing was written.", ExitCodes.Unsatisfiable,
                    new JsonObject { ["count"] = errors.Count, ["problems"] = CliJson.Array(problems) });
            output["guards"] = new JsonObject { ["count"] = problems.Count, ["problems"] = CliJson.Array(problems) };
        }
        else output["guards"] = null;
        write.Finish(doc, output, changed);
        return output;
    }

    private static JsonObject Load(string path)
    {
        string text;
        if (path == "-") text = Console.In.ReadToEnd();
        else
        {
            string full = CliPaths.Full(path);
            if (!File.Exists(full)) throw new CliException("FILE_NOT_FOUND", $"The operations file {full} does not exist.", ExitCodes.InvalidInput, new JsonObject { ["path"] = full });
            text = File.ReadAllText(full, Encoding.UTF8);
        }
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException e) { throw CliException.Invalid($"The operations file is not valid JSON: {e.Message}"); }
        return root switch
        {
            JsonObject o => o,
            JsonArray a => new JsonObject { ["ops"] = a.DeepClone() },
            _ => throw CliException.Invalid("The operations file is a JSON object with \"ops\" (or just the list of operations)."),
        };
    }

    private static string? String(JsonObject o, string key) => o[key] switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => throw CliException.Invalid($"\"{key}\" of the operations file is a string."),
    };

    /// <summary><c>defaults</c>: op name → fields that operation takes when it does not give them.</summary>
    private static Dictionary<string, JsonObject> Defaults(JsonNode? node)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (node is null) return result;
        if (node is not JsonObject o) throw CliException.Invalid("\"defaults\" is an object: op name → fields, e.g. { \"add.text\": { \"style\": { \"size\": 11 } } }.");
        foreach (var (name, fields) in o)
        {
            if (!CliApp.Ops.ContainsKey(name)) throw CliException.Invalid($"\"defaults\" names an unknown op \"{name}\".");
            if (fields is not JsonObject f || f.ContainsKey("op") || f.ContainsKey("as")) throw CliException.Invalid($"\"defaults\".\"{name}\" is an object of fields.");
            result[name] = f;
        }
        return result;
    }

    /// <summary>The operation's fields over the defaults; a <c>style</c> object in both merges field by field.</summary>
    private static JsonObject Merge(JsonObject preset, JsonObject op)
    {
        var merged = (JsonObject)op.DeepClone();
        foreach (var (key, value) in preset)
        {
            if (!merged.ContainsKey(key)) merged[key] = value?.DeepClone();
            else if (key == "style" && value is JsonObject baseStyle && merged[key] is JsonObject style)
                foreach (var (field, v) in baseStyle)
                    if (!style.ContainsKey(field)) style[field] = v?.DeepClone();
        }
        return merged;
    }

    /// <summary>The command-line tokens of an operation's fields (see the class summary for the mapping).</summary>
    private static List<string> Tokens(string name, JsonObject fields, CliOp definition)
    {
        var tokens = new List<string>();
        foreach (var (key, value) in fields)
        {
            if (key is "op" or "as") continue;
            string option = Option(key);
            if (definition.Flags.Contains(option))
            {
                if (value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                {
                    if (v.GetValue<bool>()) tokens.Add(option);
                    continue;
                }
                throw CliException.Invalid($"\"{key}\" of {name} is true or false.", new JsonObject { ["field"] = key });
            }
            if (!definition.Values.Contains(option))
                throw CliException.Invalid($"{name} has no field \"{key}\".", new JsonObject { ["field"] = key });
            if (value is null) continue;
            tokens.Add(option);
            tokens.Add(Value(value));
        }
        return tokens;
    }

    /// <summary>The option of a camelCase field: <c>sourceKey</c> → <c>--source-key</c>.</summary>
    internal static string Option(string field) => "--" + Regex.Replace(field, "(?<=[a-z0-9])([A-Z])", "-$1").ToLowerInvariant();

    /// <summary>A field's value as one command-line token (see the class summary).</summary>
    internal static string Value(JsonNode node) => node switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.GetValue<double>().ToString("R", CultureInfo.InvariantCulture),
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValue<bool>() ? "true" : "false",
        JsonObject o when o.Count == 2 && o["start"] is JsonValue s && o["end"] is JsonValue e => $"{Value(s)}:{Value(e)}",
        JsonArray a when a.All(n => n is JsonValue) => string.Join(",", a.Select(n => Value(n!))),
        _ => node.ToJsonString(),
    };

    private static List<string> Names(JsonNode? node)
    {
        var names = node switch
        {
            null => [],
            JsonValue v when v.GetValueKind() == JsonValueKind.String => [v.GetValue<string>()],
            JsonArray a when a.All(n => n is JsonValue x && x.GetValueKind() == JsonValueKind.String) => a.Select(n => n!.GetValue<string>()).ToList(),
            _ => throw CliException.Invalid("\"as\" is a page name or a list of names."),
        };
        foreach (string n in names)
            if (!PageName.IsMatch(n)) throw CliException.Invalid($"Page name \"{n}\" is not usable: letters, digits and _ (referenced as $name).");
        return names;
    }

    /// <summary>Names the pages the operation made (its <c>created</c> result), in order.</summary>
    private static void NamePages(CliDocument doc, string op, List<string> names, JsonObject result)
    {
        if (result["created"] is not JsonArray created) throw CliException.Invalid($"\"as\" names pages an operation makes; {op} makes none.");
        if (names.Count > created.Count) throw CliException.Invalid($"\"as\" has {names.Count} names but {op} made {created.Count} page(s).");
        for (int i = 0; i < names.Count; i++)
        {
            if (!doc.PageNames.TryAdd(names[i], Guid.Parse(created[i]!["pageId"]!.GetValue<string>())))
                throw CliException.Invalid($"Page name \"{names[i]}\" is used twice.");
        }
    }

    /// <summary>
    /// What the run changed against the input: annotations made, replaced (same source key), updated (changed in place,
    /// moved, retagged or reordered) and deleted, and pages made, deleted and transformed.
    /// </summary>
    private static JsonObject Summary(CliDocument doc, DocumentModel initial, HashSet<Guid> replaced, HashSet<Guid> reordered, List<Guid> transformed)
    {
        var final = doc.Model;
        var before = initial.Pages.SelectMany(p => p.Annotations.Select(a => (Page: p, Annotation: a))).ToDictionary(x => x.Annotation.Id);
        var after = final.Pages.SelectMany(p => p.Annotations.Select(a => (Page: p, Annotation: a))).ToList();
        JsonNode Entry(DocumentModel model, Page page, Annotation a) => new JsonObject
        {
            ["id"] = a.Id.ToString(), ["kind"] = AnnotListCommand.KindName(a), ["sourceKey"] = model.TagOf(a.Id)?.SourceKey,
            ["page"] = model.IndexOfPage(page.Id) + 1, ["pageId"] = page.Id.ToString(),
        };
        var created = after.Where(x => !before.ContainsKey(x.Annotation.Id)).Select(x => Entry(final, x.Page, x.Annotation));
        var replacedList = after.Where(x => before.ContainsKey(x.Annotation.Id) && replaced.Contains(x.Annotation.Id)).Select(x => Entry(final, x.Page, x.Annotation));
        var updated = after.Where(x => before.TryGetValue(x.Annotation.Id, out var old) && !replaced.Contains(x.Annotation.Id) &&
            (!ReferenceEquals(old.Annotation, x.Annotation) || old.Page.Id != x.Page.Id || !Equals(initial.TagOf(old.Annotation.Id), final.TagOf(old.Annotation.Id)) ||
             reordered.Contains(x.Annotation.Id))).Select(x => Entry(final, x.Page, x.Annotation));
        var stillThere = after.Select(x => x.Annotation.Id).ToHashSet();
        var deleted = before.Values.Where(x => !stillThere.Contains(x.Annotation.Id)).Select(x => Entry(initial, x.Page, x.Annotation));
        var initialPages = initial.Pages.Select(p => p.Id).ToHashSet();
        var finalPages = final.Pages.Select(p => p.Id).ToHashSet();
        JsonNode PageRef(DocumentModel model, Guid id) => new JsonObject { ["page"] = model.IndexOfPage(id) + 1, ["pageId"] = id.ToString() };
        return new JsonObject
        {
            ["annotations"] = new JsonObject
            {
                ["created"] = CliJson.Array(created), ["replaced"] = CliJson.Array(replacedList),
                ["updated"] = CliJson.Array(updated), ["deleted"] = CliJson.Array(deleted),
            },
            ["pages"] = new JsonObject
            {
                ["created"] = CliJson.Array(final.Pages.Where(p => !initialPages.Contains(p.Id)).Select(p => PageRef(final, p.Id))),
                ["deleted"] = CliJson.Array(initial.Pages.Where(p => !finalPages.Contains(p.Id)).Select(p => PageRef(initial, p.Id))),
                ["transformed"] = CliJson.Array(transformed.Distinct().Where(finalPages.Contains).Select(id => PageRef(final, id))),
            },
        };
    }
}
