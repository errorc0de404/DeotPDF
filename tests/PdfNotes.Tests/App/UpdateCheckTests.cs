using System.IO;

namespace PdfNotes.Tests.App;

/// <summary>Roadmap 1.1: version order, when the start-up check runs, reading GitHub's latest release, and the setting.</summary>
public sealed class UpdateCheckTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("v1.1.0", "1.0.9", 1)]
    [InlineData("1.0.10", "1.0.9", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("1.0.1-dev", "1.0.1", -1)]
    [InlineData("1.0.1-dev", "1.0.0", 1)]
    [InlineData("2.0.0-beta", "2.0.0-alpha", 1)]
    [InlineData("1.0.0+abc123", "1.0.0", 0)]
    public void VersionsAreOrderedPartByPartAndALabelComesFirst(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(UpdateCheck.Compare(a, b)));
        Assert.Equal(-sign, Math.Sign(UpdateCheck.Compare(b, a)));
    }

    [Fact]
    public void TheStartUpCheckRunsOnceADayInReleaseBuildsWhenTurnedOn()
    {
        var now = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdateCheck.Due(true, null, now, "1.1.0"));
        Assert.True(UpdateCheck.Due(true, now.AddDays(-1), now, "1.1.0"));
        Assert.False(UpdateCheck.Due(true, now.AddHours(-23), now, "1.1.0"));
        Assert.True(UpdateCheck.Due(true, now.AddDays(3), now, "1.1.0")); // a clock set back does not stop it for good
        Assert.False(UpdateCheck.Due(false, null, now, "1.1.0"));
        Assert.False(UpdateCheck.Due(null, null, now, "1.1.0")); // not asked yet
        Assert.False(UpdateCheck.Due(true, null, now, "1.1.0-dev"));
        Assert.True(UpdateCheck.IsDevelopment(UpdateCheck.CurrentVersion) || !UpdateCheck.CurrentVersion.Contains('-'));
    }

    [Fact]
    public void ReadsTheLatestReleaseAgainstThisVersion()
    {
        const string release = """
            { "tag_name": "v1.1.0", "html_url": "https://github.com/errorc0de404/DeotPDF/releases/tag/v1.1.0", "draft": false, "prerelease": false,
              "assets": [ { "name": "DeotPDF-1.1.0-win-x64.zip" } ] }
            """;
        Assert.Equal(new UpdateResult(UpdateState.Available, "1.1.0", "https://github.com/errorc0de404/DeotPDF/releases/tag/v1.1.0"), UpdateCheck.Read(release, "1.0.0"));
        Assert.Equal(UpdateState.UpToDate, UpdateCheck.Read(release, "1.1.0").State);
        Assert.Equal(UpdateState.UpToDate, UpdateCheck.Read(release, "1.2.0-dev").State);
        Assert.Equal(UpdateState.Available, UpdateCheck.Read(release, "1.1.0-dev").State);
        // Only GitHub pages are opened; anything else falls back to the releases page.
        Assert.Equal(UpdateCheck.ReleasesPage, UpdateCheck.Read("""{ "tag_name": "v9.0.0", "html_url": "https://example.com/x" }""", "1.0.0").Url);
        Assert.Equal(UpdateState.Failed, UpdateCheck.Read("""{ "message": "Not Found" }""", "1.0.0").State);
        Assert.Equal(UpdateState.Failed, UpdateCheck.Read("""{ "tag_name": "v2.0.0", "prerelease": true }""", "1.0.0").State);
        Assert.Equal(UpdateState.Failed, UpdateCheck.Read("not json", "1.0.0").State);
        Assert.Equal(UpdateState.Failed, UpdateCheck.Read("[]", "1.0.0").State);
    }

    [Fact]
    public void TheAnswerAndTheLastCheckSurviveARestart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(folder, "settings.json");
            Assert.Null(new AppSettings().CheckUpdates); // asked at the first start
            var checkedAt = new DateTime(2026, 10, 27, 3, 4, 5, DateTimeKind.Utc);
            new AppSettings { CheckUpdates = true, UpdateCheckedAt = checkedAt }.Save(path);
            var read = AppSettings.Load(path);
            Assert.True(read.CheckUpdates);
            Assert.Equal(checkedAt, read.UpdateCheckedAt!.Value.ToUniversalTime());
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
