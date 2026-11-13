using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using PdfNotes.Editor.Services;

namespace PdfNotes.Cli;

/// <summary>
/// <c>deot</c>'s end of the live link (MCP_LIVE_PLAN): finds the running DeotPDF windows from their session markers,
/// asks each which document it shows (<c>hello</c>) and sends a request to the one meant. The window asks the user for
/// consent and runs the command on its open document. <c>deot live docs|context|view|goto|flash</c> and any command with
/// <c>--live [--doc &lt;id&gt;]</c> come here.
/// </summary>
public static class LiveClient
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>The name a client goes by in the window's question: <c>DEOT_CLIENT</c>, else "deot".</summary>
    public static string DefaultClient => Environment.GetEnvironmentVariable("DEOT_CLIENT") is { Length: > 0 } name ? name : "deot";

    /// <summary>The command line: <c>live …</c> or a command with <c>--live</c>. Prints the JSON and returns the exit code.</summary>
    public static int Run(IReadOnlyList<string> args, TextWriter stdout, string client)
    {
        var (exit, json) = RunCore(args, client);
        stdout.WriteLine(json.ToJsonString(args.Contains("--pretty") ? CliJson.Pretty : CliJson.Compact));
        stdout.Flush();
        return exit;
    }

    private static (int, JsonObject) RunCore(IReadOnlyList<string> args, string client)
    {
        try
        {
            var rest = args.Where(a => a != "--pretty").ToList();
            string? doc = TakeValue(rest, "--doc");
            if (rest.Count > 0 && rest[0] == "live")
            {
                string sub = rest.Count > 1 ? rest[1] : "";
                var options = rest.Skip(2).ToList();
                return sub switch
                {
                    "docs" => (ExitCodes.Ok, Documents()),
                    "context" => Send(doc, client, "context", options),
                    "goto" or "flash" => Send(doc, client, sub, options),
                    "view" => Command(doc, client, ["render", .. options, "--view"]),
                    _ => throw CliException.Invalid("Use: deot live docs | context | view -o <png> | goto -p <page> [--rect x,y,w,h] | flash -p <page> [--rect x,y,w,h] [--doc <id>]"),
                };
            }
            rest.Remove("--live");
            return Command(doc, client, rest);
        }
        catch (CliException e) { return (e.ExitCode, CliJson.Error(e.Code, e.Message, e.Details)); }
    }

    /// <summary>A command line without its input PDF: <c>@live</c> takes its place, a plan file is sent as standard input and a PNG comes back as bytes.</summary>
    private static (int, JsonObject) Command(string? doc, string client, List<string> tokens)
    {
        string name = CliApp.FindName(tokens) ?? throw new CliException("UNKNOWN_COMMAND", "Unknown command. Run 'deot schema' for the list.");
        int words = name.Split(' ').Length;
        var args = new List<string>(tokens);
        if (name is not ("schema" or "fonts")) args.Insert(words, CliLive.Input);
        string? stdin = null;
        if (name == "apply")
        {
            int plan = words + 1;
            if (plan >= args.Count || args[plan].StartsWith('-') && args[plan] != "-")
                throw CliException.Invalid("Give the operations file: deot apply --live <ops.json> (- reads standard input).");
            stdin = args[plan] == "-" ? Console.In.ReadToEnd() : File.ReadAllText(CliPaths.Full(args[plan]), Encoding.UTF8);
            args[plan] = "-";
        }
        return Execute(doc, client, args, stdin);
    }

    /// <summary>Runs <paramref name="args"/> (with <c>@live</c>) in the window and writes a PNG it sends back where <c>--out</c> says.</summary>
    public static (int Exit, JsonObject Json) Execute(string? doc, string client, IReadOnlyList<string> args, string? stdin)
    {
        var list = args.ToList();
        int outIndex = list.FindIndex(a => a is "--out" or "-o");
        string? output = outIndex >= 0 && outIndex + 1 < list.Count ? CliPaths.Full(list[outIndex + 1]) : null;
        if (output is not null) list[outIndex + 1] = output;
        var (exit, json, files) = SendRaw(doc, client, "command", list, stdin);
        if (exit == ExitCodes.Ok && output is not null && files?["out"] is JsonValue b64)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllBytes(output, Convert.FromBase64String(b64.GetValue<string>()));
        }
        return (exit, json);
    }

    /// <summary>A request other than a command (<c>context</c>, <c>goto</c>, <c>flash</c>).</summary>
    public static (int Exit, JsonObject Json) Send(string? doc, string client, string op, IReadOnlyList<string> args)
    {
        var (exit, json, _) = SendRaw(doc, client, op, args, null);
        return (exit, json);
    }

    private static (int, JsonObject, JsonObject?) SendRaw(string? doc, string client, string op, IReadOnlyList<string> args, string? stdin)
    {
        int window = Target(doc);
        var request = new JsonObject
        {
            ["op"] = op, ["client"] = client, ["doc"] = doc,
            ["args"] = new JsonArray([.. args.Select(a => (JsonNode)a)]),
            ["stdin"] = stdin,
        };
        var answer = Ask(window, request, Timeout.Infinite) ?? throw NotRunning();
        int exit = answer["exit"] is JsonValue e && e.TryGetValue(out int code) ? code : ExitCodes.Internal;
        var result = answer["result"] as JsonObject ?? CliJson.Error("INTERNAL", "The window sent no result.", null);
        return (exit, (JsonObject)result.DeepClone(), answer["files"] as JsonObject);
    }

    /// <summary><c>live docs</c>: every running window and the document it shows.</summary>
    public static JsonObject Documents()
    {
        var list = new JsonArray();
        foreach (var (_, hello) in Hellos()) list.Add(Describe(hello));
        return new JsonObject { ["ok"] = true, ["command"] = "live docs", ["documents"] = list };
    }

    private static JsonObject Describe(JsonObject hello) => new()
    {
        ["doc"] = hello["doc"]?.DeepClone(), ["name"] = hello["name"]?.DeepClone(), ["path"] = hello["path"]?.DeepClone(),
        ["dirty"] = hello["dirty"]?.DeepClone(), ["pageCount"] = hello["pageCount"]?.DeepClone(),
        ["enabled"] = hello["enabled"]?.DeepClone(), ["access"] = hello["access"]?.DeepClone(),
    };

    /// <summary>The window to send to: the one showing <paramref name="doc"/>, or the only one.</summary>
    private static int Target(string? doc)
    {
        var hellos = Hellos();
        if (hellos.Count == 0) throw NotRunning();
        var documents = new JsonArray([.. hellos.Select(h => (JsonNode)Describe(h.Hello))]);
        if (doc is not null)
        {
            foreach (var (pid, hello) in hellos)
                if (hello["doc"]?.GetValue<string>() == doc) return pid;
            throw new CliException("LIVE_DOC_NOT_FOUND", $"No DeotPDF window shows the document {doc} (it was closed or another file was opened).", ExitCodes.InvalidInput,
                new JsonObject { ["documents"] = documents });
        }
        if (hellos.Count > 1)
            throw new CliException("LIVE_DOC_AMBIGUOUS", $"{hellos.Count} DeotPDF windows are open; say which with --doc (doc of one of the documents).", ExitCodes.InvalidInput,
                new JsonObject { ["documents"] = documents });
        return hellos[0].Pid;
    }

    private static List<(int Pid, JsonObject Hello)> Hellos()
    {
        var list = new List<(int, JsonObject)>();
        foreach (int pid in BackupStore.RunningWindows(AppSettings.Folder))
            if (Ask(pid, new JsonObject { ["op"] = "hello" }, 2000) is { } hello) list.Add((pid, hello));
        return list;
    }

    /// <summary>One request and its answer; null when the window cannot be reached.</summary>
    private static JsonObject? Ask(int pid, JsonObject request, int timeoutMs)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", CliLive.PipeName(pid), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(1000);
            using var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Utf8, leaveOpen: true);
            writer.WriteLine(request.ToJsonString());
            var line = timeoutMs == Timeout.Infinite ? reader.ReadLine()
                : reader.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).GetAwaiter().GetResult();
            return line is null ? null : JsonNode.Parse(line) as JsonObject;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    private static CliException NotRunning() =>
        new("LIVE_NOT_RUNNING", "No DeotPDF window is running (or none can be reached). Open the PDF in DeotPDF first.", ExitCodes.InvalidInput);

    private static string? TakeValue(List<string> args, string option)
    {
        int i = args.IndexOf(option);
        if (i < 0) return null;
        if (i + 1 >= args.Count) throw CliException.Invalid($"Option {option} needs a value.");
        string value = args[i + 1];
        args.RemoveRange(i, 2);
        return value;
    }
}
