using System.IO;
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
    /// <paramref name="plan"/> is the operation list of <c>apply</c>.
    /// </summary>
    public static LiveLevel Needs(string command, IReadOnlyList<string> args, JsonArray? plan)
    {
        if (command == "apply")
        {
            var level = LiveLevel.Read;
            foreach (var op in plan ?? [])
            {
                string name = op?["op"] is JsonValue n && n.TryGetValue(out string? s) ? s : "";
                bool untagged = op?["allowUntagged"] is JsonValue v && v.TryGetValue(out bool b) && b;
                var needed = OpLevel(name.Replace('.', ' '), untagged);
                if (needed > level) level = needed;
            }
            return level;
        }
        return IsWrite(command) ? OpLevel(command, args.Contains("--allow-untagged")) : LiveLevel.Read;
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
        using var fork = document.Fork();
        long start = fork.Session.Revision;
        var context = new LiveContext(CliDocument.Live(fork, id), stdin);
        var previous = current.Value;
        current.Value = context;
        try
        {
            using var output = new StringWriter();
            using var log = new StringWriter();
            int exit = CliApp.Run(tokens, output, log);
            var operations = exit == ExitCodes.Ok ? fork.Session.OperationsSince(start) : [];
            return new LiveRun(exit, output.ToString().Trim(), operations);
        }
        finally { current.Value = previous; }
    }
}

internal sealed record LiveContext(CliDocument Document, string? StandardInput);

/// <summary>A command run on a live document: exit code, its JSON and the operations a writing command made.</summary>
public sealed record LiveRun(int ExitCode, string Json, IReadOnlyList<EditOperation> Operations);
