using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;
using PdfNotes.Desktop.Platform;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Prototype;
using PdfNotes.ViewModels;

namespace PdfNotes.Desktop.Tests;

/// <summary>
/// The live link (MCP_LIVE_PLAN): an AI client's requests through <see cref="LiveLinkViewModel"/> as the window's pipe
/// hands them over — consent with the bar, reads of the open document, one Undo unit per write, permission steps, and
/// the user's edit making AI writing theirs.
/// </summary>
public sealed class LiveLinkTests
{
    private static MainWindow Window(bool enabled = true)
    {
        var app = TestWindows.App(new AppSettings { LiveLink = enabled }, Headless.TempFolder()) with { LiveCommands = new DesktopLiveCommands() };
        var window = new MainWindow(app, TestWindows.Dialogs) { Width = 1200, Height = 1000 };
        window.Script.PromptOverride = (_, _) => Answer.Yes;
        window.Script.MessageSink = _ => { };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static async Task<MainWindow> Opened(bool enabled = true, int pages = 3)
    {
        string pdf = Path.Combine(Headless.TempFolder(), "live.pdf");
        ProbeSamplePdf.Create(pdf, pages);
        var w = Window(enabled);
        Assert.True(await w.View.OpenFileAsync(pdf));
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    private static Button Find(MainWindow w, string name) =>
        w.GetLogicalDescendants().OfType<Button>().FirstOrDefault(c => c.Name == name) ?? w.View.FindControl<Button>(name) ?? throw new InvalidOperationException(name);

    private static void Press(Button button)
    {
        if (button.Command is { } command && command.CanExecute(button.CommandParameter)) command.Execute(button.CommandParameter);
    }

    private static JsonObject Request(string op, params string[] args) => new()
    {
        ["op"] = op, ["client"] = "Claude", ["args"] = new JsonArray([.. args.Select(a => (JsonNode)a)]),
    };

    /// <summary>Sends a request and answers the bar with <paramref name="button"/> once it asks.</summary>
    private static async Task<JsonObject> Ask(MainWindow w, JsonObject request, string? button = null)
    {
        var answer = w.View.ViewModel.LiveLink.HandleAsync(request);
        if (button is not null)
        {
            Headless.Pump(() => w.View.ViewModel.LiveLink.IsAsking);
            Press(Find(w, button));
        }
        Headless.Pump(() => answer.IsCompleted, 20_000);
        return await answer;
    }

    private static string Code(JsonObject answer) => answer["result"]!["error"]!["code"]!.GetValue<string>();

    [Fact]
    public void ReadsAfterConsentAndAsksOncePerDocument() => Headless.Run(async () =>
    {
        var w = await Opened();
        var live = w.View.ViewModel.LiveLink;
        var info = await Ask(w, Request("command", "info", "@live"), "LiveReadButton");
        Assert.True(info["ok"]!.GetValue<bool>(), info.ToJsonString());
        Assert.Equal(3, info["result"]!["pageCount"]!.GetValue<int>());
        Assert.True(live.IsConnected);
        Assert.Contains("Claude", live.IndicatorText);

        // The same client is not asked again for this document.
        var context = await Ask(w, Request("context"));
        Assert.True(context["ok"]!.GetValue<bool>(), context.ToJsonString());
        Assert.Equal(1, context["result"]!["currentPage"]!["page"]!.GetValue<int>());
        Assert.NotEmpty(context["result"]!["visible"]!.AsArray());
        Assert.False(live.IsAsking);

        // Read access does not write. The client is asked about once (MCP_LIVE_PLAN 15 P1): 거절 keeps read access,
        // and the next write is refused without a question.
        JsonObject Write() => new()
        {
            ["op"] = "command", ["client"] = "Claude", ["args"] = new JsonArray("apply", "@live", "-"),
            ["stdin"] = """[{ "op": "add.text", "page": 1, "rect": [50, 50, 120, 30], "text": "hi" }]""",
        };
        var asking = live.HandleAsync(Write());
        Headless.Pump(() => live.IsAsking);
        var texts = w.View.ViewModel.Texts;
        Assert.Equal(texts.Format("live.askMore", ("client", "Claude"), ("access", texts.Get("live.accessAiWrite")), ("current", texts.Get("live.accessRead"))), live.AskText);
        Press(Find(w, "LiveDenyButton"));
        Headless.Pump(() => asking.IsCompleted, 20_000);
        Assert.Equal("LIVE_PERMISSION", Code(await asking));
        Assert.True(live.HasRead);
        Assert.Equal("LIVE_PERMISSION", Code(await Ask(w, Write())));
        Assert.False(live.IsAsking);
        Assert.True((await Ask(w, Request("context")))["ok"]!.GetValue<bool>()); // reading goes on

        // Another file is another document: the client is asked again.
        string other = Path.Combine(Headless.TempFolder(), "other.pdf");
        ProbeSamplePdf.Create(other, 2);
        Assert.True(await w.View.OpenFileAsync(other));
        Dispatcher.UIThread.RunJobs();
        Assert.False(live.IsConnected);
        var again = await Ask(w, Request("context"), "LiveDenyButton");
        Assert.Equal("LIVE_CONSENT_DENIED", Code(again));
    });

    [Fact]
    public void AWriteIsOneUndoUnitAndStaysAiWritingUntilTheUserTakesIt() => Headless.Run(async () =>
    {
        var w = await Opened();
        var session = w.View.Document!.Session;
        int undo = session.UndoCount;
        var answer = await Ask(w, new JsonObject
        {
            ["op"] = "command", ["client"] = "Claude", ["args"] = new JsonArray("apply", "@live", "-"),
            ["stdin"] = """{ "ops": [{ "op": "add.text", "page": 1, "rect": [50, 50, 120, 30], "text": "hi" }, { "op": "add.text", "page": 2, "rect": [60, 60, 120, 30], "text": "there" }] }""",
        }, "LiveAiWriteButton");
        Assert.True(answer["ok"]!.GetValue<bool>(), answer.ToJsonString());
        Assert.Equal(undo + 1, session.UndoCount);
        Assert.Equal(Authorship.AiLabel, session.UndoLabel);
        var model = session.Current;
        var box = Assert.Single(model.Pages[0].Annotations.OfType<TextBoxAnnotation>());
        Assert.Equal(AnnotationTag.AiAuthor, model.TagOf(box.Id)!.Author);
        Assert.Single(model.Pages[1].Annotations.OfType<TextBoxAnnotation>());

        // The user moves it: still AI writing (MCP_LIVE_PLAN 5). Only 내 필기로 (the canvas menu) makes it theirs; Undo gives the tag back.
        session.Execute(DocumentEdits.UpdateAnnotations(session.Current, [PdfNotes.Core.Geometry.AnnotationGeometry.Similarity(box, default, move: new PdfNotes.Core.Geometry.Point2(10, 0))], "undo.move"));
        Assert.True(Authorship.IsAi(session.Current, box.Id));
        var selection = w.View.ViewModel.Selection;
        var menu = selection.Menu(0, box.Id, onTextSelection: false)!;
        var adopt = Assert.Single(menu.Entries, e => e.Command == AnnotationCommand.Adopt);
        Assert.True(selection.Run(menu, adopt));
        Assert.Null(session.Current.TagOf(box.Id));
        Assert.DoesNotContain(selection.Menu(0, box.Id, onTextSelection: false)!.Entries, e => e.Command == AnnotationCommand.Adopt);
        session.Undo();
        Assert.True(Authorship.IsAi(session.Current, box.Id));
        session.Undo(); // the move

        // Ctrl+Z on the AI edit removes both boxes at once.
        session.Undo();
        Assert.Empty(session.Current.Pages[0].Annotations.OfType<TextBoxAnnotation>());
        Assert.Empty(session.Current.Pages[1].Annotations.OfType<TextBoxAnnotation>());

        // Page structure needs full editing.
        var delete = await Ask(w, Request("command", "page", "delete", "@live", "-p", "3"), "LiveDenyButton");
        Assert.Equal("LIVE_PERMISSION", Code(delete));
        Assert.True(w.View.ViewModel.LiveLink.HasAiWrite);
        // Files of the window's own are not read for a client.
        var image = await Ask(w, Request("command", "add", "image", "@live", "-p", "1", "--file", "x.png", "--rect", "0,0,10,10"));
        Assert.Equal("LIVE_UNSUPPORTED", Code(image));
    });

    /// <summary>A client the user gave 읽기만 asks once when it wants to write; the answer is its new access (MCP_LIVE_PLAN 15 P1).</summary>
    [Fact]
    public void AClientWithReadAccessCanBeGivenMoreWhenItAsks() => Headless.Run(async () =>
    {
        var w = await Opened();
        var live = w.View.ViewModel.LiveLink;
        Assert.True((await Ask(w, Request("context"), "LiveReadButton"))["ok"]!.GetValue<bool>());
        var write = await Ask(w, new JsonObject
        {
            ["op"] = "command", ["client"] = "Claude", ["args"] = new JsonArray("apply", "@live", "-"),
            ["stdin"] = """[{ "op": "add.text", "page": 1, "rect": [50, 50, 120, 30], "text": "hi" }]""",
        }, "LiveAiWriteButton");
        Assert.True(write["ok"]!.GetValue<bool>(), write.ToJsonString());
        Assert.True(live.HasAiWrite);
        Assert.Single(w.View.Document!.Session.Current.Pages[0].Annotations.OfType<TextBoxAnnotation>());
    });

    /// <summary>MCP_LIVE_PLAN 15 M1: an explanation as a note — AI writing like any other, replaced by its source key, one Undo.</summary>
    [Fact]
    public void AnExplanationGoesInANoteThatIsAiWritingAndIsReplacedByItsKey() => Headless.Run(async () =>
    {
        var w = await Opened();
        var session = w.View.Document!.Session;
        string longText = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"{i}. " + new string('가', 60)));
        JsonObject Note(string text) => new()
        {
            ["op"] = "command", ["client"] = "Claude", ["args"] = new JsonArray("apply", "@live", "-"),
            ["stdin"] = new JsonObject { ["ops"] = new JsonArray(new JsonObject { ["op"] = "add.note", ["page"] = 1, ["at"] = new JsonArray(560, 120), ["text"] = text, ["sourceKey"] = "q3/why" }) }.ToJsonString(),
        };
        var first = await Ask(w, Note(longText), "LiveAiWriteButton");
        Assert.True(first["ok"]!.GetValue<bool>(), first.ToJsonString());
        var note = Assert.Single(session.Current.Pages[0].Annotations.OfType<StickyNote>());
        Assert.Equal(longText, note.Text); // every line, however long
        Assert.True(Authorship.IsAi(session.Current, note.Id));

        var again = await Ask(w, Note("shorter"));
        Assert.True(again["ok"]!.GetValue<bool>(), again.ToJsonString());
        var replaced = Assert.Single(session.Current.Pages[0].Annotations.OfType<StickyNote>());
        Assert.Equal(note.Id, replaced.Id);
        Assert.Equal("shorter", replaced.Text);

        // A page of text the client reads by lines; the user's own writing with the line it is on.
        var listed = await Ask(w, Request("command", "annot", "list", "@live", "--tag", "untagged", "--near-text"));
        Assert.Equal(0, listed["result"]!["count"]!.GetValue<int>());
        session.Undo();
        session.Undo();
        Assert.Empty(session.Current.Pages[0].Annotations.OfType<StickyNote>());
    });

    /// <summary>What a client must not get around (review 2026-11-13): the plan's defaults, a plan or text file, a dry run.</summary>
    [Fact]
    public void AClientGetsNoMoreThanItWasGiven() => Headless.Run(async () =>
    {
        var w = await Opened();
        var session = w.View.Document!.Session;
        JsonObject Apply(string plan, params string[] more) => new()
        {
            ["op"] = "command", ["client"] = "Claude", ["args"] = new JsonArray([.. ((string[])["apply", "@live", "-", .. more]).Select(a => (JsonNode)a)]), ["stdin"] = plan,
        };
        var first = await Ask(w, Apply("""[{ "op": "add.text", "page": 1, "rect": [50, 50, 120, 30], "text": "hi" }]"""), "LiveAiWriteButton");
        Assert.True(first["ok"]!.GetValue<bool>(), first.ToJsonString());
        string hash = first["result"]!["sourceHash"]!.GetValue<string>();

        // allowUntagged in the plan's defaults is the user's writing too: full editing.
        var mine = new StickyNote(Guid.NewGuid(), false, PdfNotes.Core.Geometry.Affine.Translation(10, 10), "mine", new RgbColor(255, 213, 79));
        session.Execute(DocumentEdits.AddAnnotation(session.Current, session.Current.Pages[0].Id, mine));
        string steal = $$"""{ "defaults": { "annot.delete": { "allowUntagged": true } }, "ops": [{ "op": "annot.delete", "id": "{{mine.Id}}" }] }""";
        Assert.Equal("LIVE_PERMISSION", Code(await Ask(w, Apply(steal), "LiveDenyButton")));
        Assert.NotNull(session.Current.FindAnnotation(mine.Id));

        // The plan comes on standard input, never from a file the window would open; the input is exactly @live.
        string file = Path.Combine(Headless.TempFolder(), "plan.json");
        File.WriteAllText(file, """[{ "op": "page.delete", "page": 3 }]""");
        Assert.Equal("LIVE_UNSUPPORTED", Code(await Ask(w, Request("command", "apply", "@live", file))));
        Assert.Equal("LIVE_UNSUPPORTED", Code(await Ask(w, Request("command", "info", file, "--note", "@live"))));
        Assert.Equal(3, session.Current.Pages.Count);

        // The window reads no file for a client (text from a file).
        string secret = Path.Combine(Headless.TempFolder(), "secret.txt");
        File.WriteAllText(secret, "secret");
        var text = await Ask(w, Request("command", "add", "text", "@live", "-p", "1", "--rect", "10,10,100,20", "--text", "@" + secret));
        Assert.Equal("PATH_NOT_ALLOWED", Code(text));

        // A dry run reports and changes nothing; the hash follows the window's document.
        int undo = session.UndoCount;
        var dry = await Ask(w, Apply("""[{ "op": "add.text", "page": 2, "rect": [50, 50, 120, 30], "text": "dry" }]""", "--dry-run"));
        Assert.True(dry["ok"]!.GetValue<bool>(), dry.ToJsonString());
        Assert.True(dry["result"]!["dryRun"]!.GetValue<bool>());
        Assert.Equal(undo, session.UndoCount);
        Assert.Empty(session.Current.Pages[1].Annotations);
        Assert.NotEqual(hash, dry["result"]!["sourceHash"]!.GetValue<string>());
    });

    [Fact]
    public void OffTellsTheUserAndTheClient() => Headless.Run(async () =>
    {
        var w = await Opened(enabled: false);
        var live = w.View.ViewModel.LiveLink;
        var answer = await Ask(w, Request("context"));
        Assert.Equal("LIVE_DISABLED", Code(answer));
        Assert.True(live.IsDisabledNotice);
        Press(Find(w, "LiveEnableButton"));
        Assert.True(w.View.ViewModel.Settings.LiveLink);
        Assert.False(live.BarVisible);
    });

    [Fact]
    public void RenderViewHandsThePngBack() => Headless.Run(async () =>
    {
        var w = await Opened();
        string png = Path.Combine(Headless.TempFolder(), "asked.png");
        var answer = await Ask(w, Request("command", "render", "@live", "--view", "-o", png), "LiveReadButton");
        Assert.True(answer["ok"]!.GetValue<bool>(), answer.ToJsonString());
        Assert.Equal(png, answer["result"]!["output"]!.GetValue<string>());
        Assert.False(File.Exists(png)); // the window does not write where a client says; deot writes the bytes
        Assert.NotEmpty(Convert.FromBase64String(answer["files"]!["out"]!.GetValue<string>()));
        Assert.NotNull(answer["result"]!["region"]);
    });

    /// <summary>After a request added writing, the status line says where and offers 보기 and 되돌리기 (MCP_LIVE_PLAN 4).</summary>
    [Fact]
    public void AfterAWriteTheStatusLineShowsAndUndoesIt() => Headless.Run(async () =>
    {
        var w = await Opened();
        var vm = w.View.ViewModel;
        var session = w.View.Document!.Session;
        int undo = session.UndoCount;
        JsonObject Two() => new()
        {
            ["op"] = "command", ["client"] = "Claude", ["args"] = new JsonArray("apply", "@live", "-"),
            ["stdin"] = """[{ "op": "add.text", "page": 1, "rect": [50, 50, 120, 30], "text": "hi" }, { "op": "add.text", "page": 2, "rect": [60, 60, 120, 30], "text": "there" }]""",
        };
        Assert.False(vm.LiveLink.HasWritten);
        var answer = await Ask(w, Two(), "LiveAiWriteButton");
        Assert.True(answer["ok"]!.GetValue<bool>(), answer.ToJsonString());
        Assert.True(vm.LiveLink.HasWritten);
        Assert.Contains("p.1, p.2", vm.Status);
        Assert.Contains("Claude", vm.Status);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Find(w, "LiveShowWrittenButton").IsVisible);

        // 보기 goes there and the notice stays; 되돌리기 erases what the request added as a new Undo unit.
        Press(Find(w, "LiveShowWrittenButton"));
        Assert.True(vm.LiveLink.HasWritten);
        Press(Find(w, "LiveUndoWrittenButton"));
        Assert.Empty(session.Current.Pages[0].Annotations);
        Assert.Empty(session.Current.Pages[1].Annotations);
        Assert.Equal(undo + 2, session.UndoCount);
        Assert.False(vm.LiveLink.HasWritten);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Find(w, "LiveShowWrittenButton").IsVisible);

        // Another notice in the status line ends it too, and so does a request that added nothing.
        await Ask(w, Two());
        Assert.True(vm.LiveLink.HasWritten);
        vm.Status = "x";
        Assert.False(vm.LiveLink.HasWritten);
    });

    /// <summary>Holds a command at its start until the test lets it go (a file is opened meanwhile).</summary>
    private sealed class HeldCommands(ILiveCommands inner) : ILiveCommands
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Go { get; } = new(true);
        public string ScratchFolder => inner.ScratchFolder;
        public LiveCommandInfo Inspect(IReadOnlyList<string> tokens, string? stdin) => inner.Inspect(tokens, stdin);

        public LiveCommandResult Run(OpenDocument document, string id, IReadOnlyList<string> tokens, string? stdin)
        {
            Entered.Set();
            Go.Wait();
            return inner.Run(document, id, tokens, stdin);
        }
    }

    /// <summary>A read runs off the UI thread: a file opened meanwhile closes its document, and the client is told so (review 13).</summary>
    [Fact]
    public void AReadOfADocumentClosedMeanwhileSaysSo() => Headless.Run(async () =>
    {
        var held = new HeldCommands(new DesktopLiveCommands());
        var app = TestWindows.App(new AppSettings { LiveLink = true }, Headless.TempFolder()) with { LiveCommands = held };
        var w = new MainWindow(app, TestWindows.Dialogs) { Width = 1200, Height = 1000 };
        w.Script.PromptOverride = (_, _) => Answer.Yes;
        w.Script.MessageSink = _ => { };
        w.Show();
        string pdf = Path.Combine(Headless.TempFolder(), "live.pdf"), other = Path.Combine(Headless.TempFolder(), "other.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        ProbeSamplePdf.Create(other, 2);
        Assert.True(await w.View.OpenFileAsync(pdf));
        Dispatcher.UIThread.RunJobs();
        Assert.True((await Ask(w, Request("command", "info", "@live"), "LiveReadButton"))["ok"]!.GetValue<bool>());

        foreach (string[] command in new[] { new[] { "text", "@live", "-p", "1" }, ["render", "@live", "-p", "1", "-o", "x.png"], ["annot", "list", "@live"] })
        {
            if (w.View.Document?.FilePath != pdf)
            {
                Assert.True(await w.View.OpenFileAsync(pdf));
                Dispatcher.UIThread.RunJobs();
                held.Go.Set();
                Assert.True((await Ask(w, Request("command", "info", "@live"), "LiveReadButton"))["ok"]!.GetValue<bool>());
            }
            held.Entered.Reset();
            held.Go.Reset();
            var answer = w.View.ViewModel.LiveLink.HandleAsync(Request("command", command));
            Headless.Pump(() => held.Entered.IsSet);
            Assert.True(await w.View.OpenFileAsync(other)); // the first document is closed under the command
            Dispatcher.UIThread.RunJobs();
            held.Go.Set();
            Headless.Pump(() => answer.IsCompleted, 20_000);
            var result = await answer;
            Assert.False(result["ok"]!.GetValue<bool>(), string.Join(' ', command) + ": " + result.ToJsonString());
            Assert.Equal("LIVE_DOC_NOT_FOUND", Code(result));
        }
    });
}
