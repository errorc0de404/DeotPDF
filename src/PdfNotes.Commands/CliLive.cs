using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfNotes.Core.Commands;
using PdfNotes.Editor.Services;

namespace PdfNotes.Cli;

/// <summary>Permission steps a user grants an AI client on one open document (MCP_LIVE_PLAN 2.1).</summary>
public enum LiveLevel { None = 0, Read = 1, AiWrite = 2, FullEdit = 3 }

/// <summary>
/// Running commands on a document a DeotPDF window has open instead of a file (MCP_LIVE_PLAN 8.2). The window runs the
/// same command code: <c>@live</c> takes the place of the input PDF, and <see cref="Run"/> makes it mean a scratch copy
/// of the window's document (<see cref="OpenDocument.Fork"/>). Reads see that copy; a writing command's operations are
/// handed back (<see cref="LiveRun.Operations"/>) so that the window executes them as one Undo unit.
/// </summary>
public static class CliLive
{
    /// <summary>The local pipe the DeotPDF window in process <paramref name="processId"/> listens on (current user only).</summary>
    public static string PipeName(long processId) => $"deotpdf-live-{processId}";

    /// <summary>The input argument that names the live document.</summary>
    public const string Input = "@live";

    private static readonly AsyncLocal<LiveContext?> current = new();

    internal static LiveContext? Current => current.Value;

    /// <summary>Commands that have no meaning on an open document or would read or write files for the window.</summary>
    public static readonly IReadOnlySet<string> Unsupported = new HashSet<string>(StringComparer.Ordinal)
    {
        "page export", "page import", "add image",
    };

    /// <summary>
    /// The permission a command needs: reads need <see cref="LiveLevel.Read"/>; AI writing (add, AI annotations, blank
    /// pages) <see cref="LiveLevel.AiWrite"/>; the user's writing and page structure <see cref="LiveLevel.FullEdit"/>.
    /// <paramref name="plan"/> is the operation list of <c>apply</c> and <paramref name="defaults"/> its <c>defaults</c>
    /// object, which <c>apply</c> merges into the operations: an <c>allowUntagged</c> anywhere in it counts for all of them.
    /// </summary>
    public static LiveLevel Needs(string command, IReadOnlyList<string> args, JsonArray? plan, JsonNode? defaults = null)
    {
        if (command == "apply")
        {
            var level = AllowsUntagged(defaults) ? LiveLevel.FullEdit : LiveLevel.Read;
            foreach (var op in plan ?? [])
            {
                string name = op?["op"] is JsonValue n && n.TryGetValue(out string? s) ? s : "";
                bool untagged = op is JsonObject fields && fields.Any(IsAllowUntagged);
                var needed = OpLevel(name.Replace('.', ' '), untagged);
                if (needed > level) level = needed;
            }
            return level;
        }
        return IsWrite(command) ? OpLevel(command, args.Contains("--allow-untagged")) : LiveLevel.Read;
    }

    /// <summary>
    /// A field that <c>apply</c> turns into <c>--allow-untagged</c>. Judged by the same mapping <c>apply</c> uses
    /// (<c>AllowUntagged</c> and <c>allow-untagged</c> become that option too), and anything but a plain false counts, so
    /// no spelling gets the user's writing changed with the permission for AI writing only.
    /// </summary>
    private static bool IsAllowUntagged(KeyValuePair<string, JsonNode?> field) =>
        string.Equals(Commands.ApplyCommand.Option(field.Key), "--allow-untagged", StringComparison.OrdinalIgnoreCase) &&
        !(field.Value is JsonValue v && v.GetValueKind() == JsonValueKind.False);

    private static bool AllowsUntagged(JsonNode? node) => node switch
    {
        JsonObject o => o.Any(p => IsAllowUntagged(p) || AllowsUntagged(p.Value)),
        JsonArray a => a.Any(AllowsUntagged),
        _ => false,
    };

    /// <summary>The folder a live command may read and write files in: only the PNG the window hands back is put there.</summary>
    public static string ScratchFolder => Path.Combine(Path.GetTempPath(), "deotpdf-live");

    /// <summary>
    /// Null when <paramref name="tokens"/> have the shape of a live command: the input right after the command name is
    /// exactly <c>@live</c> (so no file of the client's choosing is opened), and the plan of <c>apply</c> is <c>-</c>
    /// (standard input, which the permission check has read). Otherwise why not.
    /// </summary>
    public static string? ShapeError(string command, IReadOnlyList<string> tokens)
    {
        if (command is "schema" or "fonts" or "measure") return tokens.Contains(Input) ? $"'{command}' takes no input document; leave out {Input}." : null;
        int words = command.Split(' ').Length;
        if (tokens.Count <= words || tokens[words] != Input || tokens.Count(t => t == Input) != 1)
            return $"A live command takes {Input} as its input PDF, right after the command name.";
        if (command == "apply" && (tokens.Count <= words + 1 || tokens[words + 1] != "-"))
            return "A live apply reads its plan from standard input: apply @live -";
        return null;
    }

    /// <summary>The op names of an <c>apply</c> plan that <see cref="Unsupported"/> refuses.</summary>
    public static IEnumerable<string> UnsupportedOps(JsonArray? plan) =>
        (plan ?? []).Select(op => op?["op"] is JsonValue n && n.TryGetValue(out string? s) ? s.Replace('.', ' ') : "").Where(Unsupported.Contains);

    private static LiveLevel OpLevel(string name, bool untagged)
    {
        if (untagged) return LiveLevel.FullEdit;
        if (name.StartsWith("add ", StringComparison.Ordinal) || name.StartsWith("annot ", StringComparison.Ordinal) || name == "page insert-blank") return LiveLevel.AiWrite;
        return LiveLevel.FullEdit;
    }

    /// <summary>True for <c>apply</c> and the single writing commands.</summary>
    public static bool IsWrite(string command) => command == "apply" || CliApp.Ops.ContainsKey(command.Replace(' ', '.'));

    /// <summary>The command name in <paramref name="tokens"/> (<c>annot list</c>, <c>apply</c>, ...), or null.</summary>
    public static string? CommandOf(IReadOnlyList<string> tokens) => CliApp.FindName(tokens);

    /// <summary>
    /// Runs one command (its tokens, <c>@live</c> as the input) on a scratch copy of <paramref name="document"/>.
    /// <paramref name="stdin"/> is what <c>-</c> reads (the plan of <c>apply</c>). The result's JSON is what the command
    /// line prints; the window executes <see cref="LiveRun.Operations"/> itself.
    /// </summary>
    public static LiveRun Run(OpenDocument document, string id, IReadOnlyList<string> tokens, string? stdin)
    {
        long revision = document.Session.Revision; // the window's document, not the copy (a new session starts at 1)
        using var fork = document.Fork();
        long start = fork.Session.Revision;
        var context = new LiveContext(CliDocument.Live(fork, id, revision), stdin);
        var previous = current.Value;
        var roots = CliPaths.Roots;
        current.Value = context;
        // The window opens and writes no file a client names (--text @file, --style file, overlays, ...): only its own
        // scratch folder, where the PNG it hands back is drawn.
        CliPaths.Roots = [ScratchFolder];
        try
        {
            using var output = new StringWriter();
            using var log = new StringWriter();
            int exit = CliApp.Run(tokens, output, log);
            var operations = exit == ExitCodes.Ok && !context.DryRun ? fork.Session.OperationsSince(start) : [];
            return new LiveRun(exit, output.ToString().Trim(), operations);
        }
        finally
        {
            current.Value = previous;
            CliPaths.Roots = roots;
        }
    }
}

internal sealed record LiveContext(CliDocument Document, string? StandardInput)
{
    /// <summary>The command asked for <c>--dry-run</c>: its result is reported and nothing reaches the window's document.</summary>
    public bool DryRun { get; set; }
}

/// <summary>A command run on a live document: exit code, its JSON and the operations a writing command made.</summary>
public sealed record LiveRun(int ExitCode, string Json, IReadOnlyList<EditOperation> Operations);
