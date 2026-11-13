using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PdfNotes.Cli.Commands;

namespace PdfNotes.Cli.Mcp;

/// <summary>
/// <c>deot mcp --root &lt;folder&gt; [--root &lt;folder&gt;]…</c>: a stdio MCP server over the same commands. A tool call becomes the command
/// line <see cref="McpTools.Tokens"/> gives and runs through <see cref="CliApp.Run"/>, so results, error codes and files
/// are those of the CLI; the result's text is the command's JSON (<c>isError</c> when it did not succeed) and PNGs it
/// made come back as images. Calls run one at a time. Files are limited to the <c>--root</c> folders (<see cref="CliPaths"/>;
/// client roots are deprecated in MCP 2026-07-28, so the server does not ask for them).
/// </summary>
public sealed class DeotMcpServer : IDisposable
{
    public const string Instructions =
        "deot reads PDFs and writes annotations that DeotPDF opens as its own (text boxes, highlights, lines, shapes, ink, images, notes). " +
        "Coordinates are PDF points with the origin at the top-left of the page before rotation, y downward; rectangles [x, y, w, h]. " +
        "Pages are numbered from 1 (or page IDs from info); character and word ranges start at 0 and exclude the end. " +
        "Fields are the command-line options in camelCase; a flag is true. " +
        "The input PDF is never changed: writing gives a new file (out) or nothing (dryRun). " +
        "A workflow that works well: info, render with grid for an overview, then per page layout, find, ink with blocks and space; " +
        "measure text before placing it; put every edit in one apply plan with source keys, run it with dryRun and previews, add guards, " +
        "and always apply the full plan to the original file so re-runs replace instead of duplicate. " +
        "The schema tool lists every command, error code and an op's fields (e.g. command \"add text\" for add.text). " +
        "When the user is studying with the PDF open in DeotPDF, work on that open document instead of a file: live_docs lists it, " +
        "live_context tells what they see and selected, render with live and view shows it, other tools take live: true (no pdf), " +
        "an apply with live is applied in their window at once as one Undo step (not saved), and live_flash points at a place. " +
        "The user is asked once to allow access; LIVE_CONSENT_TIMEOUT means ask them to answer the bar in DeotPDF and try again.";

    private readonly IReadOnlyList<string> roots;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "deot-mcp", Guid.NewGuid().ToString("N"));
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TextWriter log;

    /// <param name="roots">Allowed folders; the first one is where relative paths start.</param>
    /// <param name="log">Where the commands' standard error goes (never the protocol's stdout).</param>
    public DeotMcpServer(IReadOnlyList<string> roots, TextWriter log)
    {
        if (roots.Count == 0) throw new ArgumentException("At least one root is needed.", nameof(roots));
        this.roots = [.. roots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))), scratch];
        this.log = log;
    }

    public static int Run(IReadOnlyList<string> args)
    {
        var roots = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--root" && i + 1 < args.Count) roots.Add(args[++i]);
            else
            {
                Console.Error.WriteLine($"deot mcp: unknown argument {args[i]}. {Usage}");
                return ExitCodes.InvalidInput;
            }
        }
        if (roots.Count == 0)
        {
            Console.Error.WriteLine($"deot mcp: give the folders the server may read and write. {Usage}");
            return ExitCodes.InvalidInput;
        }
        foreach (string root in roots)
            if (!Directory.Exists(root))
            {
                Console.Error.WriteLine($"deot mcp: the folder {Path.GetFullPath(root)} does not exist.");
                return ExitCodes.InvalidInput;
            }
        using var server = new DeotMcpServer(roots, Console.Error);
        var options = server.Options();
        server.RunAsync(new StdioServerTransport(options, null), options, CancellationToken.None).GetAwaiter().GetResult();
        return ExitCodes.Ok;
    }

    private const string Usage = "Usage: deot mcp --root <folder> [--root <folder>]...";

    public McpServerOptions Options() => new()
    {
        ServerInfo = new Implementation { Name = "deot", Title = "DeotPDF", Version = Version() },
        ServerInstructions = Instructions,
        Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [.. McpTools.All.Select(t => t.Tool)] }),
            CallToolHandler = CallAsync,
        },
    };

    public async Task RunAsync(ITransport transport, McpServerOptions options, CancellationToken cancel)
    {
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(cancel);
    }

    private static string Version()
    {
        string version = typeof(DeotMcpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        return version.Split('+')[0];
    }

    private async ValueTask<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancel)
    {
        var call = request.Params ?? throw new McpProtocolException("Missing tool call.", McpErrorCode.InvalidParams);
        var tool = McpTools.Find(call.Name) ?? throw new McpProtocolException($"Unknown tool '{call.Name}'.", McpErrorCode.InvalidParams);
        var arguments = call.Arguments is null ? null : new Dictionary<string, JsonElement>(call.Arguments);
        string client = request.Server.ClientInfo?.Title is { Length: > 0 } title ? title : request.Server.ClientInfo?.Name ?? "AI";
        await gate.WaitAsync(cancel);
        try { return await Task.Run(() => Call(tool, arguments, client), cancel); }
        finally { gate.Release(); }
    }

    /// <summary>Runs one tool call with the roots as the allowed folders.</summary>
    public CallToolResult Call(McpTools.Entry tool, IReadOnlyDictionary<string, JsonElement>? arguments, string client = "AI")
    {
        if (tool.Command.StartsWith("live ", StringComparison.Ordinal) || Flag(arguments, "live")) return CallLive(tool, arguments, client);
        Directory.CreateDirectory(scratch);
        var images = new List<string>();
        string? planFile = null;
        try
        {
            List<string> tokens;
            CliPaths.Roots = roots;
            try
            {
                tokens = tool.Command switch
                {
                    "render" => RenderTokens(tool, arguments, images),
                    "apply" => ApplyTokens(tool, arguments, out planFile),
                    _ => McpTools.Tokens(tool, arguments),
                };
            }
            catch (CliException e)
            {
                return Failure(CliJson.Error(e.Code, e.Message, e.Details));
            }

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exit = CliApp.Run(tokens, stdout, stderr);
            if (stderr.GetStringBuilder().Length > 0) log.Write(stderr.ToString());
            string json = stdout.ToString().Trim();
            var result = new CallToolResult { Content = [new TextContentBlock { Text = json }], IsError = exit != ExitCodes.Ok };
            if (exit != ExitCodes.Ok) return result;

            var output = JsonNode.Parse(json)!;
            if (tool.Command == "apply" && Flag(arguments, "previews") && output["previews"] is JsonArray previews)
                images.AddRange(previews.Select(p => p!["path"]!.GetValue<string>()));
            foreach (string png in images.Where(File.Exists))
                result.Content.Add(ImageContentBlock.FromBytes(File.ReadAllBytes(png), "image/png"));
            return result;
        }
        finally
        {
            CliPaths.Roots = null;
            if (planFile is not null) File.Delete(planFile);
        }
    }

    /// <summary><c>render</c> without <c>out</c> draws into the server's own folder; the PNG is returned either way.</summary>
    private List<string> RenderTokens(McpTools.Entry tool, IReadOnlyDictionary<string, JsonElement>? arguments, List<string> images)
    {
        var tokens = McpTools.Tokens(tool, arguments, new HashSet<string> { "out" });
        string out_ = arguments?.TryGetValue("out", out var o) == true && o.ValueKind == JsonValueKind.String
            ? CliPaths.Full(o.GetString()!)
            : Path.Combine(scratch, $"render-{Guid.NewGuid():N}.png");
        tokens.AddRange(["--out", out_]);
        images.Add(out_);
        return tokens;
    }

    /// <summary>
    /// <c>apply</c> takes the plan itself: it is written to a file of the server's own for the command. <c>previews</c>
    /// without <c>previewDir</c> renders into the server's folder.
    /// </summary>
    private List<string> ApplyTokens(McpTools.Entry tool, IReadOnlyDictionary<string, JsonElement>? arguments, out string? planFile)
    {
        planFile = null;
        if (arguments is null || !arguments.TryGetValue("plan", out var plan) || plan.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw CliException.Invalid("apply needs \"plan\": { \"ops\": [...] } or the list of ops.");
        if (!arguments.ContainsKey("pdf")) throw CliException.Invalid("The input PDF is missing.");
        var tokens = McpTools.Tokens(tool, arguments, new HashSet<string> { "plan", "previews" });
        planFile = Path.Combine(scratch, $"plan-{Guid.NewGuid():N}.json");
        File.WriteAllText(planFile, plan.GetRawText(), new UTF8Encoding(false));
        tokens.Insert(2, planFile); // after "apply <pdf>"
        if (Flag(arguments, "previews") && !arguments.ContainsKey("previewDir"))
            tokens.AddRange(["--preview-dir", Path.Combine(scratch, $"previews-{Guid.NewGuid():N}")]);
        return tokens;
    }

    /// <summary>A call on the document open in DeotPDF (MCP_LIVE_PLAN): sent to its window through <see cref="LiveClient"/>.</summary>
    private CallToolResult CallLive(McpTools.Entry tool, IReadOnlyDictionary<string, JsonElement>? arguments, string client)
    {
        Directory.CreateDirectory(scratch);
        CliPaths.Roots = roots;
        try
        {
            string? doc = arguments?.TryGetValue("doc", out var d) == true && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (arguments?.ContainsKey("pdf") == true) throw CliException.Invalid("Give pdf or live, not both.");
            (int Exit, JsonObject Json) answer;
            var images = new List<string>();
            switch (tool.Command)
            {
                case "live docs":
                    answer = (ExitCodes.Ok, LiveClient.Documents(client));
                    break;
                case "live context":
                    answer = LiveClient.Send(doc, client, "context", []);
                    break;
                case "live goto" or "live flash":
                {
                    var place = new List<string> { "--page", arguments?.TryGetValue("page", out var p) == true ? ApplyCommand.Value(JsonSerializer.SerializeToNode(p)!) : "" };
                    if (arguments?.TryGetValue("rect", out var r) == true) place.AddRange(["--rect", ApplyCommand.Value(JsonSerializer.SerializeToNode(r)!)]);
                    answer = LiveClient.Send(doc, client, tool.Command["live ".Length..], place);
                    break;
                }
                default:
                {
                    string? stdin = null;
                    List<string> tokens;
                    if (tool.Command == "apply")
                    {
                        if (arguments is null || !arguments.TryGetValue("plan", out var plan) || plan.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                            throw CliException.Invalid("apply needs \"plan\": { \"ops\": [...] } or the list of ops.");
                        tokens = McpTools.Tokens(tool, arguments, new HashSet<string> { "plan", "previews" });
                        stdin = plan.GetRawText();
                        tokens.InsertRange(1, [CliLive.Input, "-"]);
                    }
                    else
                    {
                        tokens = tool.Command == "render" ? RenderTokens(tool, arguments, images) : McpTools.Tokens(tool, arguments);
                        if (tool.Command is not ("schema" or "fonts")) tokens.Insert(tool.Command.Split(' ').Length, CliLive.Input);
                    }
                    answer = LiveClient.Execute(doc, client, tokens, stdin);
                    break;
                }
            }
            var result = new CallToolResult { Content = [new TextContentBlock { Text = answer.Json.ToJsonString(CliJson.Compact) }], IsError = answer.Exit != ExitCodes.Ok };
            if (answer.Exit == ExitCodes.Ok)
                foreach (string png in images.Where(File.Exists))
                    result.Content.Add(ImageContentBlock.FromBytes(File.ReadAllBytes(png), "image/png"));
            return result;
        }
        catch (CliException e) { return Failure(CliJson.Error(e.Code, e.Message, e.Details)); }
        finally { CliPaths.Roots = null; }
    }

    private static bool Flag(IReadOnlyDictionary<string, JsonElement>? arguments, string name) =>
        arguments?.TryGetValue(name, out var v) == true && v.ValueKind == JsonValueKind.True;

    private static CallToolResult Failure(JsonObject error) => new()
    {
        Content = [new TextContentBlock { Text = error.ToJsonString(CliJson.Compact) }],
        IsError = true,
    };

    public void Dispose()
    {
        gate.Dispose();
        try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
