using System.Globalization;
using System.Text.Json.Nodes;

namespace PdfNotes.Cli;

/// <summary>
/// Parsed command-line arguments of one command: positional values and <c>--name value</c> / <c>--flag</c> options.
/// Only the options a command declares are accepted, so a typo fails instead of being ignored.
/// </summary>
public sealed class CliArgs
{
    private static readonly Dictionary<string, string> Aliases = new() { ["-p"] = "--page", ["-o"] = "--out" };

    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> flags = new(StringComparer.Ordinal);

    public List<string> Positionals { get; } = [];

    /// <param name="valueOptions">Options that take a value (with leading dashes).</param>
    /// <param name="flagOptions">Options without a value.</param>
    public static CliArgs Parse(IReadOnlyList<string> tokens, IEnumerable<string> valueOptions, IEnumerable<string> flagOptions)
    {
        var takesValue = valueOptions.ToHashSet(StringComparer.Ordinal);
        var isFlag = flagOptions.Append("--pretty").ToHashSet(StringComparer.Ordinal);
        var result = new CliArgs();
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (token.Length > 1 && token[0] == '-' && !IsNumber(token))
            {
                string name = Aliases.GetValueOrDefault(token, token);
                if (takesValue.Contains(name))
                {
                    if (i + 1 >= tokens.Count) throw CliException.Invalid($"Option {token} needs a value.");
                    if (!result.values.TryAdd(name, tokens[++i])) throw CliException.Invalid($"Option {token} is given twice.");
                }
                else if (isFlag.Contains(name)) result.flags.Add(name);
                else throw CliException.Invalid($"Unknown option {token}.", new JsonObject { ["option"] = token });
            }
            else result.Positionals.Add(token);
        }
        return result;
    }

    private static bool IsNumber(string token) => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    public bool Has(string name) => flags.Contains(name) || values.ContainsKey(name);

    public bool Flag(string name) => flags.Contains(name);

    public string? Value(string name) => values.GetValueOrDefault(name);

    public string Required(string name) => Value(name) ?? throw CliException.Invalid($"Option {name} is required.");

    public double Number(string name, double fallback, double min, double max)
    {
        if (Value(name) is not { } text) return fallback;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < min || value > max)
            throw CliException.Invalid($"Option {name} must be a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.");
        return value;
    }

    public int Integer(string name, int fallback, int min, int max)
    {
        if (Value(name) is not { } text) return fallback;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
            throw CliException.Invalid($"Option {name} must be an integer from {min} to {max}.");
        return value;
    }

    /// <summary>The single input PDF (first positional); no other positional values are allowed.</summary>
    public string InputPath(int extraPositionals = 0)
    {
        if (Positionals.Count == 0) throw CliException.Invalid("The input PDF is missing.");
        if (Positionals.Count > 1 + extraPositionals) throw CliException.Invalid($"Unexpected argument {Positionals[1 + extraPositionals]}.");
        return Positionals[0];
    }

    /// <summary><c>x,y,w,h</c> with a positive width and height.</summary>
    public static PdfNotes.Core.Geometry.Rect2 ParseRect(string text, string option)
    {
        var n = ParseNumbers(text, 4, option);
        if (n[2] <= 0 || n[3] <= 0) throw CliException.Invalid($"Option {option} needs a positive width and height.");
        return new(n[0], n[1], n[2], n[3]);
    }

    /// <summary>Numbers separated by commas, e.g. <c>x,y,w,h</c>.</summary>
    public static double[] ParseNumbers(string text, int count, string option)
    {
        var parts = text.Split(',');
        var numbers = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) || !double.IsFinite(numbers[i]))
                throw CliException.Invalid($"Option {option} has an invalid number '{parts[i]}'.");
        if (numbers.Length != count) throw CliException.Invalid($"Option {option} needs {count} numbers separated by commas.");
        return numbers;
    }
}
