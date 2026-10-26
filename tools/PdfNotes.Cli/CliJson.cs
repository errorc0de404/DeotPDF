using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;

namespace PdfNotes.Cli;

/// <summary>JSON shapes shared by all commands (cli_plan 3.2): rectangles, quads, points and matrices in canonical points.</summary>
public static class CliJson
{
    /// <summary>Geometry is reported to 0.001 pt (pixels for image coordinates); matrices keep more digits.</summary>
    public const int Digits = 3;
    public const int MatrixDigits = 8;

    public static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Pretty = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    public static JsonValue Num(double value, int digits = Digits)
    {
        double rounded = Math.Round(value, digits, MidpointRounding.AwayFromZero);
        return JsonValue.Create(rounded == 0 ? 0 : rounded); // no "-0"
    }

    public static JsonArray Rect(Rect2 r) => [Num(r.X), Num(r.Y), Num(r.Width), Num(r.Height)];

    public static JsonArray Rect(double x, double y, double w, double h) => [Num(x), Num(y), Num(w), Num(h)];

    public static JsonArray Point(Point2 p) => [Num(p.X), Num(p.Y)];

    /// <summary>Upper-left, upper-right, lower-left, lower-right (relative to the text direction).</summary>
    public static JsonArray Quad(Quad q) => [Point(q.UpperLeft), Point(q.UpperRight), Point(q.LowerLeft), Point(q.LowerRight)];

    public static JsonArray Quads(IEnumerable<Quad> quads) => new(quads.Select(q => (JsonNode)Quad(q)).ToArray());

    /// <summary><c>[a, b, c, d, e, f]</c>: x' = a·x + c·y + e, y' = b·x + d·y + f.</summary>
    public static JsonArray Matrix(Affine m) => new(m.ToArray().Select(v => (JsonNode)Num(v, MatrixDigits)).ToArray());

    public static JsonArray Array(IEnumerable<JsonNode?> items) => new(items.ToArray());

    /// <summary>
    /// cli_plan 3.2 <c>pageTransform</c>: page frame before and after an edit and the canonical-to-canonical matrix with
    /// its inverse.
    /// </summary>
    public static JsonObject PageTransform(Guid pageId, PdfNotes.Core.Models.PageSetup before, PdfNotes.Core.Models.PageSetup after, Affine matrix) => new()
    {
        ["pageId"] = pageId.ToString(),
        ["before"] = new JsonObject { ["width"] = Num(before.WidthPt), ["height"] = Num(before.HeightPt), ["rotation"] = before.Rotation },
        ["after"] = new JsonObject { ["width"] = Num(after.WidthPt), ["height"] = Num(after.HeightPt), ["rotation"] = after.Rotation },
        ["matrix"] = Matrix(matrix),
        ["inverse"] = Matrix(matrix.Invert()),
    };

    /// <summary>The successful result of a command: <c>{ "ok": true, "command": …, … }</c>.</summary>
    public static JsonObject Result(string command, string? sourceHash = null)
    {
        var result = new JsonObject { ["ok"] = true, ["command"] = command };
        if (sourceHash is not null) result["sourceHash"] = sourceHash;
        return result;
    }

    public static JsonObject Error(string code, string message, JsonObject? details) => new()
    {
        ["ok"] = false,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["details"] = details ?? new JsonObject() }
    };
}
