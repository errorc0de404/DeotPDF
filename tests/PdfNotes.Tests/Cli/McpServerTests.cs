using System.IO;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PdfNotes.Cli;
using PdfNotes.Cli.Mcp;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Tests.Cli;

/// <summary>
/// <c>deot mcp</c> through the protocol (an SDK client on in-memory pipes): the tools and their schemas, calls giving the
/// same JSON as the command line, PNGs as images, and files limited to the roots.
/// </summary>
public sealed class McpServerTests : IAsyncLifetime
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    private readonly string outside = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource stop = new();
    private string sample = "";
    private DeotMcpServer server = null!;
    private Task running = Task.CompletedTask;
    private McpClient client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(outside);
        sample = Path.Combine(folder, "cli-sample.pdf");
        ProbeSamplePdf.CreateCli(sample);
        File.Copy(sample, Path.Combine(outside, "cli-sample.pdf"));

        var toServer = new Pipe();
        var toClient = new Pipe();
        server = new DeotMcpServer([folder], TextWriter.Null);
        var options = server.Options();
        running = server.RunAsync(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), options, stop.Token);
        client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
    }

    public async Task DisposeAsync()
    {
        await client.DisposeAsync();
        stop.Cancel();
        try { await running; }
        catch (OperationCanceledException) { }
        server.Dispose();
        Directory.Delete(folder, true);
        Directory.Delete(outside, true);
    }

    private async Task<CallToolResult> Call(string tool, object arguments)
    {
        var args = JsonSerializer.SerializeToElement(arguments).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        return await client.CallToolAsync(tool, args);
    }

    private static JsonObject Json(CallToolResult result) =>
        JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!.AsObject();

    private static string Text(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;

    private static string CommandLine(params string[] args)
    {
        var stdout = new StringWriter();
        CliApp.Run(args, stdout, new StringWriter());
        return stdout.ToString().Trim();
    }

    [Fact]
    public async Task ListsReadingToolsApplyAndPageExportWithStandaloneSchemas()
    {
        var tools = await client.ListToolsAsync();
        Assert.Equal(["schema", "info", "render", "text", "annot_list", "find", "layout", "ink", "space", "measure", "fonts", "check", "page_export", "apply"],
            tools.Select(t => t.Name));
        foreach (var tool in tools.Select(t => t.ProtocolTool))
        {
            string schema = tool.InputSchema.GetRawText();
            Assert.DoesNotContain("common.json", schema);
            Assert.DoesNotContain("appFolder", schema);
            Assert.Equal(tool.Name is not ("render" or "page_export" or "apply"), tool.Annotations!.ReadOnlyHint);
        }
        // check rules point into the tool's own definitions.
        var check = JsonNode.Parse(tools.Single(t => t.Name == "check").ProtocolTool.InputSchema.GetRawText())!;
        Assert.NotNull(check["$defs"]!["checkRule"]);
        Assert.Equal(["pdf", "plan"], tools.Single(t => t.Name == "apply").ProtocolTool.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task ReadingToolsGiveTheCommandLinesJson()
    {
        Assert.Equal(CommandLine("info", sample), Text(await Call("info", new { pdf = sample })));
        Assert.Equal(CommandLine("text", sample, "-p", "1", "--fields", "words"), Text(await Call("text", new { pdf = sample, page = "1", fields = "words" })));
        Assert.Equal(CommandLine("layout", sample, "-p", "1"), Text(await Call("layout", new { pdf = sample, page = "1" })));
        Assert.Equal(CommandLine("find", sample, "--text", ProbeSamplePdf.CliRepeatedTerm, "-p", "1", "--context", "5", "--include-annotations"),
            Text(await Call("find", new { pdf = sample, text = ProbeSamplePdf.CliRepeatedTerm, page = "1", context = 5, includeAnnotations = true })));
        Assert.Equal(CommandLine("ink", sample, "-p", "2", "--blocks"), Text(await Call("ink", new { pdf = sample, page = "2", blocks = true })));
        Assert.Equal(CommandLine("space", sample, "-p", "1", "--size", "40,20", "--limit", "3"),
            Text(await Call("space", new { pdf = sample, page = "1", size = "40,20", limit = 3 })));
        Assert.Equal(CommandLine("measure", "--text", "**굵게** 보통", "--width", "120"), Text(await Call("measure", new { text = "**굵게** 보통", width = 120 })));
        Assert.Equal(CommandLine("annot", "list", sample), Text(await Call("annot_list", new { pdf = sample })));
        Assert.Equal(CommandLine("schema", "add", "text"), Text(await Call("schema", new { command = "add text" })));
        Assert.Equal(CommandLine("schema", "annot", "list"), Text(await Call("schema", new { command = "annot_list" })));
        // A relative path starts at the first root; false flags are left out.
        Assert.Equal(CommandLine("info", sample), Text(await Call("info", new { pdf = "cli-sample.pdf" })));
        var rules = new JsonArray(new JsonObject { ["rule"] = "outsidePage" });
        Assert.Equal(CommandLine("check", sample, "--rules", rules.ToJsonString()), Text(await Call("check", new { pdf = sample, rules })));
        Assert.Equal(CommandLine("find", sample, "--text", ProbeSamplePdf.CliRepeatedTerm),
            Text(await Call("find", new { pdf = sample, text = ProbeSamplePdf.CliRepeatedTerm, fuzzy = false })));
    }

    [Fact]
    public async Task ApplyTakesThePlanAndWritesWhatTheCommandLineWrites()
    {
        var plan = new JsonObject
        {
            ["batchId"] = "mcp-test",
            ["ops"] = new JsonArray(
                new JsonObject { ["op"] = "add.text", ["page"] = 1, ["rect"] = new JsonArray(40, 40, 160, 0), ["text"] = "**MCP** 시험", ["sourceKey"] = "p1/t" },
                new JsonObject { ["op"] = "add.mark", ["page"] = 1, ["words"] = new JsonObject { ["start"] = 0, ["end"] = 2 }, ["sourceKey"] = "p1/m" },
                new JsonObject { ["op"] = "page.rotate", ["page"] = 2, ["deg"] = 90 }),
        };
        string viaCli = Path.Combine(folder, "cli.pdf"), viaMcp = Path.Combine(folder, "mcp.pdf");
        string planFile = Path.Combine(folder, "plan.json");
        File.WriteAllText(planFile, plan.ToJsonString());
        var cli = JsonNode.Parse(CommandLine("apply", sample, planFile, "-o", viaCli))!;
        var result = await Call("apply", new { pdf = sample, plan, @out = "mcp.pdf" });
        Assert.False(result.IsError);
        var mcp = Json(result);
        Assert.Equal(viaMcp, mcp["output"]!.GetValue<string>());
        Assert.Equal(cli["summary"]!["annotations"]!["created"]!.AsArray().Count, mcp["summary"]!["annotations"]!["created"]!.AsArray().Count);
        Assert.Empty(CliCompare.Differences(viaCli, viaMcp));

        // A list of ops is a plan too; previews come back as images.
        var dry = await Call("apply", new { pdf = sample, plan = plan["ops"], dryRun = true, previews = true });
        Assert.False(dry.IsError);
        Assert.True(Json(dry)["dryRun"]!.GetValue<bool>());
        Assert.Equal(2, dry.Content.OfType<ImageContentBlock>().Count());
        Assert.All(dry.Content.OfType<ImageContentBlock>(), i => Assert.Equal("image/png", i.MimeType));

        // Errors are the command's, with isError.
        var exists = await Call("apply", new { pdf = sample, plan, @out = "mcp.pdf" });
        Assert.True(exists.IsError);
        Assert.Equal("OUTPUT_EXISTS", Json(exists)["error"]!["code"]!.GetValue<string>());
        var noPlan = await Call("apply", new { pdf = sample, @out = "x.pdf" });
        Assert.True(noPlan.IsError);
        Assert.Equal("INVALID_ARGUMENT", Json(noPlan)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task RenderReturnsThePngAndKeepsItOnlyWhenAsked()
    {
        var shown = await Call("render", new { pdf = sample, page = "1", dpi = 40 });
        Assert.False(shown.IsError);
        var image = Assert.Single(shown.Content.OfType<ImageContentBlock>());
        var json = Json(shown);
        var decoded = image.DecodedData.ToArray();
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], decoded[..4]);
        Assert.False(CliPaths.Inside(json["output"]!.GetValue<string>(), folder)); // the server's own folder

        var kept = await Call("render", new { pdf = sample, pages = "1-3", grid = true, @out = "grid.png" });
        Assert.Equal(Path.Combine(folder, "grid.png"), Json(kept)["output"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(folder, "grid.png")));
        Assert.Single(kept.Content.OfType<ImageContentBlock>());
    }

    [Fact]
    public async Task FilesOutsideTheRootsAreRefused()
    {
        string other = Path.Combine(outside, "cli-sample.pdf");
        async Task Refused(string tool, object arguments)
        {
            var result = await Call(tool, arguments);
            Assert.True(result.IsError, Text(result));
            Assert.Equal("PATH_NOT_ALLOWED", Json(result)["error"]!["code"]!.GetValue<string>());
        }
        await Refused("info", new { pdf = other });
        await Refused("info", new { pdf = Path.Combine("..", Path.GetFileName(outside), "cli-sample.pdf") });
        await Refused("render", new { pdf = sample, page = "1", @out = Path.Combine(outside, "p.png") });
        await Refused("apply", new { pdf = sample, plan = new JsonArray(), @out = Path.Combine(outside, "o.pdf") });
        await Refused("apply", new { pdf = sample, plan = new JsonArray(), dryRun = true, previewDir = outside });
        await Refused("page_export", new { pdf = sample, page = "1", @out = Path.Combine(outside, "e.pdf") });
        File.WriteAllText(Path.Combine(outside, "text.json"), "[{\"text\": \"x\"}]");
        await Refused("measure", new { text = "@" + Path.Combine(outside, "text.json") });
        await Refused("apply", new
        {
            pdf = sample, dryRun = true,
            plan = new JsonArray(new JsonObject { ["op"] = "page.import", ["from"] = other, ["after"] = 0 }),
        });
        Assert.False(File.Exists(Path.Combine(outside, "p.png")));

        var unknown = await Call("info", new { pdf = sample, appFolder = outside });
        Assert.Equal("INVALID_ARGUMENT", Json(unknown)["error"]!["code"]!.GetValue<string>());
    }
}
