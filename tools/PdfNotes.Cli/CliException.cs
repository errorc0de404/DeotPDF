using System.Text.Json.Nodes;

namespace PdfNotes.Cli;

/// <summary>Exit codes (cli_plan 3.1).</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int InvalidInput = 1;
    public const int Unsatisfiable = 2;
    public const int Locked = 3;
    public const int Internal = 4;
}

/// <summary>An expected failure reported as <c>{ "ok": false, "error": { code, message, details } }</c>.</summary>
public sealed class CliException(string code, string message, int exitCode = ExitCodes.InvalidInput, JsonObject? details = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public int ExitCode { get; } = exitCode;
    public JsonObject? Details { get; } = details;

    public static CliException Invalid(string message, JsonObject? details = null) => new("INVALID_ARGUMENT", message, ExitCodes.InvalidInput, details);
}
