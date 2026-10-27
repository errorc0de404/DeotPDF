using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace PdfNotes.Editor.Services;

/// <summary>What an update check found.</summary>
public enum UpdateState { UpToDate, Available, Failed }

/// <param name="Version">The latest release's version (without the leading <c>v</c>) when one was read.</param>
/// <param name="Url">Its release page.</param>
public sealed record UpdateResult(UpdateState State, string? Version = null, string? Url = null);

/// <summary>
/// Checking for a new version (roadmap 1.1): the latest GitHub release of DeotPDF against this build's version. It only
/// tells; downloading and installing stay with the user. A check that fails (offline, rate limit, no release) is
/// <see cref="UpdateState.Failed"/> and never an error. Development builds (a version with <c>-</c>, e.g. <c>1.0.1-dev</c>)
/// do not check by themselves.
/// </summary>
public static class UpdateCheck
{
    public const string ReleasesPage = "https://github.com/errorc0de404/DeotPDF/releases/latest";
    public const string ProjectPage = "https://github.com/errorc0de404/DeotPDF";
    public const string NoticesPage = "https://github.com/errorc0de404/DeotPDF/blob/main/THIRD-PARTY-NOTICES.md";
    private static readonly Uri LatestRelease = new("https://api.github.com/repos/errorc0de404/DeotPDF/releases/latest");

    /// <summary>How often the start-up check runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>This build's version as people see it (<c>1.0.0</c>, <c>1.0.1-dev</c>), without the source revision.</summary>
    public static string CurrentVersion { get; } =
        (typeof(UpdateCheck).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"DeotPDF/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    });

    public static bool IsDevelopment(string version) => version.Contains('-');

    /// <summary>The start-up check is on, this is a release build and the last check is <see cref="Interval"/> old.</summary>
    public static bool Due(bool? enabled, DateTime? lastUtc, DateTime nowUtc, string version) =>
        enabled == true && !IsDevelopment(version) && (lastUtc is not { } last || nowUtc - last >= Interval || last > nowUtc);

    /// <summary>
    /// Orders versions <c>major.minor.patch[-label]</c> (a leading <c>v</c> is ignored): numbers part by part, and a
    /// version with a label comes before the same version without one.
    /// </summary>
    public static int Compare(string a, string b)
    {
        var (numbersA, labelA) = Split(a);
        var (numbersB, labelB) = Split(b);
        for (int i = 0; i < Math.Max(numbersA.Length, numbersB.Length); i++)
        {
            int x = i < numbersA.Length ? numbersA[i] : 0, y = i < numbersB.Length ? numbersB[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return (labelA, labelB) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(labelA, labelB),
        };
    }

    private static (int[] Numbers, string? Label) Split(string version)
    {
        string text = version.Trim().TrimStart('v', 'V').Split('+')[0];
        int dash = text.IndexOf('-');
        string? label = dash >= 0 ? text[(dash + 1)..] : null;
        if (dash >= 0) text = text[..dash];
        var numbers = text.Split('.').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0).ToArray();
        return (numbers, label);
    }

    /// <summary>The result for the GitHub "latest release" response <paramref name="json"/> against <paramref name="current"/>.</summary>
    public static UpdateResult Read(string json, string current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
                Flag(root, "draft") || Flag(root, "prerelease"))
                return new(UpdateState.Failed);
            string version = tag.GetString()!.Trim().TrimStart('v', 'V');
            string url = root.TryGetProperty("html_url", out var page) && page.ValueKind == JsonValueKind.String &&
                page.GetString() is { } link && link.StartsWith("https://github.com/", StringComparison.Ordinal) ? link : ReleasesPage;
            return new(Compare(version, current) > 0 ? UpdateState.Available : UpdateState.UpToDate, version, url);
        }
        catch (JsonException)
        {
            return new(UpdateState.Failed);
        }
    }

    private static bool Flag(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Asks GitHub for the latest release; any failure is <see cref="UpdateState.Failed"/>.</summary>
    public static async Task<UpdateResult> CheckAsync(string? current = null, CancellationToken cancel = default)
    {
        try
        {
            using var response = await Http.Value.GetAsync(LatestRelease, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(UpdateState.Failed);
            return Read(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false), current ?? CurrentVersion);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new(UpdateState.Failed);
        }
    }
}
