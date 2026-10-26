using System.IO;
using System.Text.Json.Nodes;
using PdfNotes.Cli.Commands;

namespace PdfNotes.Cli;

/// <summary>Command dispatch, JSON output on stdout, human log on stderr (cli_plan 2, 3.1).</summary>
public static class CliApp
{
    /// <summary>One command: its name as typed (e.g. <c>annot list</c>), a one-line summary and its handler.</summary>
    public sealed record Command(string Name, string Summary, Func<IReadOnlyList<string>, JsonObject> Run);

    public static readonly Command[] Commands =
    [
        new("schema", "JSON schemas of command inputs and outputs", SchemaCommand.Run),
        new("info", "Pages, sizes, rotation, text layer, annotation counts and file hash", InfoCommand.Run),
        new("render", "Page, region or page grid as PNG with page-to-pixel matrices", RenderCommand.Run),
        new("text", "PDF text layer: characters, words and lines with quads", TextCommand.Run),
        new("annot list", "Annotations with kind, bounds, style, text and tags", AnnotListCommand.Run),
        new("find", "Phrase candidates in the text layer with characters, quads and context", FindCommand.Run),
        new("layout", "Text lines grouped into paragraph and list blocks", LayoutCommand.Run),
        new("ink", "Ink fraction per grid cell and connected ink blocks of the rendered page", InkCommand.Run),
        new("space", "Empty rectangle candidates avoiding ink and given areas", SpaceCommand.Run),
        new("measure", "Text box size, lines, fonts and missing glyphs from the app's text engine", MeasureCommand.Run),
        new("fonts", "Font families the text engine accepts", FontsCommand.Run),
        .. PageCommand.Ops.Select(op => op.Command),
        new("page export", "Writes some pages as a PDF of their own", PageCommand.Export),
        .. AddCommand.Ops.Select(op => op.Command),
        .. AnnotCommand.Ops.Select(op => op.Command),
        new("check", "Geometric checks of annotations by rules: regions, overlaps, page edges, text", CheckCommand.Run),
        new("apply", "Runs a list of writing operations atomically and writes once", ApplyCommand.Run),
    ];

    /// <summary>The writing operations <c>apply</c> runs, by op name (<c>add.text</c>, <c>page.shrink</c>, ...).</summary>
    public static readonly IReadOnlyDictionary<string, CliOp> Ops =
        PageCommand.Ops.Concat(AddCommand.Ops).Concat(AnnotCommand.Ops).ToDictionary(op => op.OpName);

    /// <summary>Runs one command; WPF text and imaging need an STA thread, so other callers (tests) are moved onto one.</summary>
    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return RunHere(args, stdout, stderr);
        int exit = ExitCodes.Internal;
        var thread = new Thread(() => exit = RunHere(args, stdout, stderr));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exit;
    }

    private static int RunHere(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        bool pretty = args.Contains("--pretty");
        JsonObject output;
        int exit;
        try
        {
            PdfNotes.Text.WpfText.Register();
            var (command, rest) = Find(args);
            output = command.Run(rest);
            exit = ExitCodes.Ok;
        }
        catch (CliException e)
        {
            output = CliJson.Error(e.Code, e.Message, e.Details);
            exit = e.ExitCode;
        }
        catch (Exception e)
        {
            stderr.WriteLine(e.ToString());
            output = CliJson.Error("INTERNAL", e.Message, new JsonObject { ["type"] = e.GetType().Name });
            exit = ExitCodes.Internal;
        }
        stdout.WriteLine(output.ToJsonString(pretty ? CliJson.Pretty : CliJson.Compact));
        stdout.Flush();
        return exit;
    }

    private static (Command Command, IReadOnlyList<string> Remaining) Find(IReadOnlyList<string> args)
    {
        var words = args.TakeWhile(a => !a.StartsWith('-')).ToList();
        foreach (var command in Commands.OrderByDescending(c => c.Name.Count(ch => ch == ' ')))
        {
            var parts = command.Name.Split(' ');
            if (words.Count >= parts.Length && parts.Select((p, i) => p == words[i]).All(x => x))
                return (command, args.Skip(parts.Length).ToList());
        }
        if (words.Count == 0) throw new CliException("UNKNOWN_COMMAND", "No command was given. Run 'deot schema' for the list.");
        throw new CliException("UNKNOWN_COMMAND", $"Unknown command '{words[0]}'. Run 'deot schema' for the list.",
            ExitCodes.InvalidInput, new JsonObject { ["command"] = words[0] });
    }
}
