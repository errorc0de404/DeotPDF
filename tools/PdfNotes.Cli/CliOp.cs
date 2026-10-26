using System.Text.Json.Nodes;
using PdfNotes.Core.Commands;

namespace PdfNotes.Cli;

/// <summary>
/// Where a writing operation runs: the open document and the run's batch id. <c>apply</c> also keeps the source keys its
/// operations made, because one key made twice in one run is refused (cli_plan 6).
/// </summary>
public sealed class CliContext(CliDocument doc, string batchId)
{
    public CliDocument Doc { get; } = doc;
    public string BatchId { get; } = batchId;

    /// <summary>Source keys made or replaced in this <c>apply</c> run; null for a single command.</summary>
    public HashSet<string>? KeysThisRun { get; init; }
}

/// <summary>
/// One writing operation (cli_plan 4.5–4.7): the command <see cref="Name"/> (<c>add text</c>; in <c>apply</c> its
/// <see cref="OpName"/> <c>add.text</c>), its own options and the edit on an open document. The edit fills the result
/// and returns the pages it changed. As a command it is framed by <see cref="CliWrite"/> (open, hash check, write);
/// <c>apply</c> runs many on one document and writes once.
/// </summary>
public sealed record CliOp(string Name, string Summary, string[] Values, string[] Flags, Func<CliArgs, CliContext, JsonObject, IEnumerable<Guid>> Edit)
{
    public string OpName => Name.Replace(' ', '.');

    public CliApp.Command Command => new(Name, Summary, Run);

    public JsonObject Run(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, [.. CliWrite.ValueOptions, .. Values], [.. CliWrite.FlagOptions, .. Flags]);
        string input = args.InputPath();
        var write = CliWrite.Parse(args, input);
        using var doc = CliDocument.Open(input);
        write.CheckHash(doc);
        var result = CliJson.Result(Name, doc.SourceHash);
        var changed = Execute(args, new CliContext(doc, write.BatchId!), result);
        write.Finish(doc, result, changed);
        return result;
    }

    public IReadOnlyList<Guid> Execute(CliArgs args, CliContext context, JsonObject result)
    {
        try { return Edit(args, context, result).ToList(); }
        catch (AnnotationLockedException)
        {
            throw new CliException("LOCKED_ANNOTATION", "A locked annotation would change; unlock it first (annot lock --off).", ExitCodes.Unsatisfiable);
        }
    }
}
