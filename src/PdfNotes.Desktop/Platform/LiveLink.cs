using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfNotes.Cli;
using PdfNotes.Editor.Services;
using PdfNotes.ViewModels;

namespace PdfNotes.Desktop.Platform;

/// <summary>
/// The window's end of the live link (MCP_LIVE_PLAN 8.4): <c>deot</c> connects to the local pipe named after this
/// process (<see cref="PipeName"/>, current user only) and sends one JSON request per line; each answer is one line. The
/// requests are handled by <see cref="LiveLinkViewModel"/>. The pipe listens even while the setting is off, so that the
/// window can tell the user a client asked.
/// </summary>
public static class LiveLink
{
    /// <summary>The pipe of the window in process <paramref name="processId"/>.</summary>
    public static string PipeName(long processId) => CliLive.PipeName(processId);

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Connections at a time: a few clients and their short requests, not a queue for anything on the machine.</summary>
    private const int MaxConnections = 16;

    /// <summary>The longest request line (an apply plan with long text), in characters.</summary>
    private const int MaxRequestChars = 16 * 1024 * 1024;

    /// <summary>How long a connection may stay silent before its request arrives.</summary>
    private static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(15);

    /// <summary>Listens until <paramref name="stop"/> is cancelled; <paramref name="handle"/> answers each request.</summary>
    public static Task Listen(Func<JsonObject, Task<JsonObject>> handle, CancellationToken stop) =>
        Task.Run(() => AcceptAsync(PipeName(Environment.ProcessId), handle, stop), CancellationToken.None);

    private static async Task AcceptAsync(string name, Func<JsonObject, Task<JsonObject>> handle, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(name, PipeDirection.InOut, MaxConnections,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop);
                var connected = server;
                server = null;
                _ = Task.Run(() => ServeAsync(connected, handle, stop), CancellationToken.None);
            }
            catch (OperationCanceledException) { server?.Dispose(); return; }
            catch (Exception) // too many connections, a broken pipe, ...: the listener must outlive it
            {
                server?.Dispose();
                try { await Task.Delay(1000, stop); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, Func<JsonObject, Task<JsonObject>> handle, CancellationToken stop)
    {
        await using var _ = pipe;
        using var reader = new StreamReader(pipe, Utf8);
        await using var writer = new StreamWriter(pipe, Utf8) { AutoFlush = true };
        try
        {
            while (!stop.IsCancellationRequested && await ReadRequestAsync(reader, stop) is { } line)
            {
                JsonObject answer;
                try
                {
                    answer = JsonNode.Parse(line) is JsonObject request ? await handle(request)
                        : Error("INVALID_ARGUMENT", "A request is one JSON object per line.");
                }
                catch (JsonException e) { answer = Error("INVALID_ARGUMENT", $"The request is not JSON: {e.Message}"); }
                // A client that sends requests and never reads the answers would hold this connection for good.
                using var sending = CancellationTokenSource.CreateLinkedTokenSource(stop);
                sending.CancelAfter(TimeSpan.FromSeconds(30));
                await writer.WriteLineAsync(answer.ToJsonString().AsMemory(), sending.Token);
            }
        }
        catch (OperationCanceledException) { } // the window closes, or the client sent nothing in time
        catch (IOException) { } // the client went away
        catch (InvalidDataException) { } // a request longer than MaxRequestChars
    }

    /// <summary>One request line; null at the end. Gives up on a silent client and on a line that never ends.</summary>
    private static async Task<string?> ReadRequestAsync(StreamReader reader, CancellationToken stop)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
        wait.CancelAfter(RequestWait);
        var line = new StringBuilder();
        char[] one = new char[1];
        while (true)
        {
            // Character by character: the reader buffers, and what follows the newline belongs to the next request.
            if (await reader.ReadAsync(one.AsMemory(), wait.Token) == 0) return line.Length == 0 ? null : line.ToString();
            if (one[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= MaxRequestChars) throw new InvalidDataException("The request is too long.");
            line.Append(one[0]);
        }
    }

    private static JsonObject Error(string code, string message) => new()
    {
        ["ok"] = false, ["exit"] = 1,
        ["result"] = CliJson.Error(code, message, null),
    };
}

/// <summary>deot's commands for <see cref="LiveLinkViewModel"/>: the same code the command line runs (<see cref="CliLive"/>).</summary>
public sealed class DesktopLiveCommands : ILiveCommands
{
    public string ScratchFolder => CliLive.ScratchFolder;

    public LiveCommandInfo Inspect(IReadOnlyList<string> tokens, string? stdin)
    {
        if (CliLive.CommandOf(tokens) is not { } name) return new(null, false, LiveAccess.Read, "Unknown command. Run 'deot schema' for the list.");
        if (CliLive.Unsupported.Contains(name)) return new(name, false, LiveAccess.Read, $"'{name}' is not available on a document open in DeotPDF (it reads or writes files of its own).");
        if (CliLive.ShapeError(name, tokens) is { } shape) return new(name, false, LiveAccess.Read, shape);
        JsonArray? plan = null;
        JsonNode? defaults = null;
        if (name == "apply")
        {
            try
            {
                var root = JsonNode.Parse(stdin ?? "");
                plan = root switch
                {
                    JsonObject o => o["ops"] as JsonArray,
                    JsonArray a => a,
                    _ => null,
                };
                defaults = (root as JsonObject)?["defaults"];
            }
            catch (JsonException) { } // apply itself reports the bad plan
            if (CliLive.UnsupportedOps(plan).FirstOrDefault() is { } op)
                return new(name, true, LiveAccess.Read, $"The op '{op}' is not available on a document open in DeotPDF (it reads files of its own).");
        }
        return new(name, CliLive.IsWrite(name), (LiveAccess)(int)CliLive.Needs(name, tokens, plan, defaults), null);
    }

    public LiveCommandResult Run(OpenDocument document, string id, IReadOnlyList<string> tokens, string? stdin)
    {
        var run = CliLive.Run(document, id, tokens, stdin);
        return new(run.ExitCode, run.Json, run.Operations);
    }
}
