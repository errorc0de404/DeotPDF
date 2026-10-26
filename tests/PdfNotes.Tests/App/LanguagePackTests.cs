using System.IO;
using System.Text.RegularExpressions;
using PdfNotes.Core.Commands;
using PdfNotes.Editor.Localization;
using Xunit.Abstractions;

namespace PdfNotes.Tests.App;

[CollectionDefinition("Language", DisableParallelization = true)]
public sealed class LanguageCollection;

/// <summary>D14e: UI strings come from JSON language packs; Korean is built in and the fallback.</summary>
[Collection("Language")]
public sealed partial class LanguagePackTests(ITestOutputHelper output) : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Strings.Use(null);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    private static string Pack(string language, string name, params (string Key, string Text)[] strings) =>
        System.Text.Json.JsonSerializer.Serialize(new { meta = new { language, name, version = 1 }, strings = strings.ToDictionary(s => s.Key, s => s.Text) });

    private string Write(string sub, string file, string json)
    {
        string dir = Path.Combine(folder, sub);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), json);
        return dir;
    }

    [Fact]
    public void TheKoreanPackIsBuiltInWithValidPlaceholders()
    {
        Assert.Equal("ko", Strings.Korean.Language);
        Assert.True(Strings.Korean.Values.Count > 400);
        Assert.Equal("저장됨", Strings.Get("status.saved"));
        Assert.All(Strings.Korean.Values, v => Assert.NotNull(Strings.Placeholders(v.Value)));
        Assert.All(Strings.Korean.Values.Keys, k => Assert.Matches(@"^[a-z][A-Za-z0-9]*(\.[a-z][A-Za-z0-9]*)+$", k));
    }

    [Fact]
    public void NamedPlaceholdersAndBraces()
    {
        Assert.Equal("페이지 3개를 삭제할까요?", Strings.Format("page.deleteConfirm", ("count", 3)));
        Strings.Use(LanguagePack.Parse(Pack("xx", "Test", ("page.deleteConfirm", "{{x}} delete {count} pages?"))));
        Assert.Equal("{x} delete 3 pages?", Strings.Format("page.deleteConfirm", ("count", 3)));
        Assert.Equal("missing.key", Strings.Get("missing.key"));
    }

    [Fact]
    public void MissingKeysAndChangedPlaceholdersFallBackToKorean()
    {
        Strings.Use(LanguagePack.Parse(Pack("xx", "Test", ("status.saved", "Saved"), ("page.deleteConfirm", "Delete {number} pages?"), ("no.such", "x")), "test.json"));
        Assert.Equal("xx", Strings.Language);
        Assert.Equal("Saved", Strings.Get("status.saved"));
        Assert.Equal("페이지 2개를 삭제할까요?", Strings.Format("page.deleteConfirm", ("count", 2))); // {number} is not {count}
        Assert.Equal("저장 중…", Strings.Get("status.saving")); // missing
        Assert.Contains(Strings.Diagnostics, d => d.Contains("placeholders of page.deleteConfirm", StringComparison.Ordinal));
        Assert.Contains(Strings.Diagnostics, d => d.Contains("missing key status.saving", StringComparison.Ordinal));
        Assert.Contains(Strings.Diagnostics, d => d.Contains("unknown key no.such", StringComparison.Ordinal));
        Assert.Equal(PageRanges.All, Strings.Get("range.all"));
    }

    [Fact]
    public void BrokenPacksAreSkippedAndTheUserFolderWins()
    {
        string app = Write("app", "en.json", Pack("en", "English (app)", ("status.saved", "Saved (app)")));
        Write("app", "broken.json", "{ \"meta\": { \"language\": \"de\" }, \"strings\": { ");
        Write("app", "notapack.json", "[1, 2]");
        string user = Write("user", "en.json", Pack("en", "English (user)", ("status.saved", "Saved (user)")));
        var problems = new List<string>();
        var packs = Strings.Discover([app, user, Path.Combine(folder, "absent")], problems);
        Assert.Equal(["ko", "en"], packs.Select(p => p.Language));
        Assert.Equal("English (user)", packs[1].Name);
        Assert.Equal(2, problems.Count);

        var diagnostics = Strings.Apply("en", [app, user]);
        Assert.Equal("Saved (user)", Strings.Get("status.saved"));
        Assert.Contains(diagnostics, d => d.Contains("broken.json", StringComparison.Ordinal));
        Strings.Apply("fr", [app, user]);
        Assert.Equal("ko", Strings.Language); // an unknown language uses Korean
        Assert.Equal("저장됨", Strings.Get("status.saved"));
        Assert.Throws<InvalidDataException>(() => LanguagePack.Parse("{}"));
    }

    [Fact]
    public async Task SavedFilesDoNotDependOnTheLanguage()
    {
        Directory.CreateDirectory(folder);
        using var doc = OpenDocument.CreateNote(PdfNotes.Core.Models.NewPageSpec.Default);
        var page = doc.Session.Current.Pages[0].Id;
        doc.Session.Execute(DocumentEdits.AddAnnotation(doc.Session.Current, page, PdfNotes.Tests.Core.TestModels.Ink()));
        var resource = PdfNotes.Tests.Core.TestModels.PngResource();
        doc.Session.Execute(DocumentEdits.AddAnnotation(doc.Session.Current, page, PdfNotes.Tests.Core.TestModels.Image(resource), resource));
        string korean = Path.Combine(folder, "ko.pdf"), other = Path.Combine(folder, "xx.pdf");
        await doc.WriteCopyAsync(korean);
        Strings.Use(LanguagePack.Parse(Pack("xx", "Test", [.. Strings.Korean.Values.Select(v => (v.Key, "X " + v.Value))])));
        Assert.Equal("X 저장됨", Strings.Get("status.saved"));
        await doc.WriteCopyAsync(other);
        // Every save stamps its time and fresh document IDs; everything else must be the same bytes.
        Assert.Equal(WithoutStamps(korean), WithoutStamps(other));
    }

    [GeneratedRegex(@"\(D:\d{14}[^)]*\)|\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:Z|[+-]\d\d:\d\d)|uuid:[0-9a-f-]{36}|/ID\[<[0-9A-F]+><[0-9A-F]+>\]")] private static partial Regex Stamp();

    private static string WithoutStamps(string pdf) => Stamp().Replace(System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf)), "#");

    [Fact]
    public void TheShippedEnglishPackMatchesTheKoreanKeysAndPlaceholders()
    {
        string file = Path.Combine(Path.GetDirectoryName(SourceRoot())!, "src", "PdfNotes.App", "Languages", "en.json");
        Strings.Use(LanguagePack.Parse(File.ReadAllText(file), file));
        Assert.Equal("en", Strings.Language);
        Assert.Equal("Saved", Strings.Get("status.saved"));
        Assert.DoesNotContain(Strings.Diagnostics, d => d.Contains("placeholders", StringComparison.Ordinal) || d.Contains("unknown key", StringComparison.Ordinal));
        foreach (var d in Strings.Diagnostics) output.WriteLine("warning: " + d); // a new Korean key not translated yet shows in Korean
    }

    // ----- build checks: no Korean UI literals in the source, every key used exists -----

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PdfNotes.slnx"))) return Path.Combine(dir.FullName, "src");
        throw new DirectoryNotFoundException("PdfNotes.slnx");
    }

    private static IEnumerable<(string File, int Line, string Text)> SourceLines()
    {
        string root = SourceRoot();
        foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file);
            if (!(rel.EndsWith(".cs", StringComparison.Ordinal) || rel.EndsWith(".xaml", StringComparison.Ordinal))) continue;
            if (rel.Split(Path.DirectorySeparatorChar) is var parts && (parts.Contains("obj") || parts.Contains("bin") || parts.Contains("Prototype"))) continue;
            int n = 0;
            foreach (string line in File.ReadLines(file)) yield return (rel, ++n, line);
        }
    }

    [GeneratedRegex(@"\$?@?""(?:[^""\\\n]|\\.)*""")] private static partial Regex CsLiteral();
    [GeneratedRegex(@"\w[\w.:]*=""([^""]*)""")] private static partial Regex XamlAttribute();
    [GeneratedRegex(@"[가-힣]")] private static partial Regex Hangul();

    [Fact]
    public void TheSourceHasNoKoreanUiLiterals()
    {
        var found = new List<string>();
        foreach (var (file, line, text) in SourceLines())
        {
            // A stored value or sample, or the Korean default the pack replaces, marked where it is.
            if (text.Contains("not UI text (D14e)", StringComparison.Ordinal) || text.Contains("the language pack sets it (D14e)", StringComparison.Ordinal)) continue;
            if (file.EndsWith(".cs", StringComparison.Ordinal))
            {
                string code = text.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : text.Split(" // ")[0];
                found.AddRange(CsLiteral().Matches(code).Where(m => Hangul().IsMatch(m.Value)).Select(m => $"{file}:{line} {m.Value}"));
            }
            else if (!text.Contains("<!--", StringComparison.Ordinal))
                found.AddRange(XamlAttribute().Matches(text).Where(m => Hangul().IsMatch(m.Groups[1].Value)).Select(m => $"{file}:{line} {m.Value}"));
        }
        Assert.True(found.Count == 0, string.Join("\n", found));
    }

    [GeneratedRegex(@"Strings\.(?:Get|Format)\(""([^""]+)""|\{l:T ([^}\s]+)\}|""((?:undo|newPage|pageTransform)\.[A-Za-z0-9.]+)""")] private static partial Regex KeyUse();

    [Fact]
    public void EveryKeyTheCodeUsesIsInTheKoreanPack()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var (file, line, text) in SourceLines())
            foreach (Match m in KeyUse().Matches(text))
            {
                string key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                used.Add(key);
                if (!Strings.Korean.Values.ContainsKey(key)) missing.Add($"{file}:{line} {key}");
            }
        Assert.True(missing.Count == 0, string.Join("\n", missing));
        // Keys looked up through a variable (save/open failure tables, range.all) are not visible here: unused keys only warn.
        foreach (var key in Strings.Korean.Values.Keys.Where(k => !used.Contains(k)).Order(StringComparer.Ordinal))
            output.WriteLine($"warning: unused key {key}");
    }
}
