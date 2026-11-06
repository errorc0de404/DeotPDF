using System.Globalization;
using System.Text;
using System.Text.Json;
using PdfNotes.Core.Commands;

namespace PdfNotes.Editor.Localization;

/// <summary>A UI language pack (D14e): <c>{ "meta": { "language", "name", "version" }, "strings": { key: text } }</c>.</summary>
public sealed record LanguagePack(string Language, string Name, int Version, IReadOnlyDictionary<string, string> Values, string? Path)
{
    /// <summary>Reads a pack. Throws <see cref="InvalidDataException"/> for anything that is not a pack.</summary>
    public static LanguagePack Parse(string json, string? path = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("strings", out var strings) || strings.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("A language pack needs \"meta\" and \"strings\" objects.");
            string language = meta.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()!.Trim() : "";
            if (language.Length == 0) throw new InvalidDataException("meta.language is missing.");
            string name = meta.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : language;
            int version = meta.TryGetProperty("version", out var v) && v.TryGetInt32(out int number) ? number : 1;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in strings.EnumerateObject())
                if (item.Value.ValueKind == JsonValueKind.String) values[item.Name] = item.Value.GetString()!;
            return new(language, name, version, values, path);
        }
        catch (JsonException e) { throw new InvalidDataException("The language pack is not valid JSON.", e); }
    }
}

/// <summary>
/// The UI strings (D14e). Text is looked up by flat dotted keys (<c>area.name</c>) with named placeholders
/// (<c>{count}</c>; <c>{{</c> and <c>}}</c> for braces). The built-in Korean pack is the default and the fallback: a key a
/// pack lacks, a pack text whose placeholders differ from Korean or an unreadable pack falls back to Korean and is
/// listed in <see cref="Diagnostics"/>. Stored values (PDF names, schema values, backup file names) never come from here.
/// </summary>
public static class Strings
{
    public const string BuiltInLanguage = Texts.BuiltInLanguage;

    private static readonly Lazy<LanguagePack> BuiltIn = new(() =>
    {
        using var stream = typeof(Strings).Assembly.GetManifestResourceStream("PdfNotes.Editor.Localization.ko.json")
            ?? throw new InvalidOperationException("The built-in Korean pack is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return LanguagePack.Parse(reader.ReadToEnd());
    });

    private static readonly List<string> diagnostics = [];

    /// <summary>The built-in Korean pack.</summary>
    public static LanguagePack Korean => BuiltIn.Value;

    /// <summary>The language in use (<see cref="BuiltInLanguage"/> unless a pack was applied).</summary>
    public static string Language { get; private set; } = BuiltInLanguage;

    /// <summary>Problems found when the last pack was applied or discovered (missing keys, placeholders, unreadable files).</summary>
    public static IReadOnlyList<string> Diagnostics => diagnostics;

    /// <summary>
    /// The texts in use, set at start (<see cref="Apply"/>). The view models and the editor's services are given it; the
    /// desktop views read <see cref="Get"/> and <see cref="Format"/>, which use it.
    /// </summary>
    public static Texts Current => current ?? Texts.Korean;
    private static Texts? current; // null until a pack is applied (Texts.Korean reads this class: no initializer cycle)

    /// <summary>The text of <paramref name="key"/> in <see cref="Current"/> (the key itself when even Korean lacks it).</summary>
    public static string Get(string key) => Current.Get(key);

    /// <summary>The text of <paramref name="key"/> in <see cref="Current"/> with its named placeholders filled.</summary>
    public static string Format(string key, params (string Name, object? Value)[] values) => Current.Format(key, values);

    /// <summary>Fills the named placeholders of <paramref name="text"/>, values formatted for the current culture.</summary>
    internal static string Fill(string text, (string Name, object? Value)[] values)
    {
        var result = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((c == '{' || c == '}') && i + 1 < text.Length && text[i + 1] == c) { result.Append(c); i++; continue; }
            if (c != '{') { result.Append(c); continue; }
            int end = text.IndexOf('}', i + 1);
            if (end < 0) { result.Append(text, i, text.Length - i); break; }
            string name = text[(i + 1)..end];
            int index = Array.FindIndex(values, v => v.Name == name);
            result.Append(index >= 0 ? Convert.ToString(values[index].Value, CultureInfo.CurrentCulture) : text[i..(end + 1)]);
            i = end;
        }
        return result.ToString();
    }

    /// <summary>The placeholder names of a text, or null when its braces are unbalanced.</summary>
    public static IReadOnlySet<string>? Placeholders(string text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((c == '{' || c == '}') && i + 1 < text.Length && text[i + 1] == c) { i++; continue; }
            if (c == '}') return null;
            if (c != '{') continue;
            int end = text.IndexOf('}', i + 1);
            if (end < 0) return null;
            string name = text[(i + 1)..end];
            if (name.Length == 0 || name.Contains('{')) return null;
            names.Add(name);
            i = end;
        }
        return names;
    }

    internal static string Unescape(string text) => text.Contains('{') || text.Contains('}') ? text.Replace("{{", "{").Replace("}}", "}") : text;

    /// <summary>
    /// Uses <paramref name="pack"/> (null: Korean). Its keys are checked against Korean: missing keys, placeholder
    /// mismatches and unknown keys are recorded in <see cref="Diagnostics"/>, and the Korean text is used for the first two.
    /// </summary>
    public static void Use(LanguagePack? pack)
    {
        diagnostics.Clear();
        if (pack is null || pack.Language == BuiltInLanguage && pack.Path is null)
        {
            current = null;
            Language = BuiltInLanguage;
            PageRanges.All = Get("range.all");
            return;
        }
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        string source = pack.Path ?? pack.Language;
        foreach (var (key, korean) in Korean.Values)
        {
            if (!pack.Values.TryGetValue(key, out var text)) { diagnostics.Add($"{source}: missing key {key}"); merged[key] = korean; continue; }
            var expected = Placeholders(korean);
            var actual = Placeholders(text);
            if (actual is null || expected is not null && !actual.SetEquals(expected))
            {
                diagnostics.Add($"{source}: placeholders of {key} differ from Korean");
                merged[key] = korean;
                continue;
            }
            merged[key] = text;
        }
        foreach (var key in pack.Values.Keys.Where(k => !Korean.Values.ContainsKey(k)).Order(StringComparer.Ordinal))
            diagnostics.Add($"{source}: unknown key {key}");
        current = new Texts(merged);
        Language = pack.Language;
        PageRanges.All = Get("range.all");
    }

    /// <summary>
    /// The packs in <paramref name="folders"/> (<c>*.json</c>), Korean first. A later folder wins for the same language
    /// (the app folder first, then the user's folder). Unreadable files are skipped and recorded in <see cref="Diagnostics"/>.
    /// </summary>
    public static IReadOnlyList<LanguagePack> Discover(IEnumerable<string> folders, ICollection<string>? problems = null)
    {
        var found = new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in folders)
        {
            string[] files;
            try { files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problems?.Add($"{folder}: {e.Message}"); continue; }
            foreach (string file in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                try { var pack = LanguagePack.Parse(File.ReadAllText(file, Encoding.UTF8), file); found[pack.Language] = pack; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { problems?.Add($"{file}: {e.Message}"); }
            }
        }
        found.Remove(BuiltInLanguage, out var externalKorean);
        return [externalKorean ?? Korean, .. found.Values.OrderBy(p => p.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>
    /// Applies the pack of <paramref name="language"/> from <paramref name="folders"/> (Korean when it is not found) and
    /// returns the diagnostics of discovery and use together.
    /// </summary>
    public static IReadOnlyList<string> Apply(string? language, IEnumerable<string> folders)
    {
        var problems = new List<string>();
        var packs = Discover(folders, problems);
        var pack = packs.FirstOrDefault(p => string.Equals(p.Language, language ?? BuiltInLanguage, StringComparison.OrdinalIgnoreCase));
        if (pack is null) problems.Add($"language {language} not found; using Korean");
        Use(pack is null || ReferenceEquals(pack, Korean) ? null : pack);
        problems.AddRange(diagnostics);
        diagnostics.Clear();
        diagnostics.AddRange(problems);
        return diagnostics;
    }
}

/// <summary>
/// The texts of one language pack (D14e), given to the view models and the editor's services so they do not read a
/// global. Keys missing from the pack fall back to Korean, then to the key itself.
/// </summary>
public sealed class Texts
{
    public const string BuiltInLanguage = "ko";

    private readonly IReadOnlyDictionary<string, string> values;

    internal Texts(IReadOnlyDictionary<string, string> values) => this.values = values;

    /// <summary>The texts of <paramref name="pack"/> as they are (no checks; <see cref="Strings.Apply"/> checks a pack against Korean).</summary>
    public static Texts From(LanguagePack pack) => new(pack.Values);

    /// <summary>The built-in Korean texts.</summary>
    public static Texts Korean => korean.Value;
    private static readonly Lazy<Texts> korean = new(() => new Texts(Strings.Korean.Values));

    /// <summary>The text of <paramref name="key"/> (the key itself when even Korean lacks it).</summary>
    public string Get(string key) =>
        values.TryGetValue(key, out var text) || Strings.Korean.Values.TryGetValue(key, out text) ? Strings.Unescape(text) : key;

    /// <summary>The text of <paramref name="key"/> with its named placeholders filled, values formatted for the current culture.</summary>
    public string Format(string key, params (string Name, object? Value)[] values) =>
        Strings.Fill(this.values.TryGetValue(key, out var found) || Strings.Korean.Values.TryGetValue(key, out found) ? found : key, values);
}
