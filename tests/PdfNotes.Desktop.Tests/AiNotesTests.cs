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
/// The AI tab (MCP_LIVE_PLAN 6): it shows with a connection or AI writing, hides AI writing from the screen and the tools,
/// erases it page by page or all at once, takes back what the last request added, and the work list erases or adopts
/// batches. Plus the connection's access change and the status a client sees.
/// </summary>
public sealed class AiNotesTests
{
    private static async Task<MainWindow> Opened(int pages = 3)
    {
        string pdf = Path.Combine(Headless.TempFolder(), "ai.pdf");
        ProbeSamplePdf.Create(pdf, pages);
        var app = TestWindows.App(new AppSettings { LiveLink = true }, Headless.TempFolder()) with { LiveCommands = new DesktopLiveCommands() };
        var w = new MainWindow(app, TestWindows.Dialogs) { Width = 1200, Height = 1000 };
        w.Script.PromptOverride = (_, _) => Answer.Yes;
        w.Script.MessageSink = _ => { };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(await w.View.OpenFileAsync(pdf));
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    private static Control Find(MainWindow w, string name) =>
        w.GetLogicalDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name) ?? w.View.FindControl<Control>(name) ?? throw new InvalidOperationException(name);

    private static void Press(MainWindow w, string name)
    {
        var button = (Button)Find(w, name);
        Assert.True(button.Command!.CanExecute(button.CommandParameter), name);
        button.Command.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<JsonObject> Send(MainWindow w, JsonObject request, string? button = null)
    {
        var live = w.View.ViewModel.LiveLink;
        var answer = live.HandleAsync(request);
        if (button is not null)
        {
            Headless.Pump(() => live.IsAsking);
            Press(w, button);
        }
        Headless.Pump(() => answer.IsCompleted, 20_000);
        return await answer;
    }

    private static JsonObject Apply(string plan) => new()
    {
        ["op"] = "command", ["client"] = "Claude", ["args"] = new JsonArray("apply", "@live", "-"), ["stdin"] = plan,
    };

    private const string TwoBoxes = """{ "ops": [{ "op": "add.text", "page": 1, "rect": [50, 50, 120, 30], "text": "one" }, { "op": "add.text", "page": 2, "rect": [60, 60, 120, 30], "text": "two" }] }""";

    [Fact]
    public void TheTabFollowsAiWritingHidesItAndErasesIt() => Headless.Run(async () =>
    {
        var w = await Opened();
        var vm = w.View.ViewModel;
        var ai = vm.AiNotes;
        var session = w.View.Document!.Session;
        Assert.False(ai.TabVisible);
        Assert.False(Find(w, "AiTabButton").IsVisible);

        var answer = await Send(w, Apply(TwoBoxes), "LiveAiWriteButton");
        Assert.True(answer["ok"]!.GetValue<bool>(), answer.ToJsonString());
        Dispatcher.UIThread.RunJobs();
        Assert.True(ai.TabVisible);
        Assert.True(Find(w, "AiTabButton").IsVisible);
        Assert.Equal(2, ai.Count);
        var run = Assert.Single(ai.Runs);
        Assert.Equal("Claude", run.Client);
        Assert.Equal(2, run.Created.Count);

        // live docs reports the access this client has (hello carries the client name).
        var hello = await Send(w, new JsonObject { ["op"] = "hello", ["client"] = "Claude" });
        Assert.Equal("ai-write", hello["access"]!.GetValue<string>());

        // Hidden: off the screen, the thumbnails and the tools; the status bar says so.
        var model = session.Current;
        var boxId = model.Pages[0].Annotations.OfType<TextBoxAnnotation>().Single().Id;
        Press(w, "AiHideButton");
        Assert.True(ai.IsHidden);
        Assert.True(Find(w, "AiHiddenText").IsVisible);
        Assert.Empty(ai.Visible(session.Current, session.Current.Pages[0]).Annotations);
        vm.Selection.Tool.SetSelection([boxId]);
        Assert.Empty(vm.Selection.Tool.SelectedIds);

        // 모두 선택 shows them again and selects this page's AI writing.
        Press(w, "AiSelectPageButton");
        Assert.True(ai.IsShown);
        Assert.Equal([boxId], vm.Selection.Tool.SelectedIds);

        // 이 페이지: one Undo unit; Undo brings the writing and its tag back.
        int undo = session.UndoCount;
        Press(w, "AiErasePageButton");
        Assert.Equal(1, ai.Count);
        Assert.Equal(undo + 1, session.UndoCount);
        Assert.Equal(Authorship.EraseLabel, session.UndoLabel);
        session.Undo();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, ai.Count);
        Assert.True(Authorship.IsAi(session.Current, boxId));

        // 전체 after the confirmation, locked AI writing too.
        session.Execute(DocumentEdits.SetLocked(session.Current, [boxId], true));
        Press(w, "AiEraseAllButton");
        Headless.Pump(() => ai.Count == 0);
        Assert.Empty(session.Current.Tags);
        Assert.True(ai.TabVisible); // still connected
    });

    [Fact]
    public void EraseLastTakesOnlyWhatItAddedAndTheListAdopts() => Headless.Run(async () =>
    {
        var w = await Opened();
        var ai = w.View.ViewModel.AiNotes;
        var session = w.View.Document!.Session;
        Assert.True((await Send(w, Apply(TwoBoxes), "LiveAiWriteButton"))["ok"]!.GetValue<bool>());
        Assert.True((await Send(w, Apply("""[{ "op": "add.text", "page": 3, "rect": [40, 40, 100, 30], "text": "three" }]""")))["ok"]!.GetValue<bool>());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, ai.Count);
        Assert.Equal(2, ai.Runs.Count);

        // The work list: one row per request, the newest first.
        Press(w, "AiHistoryButton");
        Assert.True(Find(w, "AiPanel").IsVisible);
        Assert.Equal(2, ai.Items.Count);
        Assert.Contains("p.3", ai.Items[0].Detail);
        Assert.Contains("Claude", ai.Items[0].Title);

        // 마지막 작업: only the third box goes.
        Press(w, "AiEraseLastButton");
        Assert.Equal(2, ai.Count);
        Assert.Empty(session.Current.Pages[2].Annotations);
        Assert.Single(ai.Items);

        // [내 필기로] on the remaining row: the boxes stay, the tags go.
        ai.Items[0].IsChecked = true;
        Press(w, "AiAdoptCheckedButton");
        Assert.Equal(0, ai.Count);
        Assert.Single(session.Current.Pages[0].Annotations);
        Assert.Equal(Authorship.AdoptLabel, session.UndoLabel);
        Assert.True(ai.ItemsEmpty);
    });

    [Fact]
    public void ChangingAccessAppliesToTheNextRequest() => Headless.Run(async () =>
    {
        var w = await Opened();
        var live = w.View.ViewModel.LiveLink;
        Assert.True((await Send(w, Apply(TwoBoxes), "LiveAiWriteButton"))["ok"]!.GetValue<bool>());
        Assert.True(live.HasAiWrite);
        live.SetAccessCommand.Execute("Read");
        Assert.True(live.HasRead);
        Assert.Contains("Claude", live.IndicatorText);
        var refused = await Send(w, Apply("""[{ "op": "add.text", "page": 1, "rect": [10, 10, 60, 20], "text": "x" }]"""));
        Assert.Equal("LIVE_PERMISSION", refused["result"]!["error"]!["code"]!.GetValue<string>());

        // Writing while AI writing is hidden: the status and the client both hear it.
        live.SetAccessCommand.Execute("AiWrite");
        w.View.ViewModel.AiNotes.SetView(AiNotesView.Hidden);
        var hidden = await Send(w, Apply("""[{ "op": "add.text", "page": 1, "rect": [10, 10, 60, 20], "text": "x" }]"""));
        Assert.True(hidden["ok"]!.GetValue<bool>(), hidden.ToJsonString());
        Assert.Contains(hidden["result"]!["warnings"]!.AsArray(), x => x!["code"]!.GetValue<string>() == "AI_NOTES_HIDDEN");
    });

    [Fact]
    public void DisconnectingAsksFirst() => Headless.Run(async () =>
    {
        var w = await Opened();
        var live = w.View.ViewModel.LiveLink;
        Assert.True((await Send(w, Apply(TwoBoxes), "LiveAiWriteButton"))["ok"]!.GetValue<bool>());
        w.Script.PromptOverride = (_, _) => Answer.No;
        Press(w, "AiDisconnectButton");
        Headless.Pump(() => true);
        Assert.True(live.IsConnected); // a slip of the hand changes nothing
        w.Script.PromptOverride = (_, _) => Answer.Yes;
        Press(w, "AiDisconnectButton");
        Headless.Pump(() => !live.IsConnected);
        Assert.False(live.IsConnected);
    });

    /// <summary>MCP_LIVE_PLAN 5: the pieces an eraser leaves of AI writing are AI writing; the source key stays with the piece that keeps the id.</summary>
    [Fact]
    public void EraserPiecesStayAiWriting()
    {
        var stroke = new InkStroke(Guid.NewGuid(), false, PdfNotes.Core.Geometry.Affine.Identity, [new(0, 50), new(200, 50)], 4, new RgbColor(255, 0, 0), 1);
        var model = DocumentModel.Create([new PageSetup(560, 600, 0, PageSource.Generated, PageBackground.Blank)]);
        model = model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Annotations = [stroke] }), Tags = model.Tags.Add(stroke.Id, new AnnotationTag(AnnotationTag.AiAuthor, "b1", "key-1")) };
        var session = new DocumentSession(model);
        var gesture = new EraseGesture(session.Current, model.Pages[0].Id, EraserMode.Area, 20);
        gesture.AddPoint(new(100, 50)); // cuts the middle out: two pieces
        session.Execute(gesture.Commit());
        var pieces = session.Current.Pages[0].Annotations;
        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, p => Assert.True(Authorship.IsAi(session.Current, p.Id)));
        Assert.Single(pieces, p => session.Current.TagOf(p.Id)!.SourceKey == "key-1");
        Assert.All(pieces, p => Assert.Equal("b1", session.Current.TagOf(p.Id)!.BatchId));
        session.Undo();
        Assert.Single(session.Current.Tags);
    }

    [Fact]
    public void ARunCountsWhatOnlyUndoTakesBack()
    {
        var session = new DocumentSession(DocumentModel.Create([new PageSetup(560, 600, 0, PageSource.Generated, PageBackground.Blank)]));
        var page = session.Current.Pages[0];
        var mine = new StickyNote(Guid.NewGuid(), false, PdfNotes.Core.Geometry.Affine.Translation(10, 10), "mine", new RgbColor(255, 213, 79));
        session.Execute(DocumentEdits.AddAnnotation(session.Current, page.Id, mine));
        var model = session.Current;
        var changed = mine with { Text = "changed by AI" };
        var added = mine with { Id = Guid.NewGuid(), Text = "new" };
        var run = AiRun.From("b", DateTimeOffset.UnixEpoch, "Claude", model,
            [new ReplaceAnnotationOperation(page.Id, mine, changed), new InsertAnnotationOperation(page.Id, 1, added)]);
        Assert.Equal([added.Id], run.Created);
        Assert.Equal(1, run.UndoOnlyChanges);
    }
}
