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
                server = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop);
                var connected = server;
                server = null;
                _ = Task.Run(() => ServeAsync(connected, handle, stop), CancellationToken.None);
            }
            catch (OperationCanceledException) { server?.Dispose(); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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
            while (!stop.IsCancellationRequested && await reader.ReadLineAsync(stop) is { } line)
            {
                JsonObject answer;
                try
                {
                    answer = JsonNode.Parse(line) is JsonObject request ? await handle(request)
                        : Error("INVALID_ARGUMENT", "A request is one JSON object per line.");
                }
                catch (JsonException e) { answer = Error("INVALID_ARGUMENT", $"The request is not JSON: {e.Message}"); }
                await writer.WriteLineAsync(answer.ToJsonString());
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // the client went away
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
    public LiveCommandInfo Inspect(IReadOnlyList<string> tokens, string? stdin)
    {
        if (CliLive.CommandOf(tokens) is not { } name) return new(null, false, LiveAccess.Read, "Unknown command. Run 'deot schema' for the list.");
        if (CliLive.Unsupported.Contains(name)) return new(name, false, LiveAccess.Read, $"'{name}' is not available on a document open in DeotPDF (it reads or writes files of its own).");
        bool takesInput = name is not ("schema" or "fonts");
        if (takesInput && !tokens.Contains(CliLive.Input)) return new(name, false, LiveAccess.Read, $"A live command takes {CliLive.Input} as its input PDF.");
        JsonArray? plan = null;
        if (name == "apply")
        {
            try
            {
                plan = JsonNode.Parse(stdin ?? "") switch
                {
                    JsonObject o => o["ops"] as JsonArray,
                    JsonArray a => a,
                    _ => null,
                };
            }
            catch (JsonException) { } // apply itself reports the bad plan
            if (CliLive.UnsupportedOps(plan).FirstOrDefault() is { } op)
                return new(name, true, LiveAccess.Read, $"The op '{op}' is not available on a document open in DeotPDF (it reads files of its own).");
        }
        return new(name, CliLive.IsWrite(name), (LiveAccess)(int)CliLive.Needs(name, tokens, plan), null);
    }

    public LiveCommandResult Run(OpenDocument document, string id, IReadOnlyList<string> tokens, string? stdin)
    {
        var run = CliLive.Run(document, id, tokens, stdin);
        return new(run.ExitCode, run.Json, run.Operations);
    }
}
