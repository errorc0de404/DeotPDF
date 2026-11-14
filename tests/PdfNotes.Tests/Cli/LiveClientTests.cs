using System.IO;
using System.Text.Json.Nodes;
using PdfNotes.Cli;

namespace PdfNotes.Tests.Cli;

/// <summary><c>deot … --live</c> before it reaches a window: whatever goes wrong, the caller gets JSON and an exit code.</summary>
public sealed class LiveClientTests
{
    [Fact]
    public void AMissingPlanFileIsAnErrorInJson()
    {
        string missing = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"), "plan.json");
        var stdout = new StringWriter();
        int exit = LiveClient.Run(["apply", "--live", missing], stdout, "test");
        var json = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal(ExitCodes.InvalidInput, exit);
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Equal("FILE_NOT_FOUND", json["error"]!["code"]!.GetValue<string>());
    }
}
