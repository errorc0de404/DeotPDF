using System.Globalization;
using System.Text.Json.Nodes;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;

namespace PdfNotes.ViewModels;

/// <summary>Permission steps a user grants an AI client on one open document (MCP_LIVE_PLAN 2.1).</summary>
public enum LiveAccess { None = 0, Read = 1, AiWrite = 2, FullEdit = 3 }

/// <summary>deot's commands as the live link runs them on the open document (the desktop app implements it with them).</summary>
public interface ILiveCommands
{
    /// <summary>The only folder a live command may write a file in (the PNG the window hands back as bytes).</summary>
    string ScratchFolder { get; }

    /// <summary>What a command line is: its name, whether it writes, the access it needs, or why it cannot run live.</summary>
    LiveCommandInfo Inspect(IReadOnlyList<string> tokens, string? stdin);

    /// <summary>Runs it on a scratch copy of <paramref name="document"/>; a writing command hands back its operations.</summary>
    LiveCommandResult Run(OpenDocument document, string id, IReadOnlyList<string> tokens, string? stdin);
}

public sealed record LiveCommandInfo(string? Command, bool IsWrite, LiveAccess Needs, string? Refusal);

public sealed record LiveCommandResult(int ExitCode, string Json, IReadOnlyList<EditOperation> Operations);

/// <summary>
/// The open document as AI clients reach it through <c>deot</c> (MCP_LIVE_PLAN). Requests come from the window's pipe
/// (any thread) and are handled on the UI thread: the user consents once per client and document with the bar above the
/// document, reads run on a snapshot, and a write waits for the user's input to end and becomes one Undo unit. The status
/// bar shows who is connected; pressing it disconnects.
/// </summary>
public sealed class LiveLinkViewModel : ObservableObject
{
    /// <summary>How long a request waits for the user's answer (MCP_LIVE_PLAN 2).</summary>
    public static readonly TimeSpan ConsentWait = TimeSpan.FromSeconds(60);

    /// <summary>How long a write waits for a stroke or text editing to end.</summary>
    public static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(10);

    private readonly MainViewModel main;
    private readonly ILiveCommands? commands;
    private readonly IDocumentViewport viewport;
    private readonly IUiThread ui;
    private readonly TimeProvider time;
    private readonly IEditorDialogs dialogs;
    private readonly Dictionary<(Guid Session, string Client), LiveAccess> granted = [];
    private readonly List<Pending> pending = [];
    /// <summary>The most a client asked for beyond what it has and did not get: it is not asked for that again (MCP_LIVE_PLAN 15 P1).</summary>
    private readonly Dictionary<(Guid Session, string Client), LiveAccess> refused = [];

    /// <summary>A question on the bar. <paramref name="More"/>: the access a client that already has some asks for.</summary>
    private sealed record Pending(Guid Session, string Client, LiveAccess? More, TaskCompletionSource<LiveAccess> Answer);

    public LiveLinkViewModel(MainViewModel main, ILiveCommands? commands, IDocumentViewport viewport, IUiThread ui, TimeProvider time, IEditorDialogs dialogs)
    {
        this.main = main;
        this.dialogs = dialogs;
        this.commands = commands;
        this.viewport = viewport;
        this.ui = ui;
        this.time = time;
        main.DocumentShown += (_, _) => DocumentChanged();
        // The notice of a write lasts as long as the status line says it.
        main.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Status) && written is not null && main.Status != writtenStatus) Written = null; };
        ShowWrittenCommand = new(() => { if (written is { } run) main.AiNotes?.Show(run); });
        UndoWrittenCommand = new(() => { if (written is { } run) main.AiNotes?.Erase(run); });
        GrantReadCommand = new(() => Answer(LiveAccess.Read));
        GrantAiWriteCommand = new(() => Answer(LiveAccess.AiWrite));
        GrantFullCommand = new(() => Answer(LiveAccess.FullEdit));
        DenyCommand = new(() => Answer(LiveAccess.None));
        EnableCommand = new(Enable);
        CloseDisabledCommand = new(() => DisabledClient = null);
        DisconnectCommand = new(DisconnectAsync);
        SetAccessCommand = new(name => { if (Enum.TryParse<LiveAccess>(name, out var access)) SetAccess(access); });
    }

    /// <summary>True when this app can be reached at all (desktop).</summary>
    public bool Available => commands is not null;

    // ----- the bar above the document and the status bar -----

    private string? askingClient;
    private string? disabledClient;
    private string? indicatorText;

    /// <summary>The client the bar asks about, or null (no question).</summary>
    public string? AskingClient
    {
        get => askingClient;
        private set { if (SetProperty(ref askingClient, value)) { OnPropertyChanged(nameof(IsAsking)); OnPropertyChanged(nameof(AskText)); OnPropertyChanged(nameof(BarVisible)); } }
    }

    public bool IsAsking => askingClient is not null;
    public string AskText => pending.Count == 0 ? ""
        : pending[0].More is { } more && granted.TryGetValue((pending[0].Session, pending[0].Client), out var has)
            ? main.Texts.Format("live.askMore", ("client", pending[0].Client), ("access", main.Texts.Get(AccessKey(more))), ("current", main.Texts.Get(AccessKey(has))))
            : main.Texts.Format("live.ask", ("client", pending[0].Client));

    /// <summary>A client that asked while the live link is off (the bar offers to turn it on).</summary>
    public string? DisabledClient
    {
        get => disabledClient;
        private set { if (SetProperty(ref disabledClient, value)) { OnPropertyChanged(nameof(IsDisabledNotice)); OnPropertyChanged(nameof(DisabledText)); OnPropertyChanged(nameof(BarVisible)); } }
    }

    public bool IsDisabledNotice => disabledClient is not null && askingClient is null;
    public string DisabledText => disabledClient is null ? "" : main.Texts.Format("live.disabled", ("client", disabledClient));
    public bool BarVisible => IsAsking || IsDisabledNotice;

    /// <summary>"AI 연결됨 · Claude (읽기)", or null when no client has access to this document.</summary>
    public string? IndicatorText { get => indicatorText; private set { if (SetProperty(ref indicatorText, value)) OnPropertyChanged(nameof(IsConnected)); } }

    public bool IsConnected => indicatorText is not null;

    // ----- after a write: [보기] [되돌리기] beside the status line (MCP_LIVE_PLAN 4) -----

    private AiRun? written;
    private string? writtenStatus;

    /// <summary>The request the status line tells of now (it added writing), or null.</summary>
    private AiRun? Written
    {
        get => written;
        set { if (!ReferenceEquals(written, value)) { written = value; OnPropertyChanged(nameof(HasWritten)); } }
    }

    /// <summary>True while the status line tells of writing an AI client just added: the two buttons show.</summary>
    public bool HasWritten => written is not null;

    /// <summary>보기: goes to what the request wrote.</summary>
    public Command ShowWrittenCommand { get; }

    /// <summary>되돌리기: erases what the request added (the AI tab's "마지막 작업" for this request).</summary>
    public Command UndoWrittenCommand { get; }

    public Command GrantReadCommand { get; }
    public Command GrantAiWriteCommand { get; }
    public Command GrantFullCommand { get; }
    public Command DenyCommand { get; }
    public Command EnableCommand { get; }
    public Command CloseDisabledCommand { get; }
    public AsyncCommand DisconnectCommand { get; }

    /// <summary>권한 바꾸기 (MCP_LIVE_PLAN 2): every connected client of this document gets this access (parameter: Read, AiWrite, FullEdit).</summary>
    public Command<string> SetAccessCommand { get; }

    /// <summary>The highest access a client has on this document (the AI tab's connection buttons show it).</summary>
    public LiveAccess CurrentAccess
    {
        get
        {
            var session = main.Document?.Session.SessionId;
            return granted.Where(g => g.Key.Session == session).Select(g => g.Value).DefaultIfEmpty(LiveAccess.None).Max();
        }
    }

    public bool HasRead => CurrentAccess == LiveAccess.Read;
    public bool HasAiWrite => CurrentAccess == LiveAccess.AiWrite;
    public bool HasFullEdit => CurrentAccess == LiveAccess.FullEdit;

    private void SetAccess(LiveAccess access)
    {
        var session = main.Document?.Session.SessionId;
        // What the user sets here is what they want: a client that needs more is refused without a question.
        foreach (var key in granted.Keys.Where(k => k.Session == session && granted[k] != LiveAccess.None).ToList())
        {
            granted[key] = access;
            refused[key] = LiveAccess.FullEdit;
        }
        RefreshIndicator();
    }

    private void Answer(LiveAccess access)
    {
        if (pending.Count == 0) return;
        var first = pending[0];
        pending.RemoveAt(0);
        var key = (first.Session, first.Client);
        if (first.More is { } more && granted.TryGetValue(key, out var had) && access < more)
        {
            // Asked for more and not given it: the client keeps what it had (거절 does not disconnect it) and is not asked again.
            if (more > refused.GetValueOrDefault(key)) refused[key] = more;
            if (access == LiveAccess.None) access = had;
        }
        granted[key] = access;
        first.Answer.TrySetResult(access);
        ShowNext();
        RefreshIndicator();
    }

    private void ShowNext()
    {
        string? before = askingClient;
        AskingClient = pending.Count > 0 ? pending[0].Client : null;
        OnPropertyChanged(nameof(AskText)); // the same client may ask for more next
        if (askingClient is not null && before is null) main.Window.RequestAttention(); // the window may be behind the AI client's
    }

    private void Enable()
    {
        main.Settings.LiveLink = true;
        main.SaveSettings();
        DisabledClient = null;
        main.Status = main.Texts.Get("live.enabled");
    }

    /// <summary>
    /// 끊기: after one confirmation (a slip of the hand would otherwise make every client ask again) every client loses
    /// access to this document; each is asked again next time.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (main.Document is not { } doc || !IsConnected) return;
        if (await dialogs.AskAsync(main.Texts.Get("live.disconnectConfirm"), AskButtons.YesNo, NoticeKind.Question) != PdfNotes.Editor.Platform.Answer.Yes) return;
        if (!ReferenceEquals(main.Document, doc)) return;
        var session = doc.Session.SessionId;
        foreach (var key in granted.Keys.Where(k => k.Session == session).ToList()) granted.Remove(key);
        foreach (var key in refused.Keys.Where(k => k.Session == session).ToList()) refused.Remove(key);
        RefreshIndicator();
        main.Status = main.Texts.Get("live.disconnected");
    }

    private void DocumentChanged()
    {
        // Consent is per document: questions about the one that left are answered "no".
        var session = main.Document?.Session.SessionId;
        foreach (var p in pending.Where(p => p.Session != session).ToList())
        {
            pending.Remove(p);
            p.Answer.TrySetResult(LiveAccess.None);
        }
        Written = null;
        ShowNext();
        RefreshIndicator();
    }

    private void RefreshIndicator()
    {
        var session = main.Document?.Session.SessionId;
        var clients = granted.Where(g => g.Key.Session == session && g.Value != LiveAccess.None)
            .Select(g => $"{g.Key.Client} ({main.Texts.Get(AccessKey(g.Value))})").ToList();
        IndicatorText = clients.Count == 0 ? null : main.Texts.Format("live.connected", ("clients", string.Join(", ", clients)));
        OnPropertyChanged(nameof(CurrentAccess));
        OnPropertyChanged(nameof(HasRead));
        OnPropertyChanged(nameof(HasAiWrite));
        OnPropertyChanged(nameof(HasFullEdit));
        main.AiNotes?.Refresh(); // the AI tab shows while a client is connected
    }

    private static string AccessKey(LiveAccess access) => access switch
    {
        LiveAccess.Read => "live.accessRead",
        LiveAccess.AiWrite => "live.accessAiWrite",
        _ => "live.accessFull",
    };

    // ----- requests -----

    /// <summary>The open document's id for clients: the process and the document session (a new file gets a new id).</summary>
    public string? DocumentId => main.Document is { } doc ? $"{Environment.ProcessId}-{doc.Session.SessionId.ToString("N")[..8]}" : null;

    /// <summary>Handles one request from the pipe (any thread) on the UI thread.</summary>
    public Task<JsonObject> HandleAsync(JsonObject request)
    {
        var done = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(async () =>
        {
            try { done.TrySetResult(await HandleOnUiAsync(request)); }
            catch (Exception e) { done.TrySetResult(Error("INTERNAL", e.Message)); }
        });
        return done.Task;
    }

    private async Task<JsonObject> HandleOnUiAsync(JsonObject request)
    {
        string op = Text(request, "op") ?? "";
        string client = Client(Text(request, "client"));
        if (op == "hello") return Hello(client);
        if (commands is null) return Error("LIVE_DISABLED", "This DeotPDF cannot be reached by AI clients.");
        if (main.Document is not { } doc || DocumentId is not { } id) return Error("LIVE_DOC_NOT_FOUND", "No document is open in this window.");
        if (Text(request, "doc") is { } wanted && wanted != id)
            return Error("LIVE_DOC_NOT_FOUND", $"The document {wanted} is no longer open here (a different file was opened).", new JsonObject { ["doc"] = id });
        if (!main.Settings.LiveLink)
        {
            if (disabledClient is null) main.Window.RequestAttention();
            DisabledClient = client;
            return Error("LIVE_DISABLED", "The user has not turned on AI tool access in DeotPDF. They were asked to turn it on; try again after they do.");
        }

        var args = (request["args"] as JsonArray)?.Select(a => a?.GetValue<string>() ?? "").ToList() ?? [];
        string? stdin = Text(request, "stdin");
        LiveCommandInfo? info = null;
        var needs = LiveAccess.Read;
        if (op == "command")
        {
            args = RewriteView(args, out var viewError);
            if (viewError is not null) return viewError;
            info = commands.Inspect(args, stdin);
            if (info.Refusal is { } refusal) return Error("LIVE_UNSUPPORTED", refusal);
            needs = info.Needs;
        }
        else if (op is not ("context" or "goto" or "flash")) return Error("INVALID_ARGUMENT", $"Unknown request '{op}'.");

        var access = await AccessAsync(doc, client, needs);
        if (access == null) return Error("LIVE_CONSENT_TIMEOUT", "The user did not answer within a minute. The question stays on screen; try again.");
        if (access == LiveAccess.None) return Error("LIVE_CONSENT_DENIED", "The user refused access to this document.");
        if (access < needs)
            return Refused(needs, access.Value);
        if (!ReferenceEquals(main.Document, doc)) return Error("LIVE_DOC_NOT_FOUND", "A different file was opened meanwhile.");

        return op switch
        {
            "context" => Ok(Context(doc, id)),
            "goto" or "flash" => GoTo(doc, request, op == "flash") is var shown && shown["ok"]!.GetValue<bool>() ? Ok(shown) : shown,
            _ => info!.IsWrite ? await WriteAsync(doc, id, client, needs, args, stdin) : await ReadAsync(doc, id, args, stdin, request),
        };
    }

    private JsonObject Hello(string client)
    {
        var doc = main.Document;
        var session = doc?.Session.SessionId;
        var access = session is { } s && granted.TryGetValue((s, client), out var a) ? a : LiveAccess.None;
        bool enabled = commands is not null && main.Settings.LiveLink;
        // While the link is off a caller learns only that a window is there (so that it can be told to ask the user).
        return new JsonObject
        {
            ["ok"] = true,
            ["doc"] = DocumentId,
            ["name"] = enabled ? doc?.DisplayNameIn(main.Texts) : null,
            ["path"] = enabled ? doc?.FilePath : null,
            ["dirty"] = enabled && (doc?.Session.IsDirty ?? false),
            ["pageCount"] = enabled ? doc?.Session.Current.Pages.Count ?? 0 : 0,
            ["enabled"] = enabled,
            ["access"] = Name(access),
        };
    }

    /// <summary>The access <paramref name="client"/> has on <paramref name="doc"/>, asking with the bar when it has none yet, or once when it has less than
    /// <paramref name="needs"/> (the user picked 읽기만 and the client now wants to write); null when nobody answered in time.</summary>
    private async Task<LiveAccess?> AccessAsync(OpenDocument doc, string client, LiveAccess needs)
    {
        var key = (doc.Session.SessionId, client);
        bool has = granted.TryGetValue(key, out var given);
        // A refusal stands, and so does a "no" to more: asking each time would nag.
        if (has && (given == LiveAccess.None || given >= needs || refused.GetValueOrDefault(key) >= needs)) return given;
        var waiting = pending.FirstOrDefault(p => p.Session == key.SessionId && p.Client == client);
        if (waiting is null)
        {
            waiting = new Pending(key.SessionId, client, has ? needs : null, new(TaskCreationOptions.RunContinuationsAsynchronously));
            pending.Add(waiting);
            ShowNext();
        }
        var answered = await Task.WhenAny(waiting.Answer.Task, Task.Delay(ConsentWait, time));
        return answered == waiting.Answer.Task ? waiting.Answer.Task.Result : null;
    }

    private static JsonObject Refused(LiveAccess needs, LiveAccess has) =>
        Error("LIVE_PERMISSION", $"This needs '{Name(needs)}' access; the user gave '{Name(has)}'. Only the user can raise it: in DeotPDF's AI tab, or by answering when the document asks.",
            new JsonObject { ["needs"] = Name(needs), ["granted"] = Name(has) });

    // ----- reading -----

    private async Task<JsonObject> ReadAsync(OpenDocument doc, string id, List<string> args, string? stdin, JsonObject request)
    {
        // A PNG is drawn into a file of this app and handed back as bytes: the window does not write where a client says.
        int outIndex = args.FindIndex(a => a is "--out" or "-o");
        string? asked = null, temp = null;
        if (outIndex >= 0 && outIndex + 1 < args.Count)
        {
            asked = args[outIndex + 1];
            temp = Path.Combine(commands!.ScratchFolder, $"{Guid.NewGuid():N}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            args[outIndex + 1] = temp;
        }
        try
        {
            var result = await Task.Run(() => commands!.Run(doc, id, args, stdin));
            // A file opened meanwhile closed the document under the command: whatever it reported (an error from the
            // closed PDF, or an answer about a document that is no longer there), the client is told that.
            if (!ReferenceEquals(main.Document, doc)) return Error("LIVE_DOC_NOT_FOUND", "The document was closed while the command ran.");
            var response = Wrap(result);
            if (temp is not null && File.Exists(temp))
            {
                response["files"] = new JsonObject { ["out"] = Convert.ToBase64String(File.ReadAllBytes(temp)) };
                if (response["result"] is JsonObject r && r["output"] is not null) r["output"] = asked;
            }
            return response;
        }
        catch (ObjectDisposedException) { return Error("LIVE_DOC_NOT_FOUND", "The document was closed while the command ran."); }
        finally { if (temp is not null) TryDelete(temp); }
    }

    /// <summary><c>render --view</c>: the visible part of the page in view (<c>-p</c> and <c>--region</c> from the canvas).</summary>
    private List<string> RewriteView(List<string> args, out JsonObject? error)
    {
        error = null;
        int index = args.IndexOf("--view");
        if (index < 0) return args;
        var model = main.Document!.Session.Current;
        int page = viewport.CurrentPageIndex;
        if (page < 0 || page >= model.Pages.Count || viewport.VisiblePart(page) is not { } part)
        {
            error = Error("LIVE_NOTHING_IN_VIEW", "No page is in view.");
            return args;
        }
        if (args.Contains("--page") || args.Contains("-p") || args.Contains("--region") || args.Contains("--grid"))
        {
            error = Error("INVALID_ARGUMENT", "--view picks the page and region itself; leave out -p, --region and --grid.");
            return args;
        }
        var rewritten = new List<string>(args);
        rewritten.RemoveAt(index);
        rewritten.AddRange(["--page", model.Pages[page].Id.ToString(), "--region", Rect(part)]);
        return rewritten;
    }

    private JsonObject Context(OpenDocument doc, string id)
    {
        var model = doc.Session.Current;
        var visible = new JsonArray();
        for (int i = 0; i < model.Pages.Count; i++)
            if (viewport.VisiblePart(i) is { } part)
                visible.Add(new JsonObject { ["page"] = i + 1, ["pageId"] = model.Pages[i].Id.ToString(), ["rect"] = RectArray(part) });
        int current = Math.Clamp(viewport.CurrentPageIndex, 0, Math.Max(0, model.Pages.Count - 1));
        var result = new JsonObject
        {
            ["ok"] = true,
            ["command"] = "live context",
            ["doc"] = id,
            ["name"] = doc.DisplayNameIn(main.Texts),
            ["path"] = doc.FilePath,
            ["dirty"] = doc.Session.IsDirty,
            ["pageCount"] = model.Pages.Count,
            ["currentPage"] = model.Pages.Count == 0 ? null : new JsonObject { ["page"] = current + 1, ["pageId"] = model.Pages[current].Id.ToString() },
            ["zoom"] = Math.Round(viewport.Zoom, 4),
            ["visible"] = visible,
            ["selectedAnnotations"] = SelectedAnnotations(model),
            ["selectedText"] = SelectedText(model),
        };
        return result;
    }

    private JsonArray SelectedAnnotations(DocumentModel model)
    {
        var list = new JsonArray();
        SelectTool tool;
        try { tool = main.Selection.Tool; }
        catch (InvalidOperationException) { return list; }
        foreach (var annotationId in tool.SelectedIds)
        {
            if (model.FindAnnotation(annotationId) is not { } found) continue;
            var (page, annotation) = (found.Page, found.Annotation);
            var item = new JsonObject
            {
                ["id"] = annotationId.ToString(),
                ["page"] = model.IndexOfPage(page.Id) + 1,
                ["pageId"] = page.Id.ToString(),
                ["kind"] = annotation.GetType().Name,
                ["ai"] = model.TagOf(annotationId)?.Author == AnnotationTag.AiAuthor,
            };
            if (annotation is TextBoxAnnotation box) item["text"] = box.Text;
            else if (annotation is StickyNote note) item["text"] = note.Text;
            list.Add(item);
        }
        return list;
    }

    private JsonObject? SelectedText(DocumentModel model)
    {
        SelectTool tool;
        try { tool = main.Selection.Tool; }
        catch (InvalidOperationException) { return null; }
        if (!tool.HasTextSelection) return null;
        int page = tool.TextSelectionPage;
        if (page < 0 || page >= model.Pages.Count) return null;
        var (start, end) = tool.TextSelectionRange;
        return new JsonObject
        {
            ["page"] = page + 1,
            ["pageId"] = model.Pages[page].Id.ToString(),
            ["text"] = tool.SelectedText,
            ["chars"] = new JsonArray(start, end),
            ["quads"] = new JsonArray([.. tool.TextSelectionQuads(page).Select(q => (JsonNode)new JsonArray(
                N(q.UpperLeft.X), N(q.UpperLeft.Y), N(q.UpperRight.X), N(q.UpperRight.Y), N(q.LowerRight.X), N(q.LowerRight.Y), N(q.LowerLeft.X), N(q.LowerLeft.Y)))]),
        };
    }

    // ----- showing -----

    /// <summary><c>live goto</c> / <c>live flash</c>: scrolls to a page (and point or rectangle); flash also marks the place for a moment.</summary>
    private JsonObject GoTo(OpenDocument doc, JsonObject request, bool flash)
    {
        var model = doc.Session.Current;
        var args = (request["args"] as JsonArray)?.Select(a => a?.GetValue<string>() ?? "").ToList() ?? [];
        string? pageArg = Option(args, "--page") ?? Option(args, "-p");
        if (pageArg is null) return Error("INVALID_ARGUMENT", "Give the page with -p <number or page ID>.");
        int index = int.TryParse(pageArg, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number - 1
            : Guid.TryParse(pageArg, out var pageId) ? model.IndexOfPage(pageId) : -1;
        if (index < 0 || index >= model.Pages.Count) return Error("PAGE_NOT_FOUND", $"Page '{pageArg}' does not exist (pages 1-{model.Pages.Count}).");
        var setup = model.Pages[index].Setup;
        Rect2 target = new(0, 0, setup.WidthPt, setup.HeightPt);
        if (Option(args, "--rect") is { } rectText)
        {
            var n = rectText.Split(',').Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN).ToArray();
            if (n.Length != 4 || !n.All(double.IsFinite) || n[2] <= 0 || n[3] <= 0) return Error("INVALID_ARGUMENT", "--rect is x,y,w,h in page points.");
            target = new(n[0], n[1], n[2], n[3]);
        }
        if (Option(args, "--rect") is null) viewport.GoToPage(index);
        else viewport.ScrollToPagePoint(index, new Point2(target.X + target.Width / 2, target.Y + target.Height / 2));
        if (flash) Flashed?.Invoke(index, target);
        return new JsonObject { ["ok"] = true, ["command"] = flash ? "live flash" : "live goto", ["page"] = index + 1, ["pageId"] = model.Pages[index].Id.ToString(), ["rect"] = RectArray(target) };
    }

    /// <summary>A place to mark for a moment (page index, canonical rectangle); the canvas draws it (MCP_LIVE_PLAN 3).</summary>
    public event Action<int, Rect2>? Flashed;

    // ----- writing -----

    private async Task<JsonObject> WriteAsync(OpenDocument doc, string id, string client, LiveAccess needs, List<string> args, string? stdin)
    {
        var deadline = time.GetUtcNow() + BusyWait;
        while (Busy())
        {
            if (time.GetUtcNow() >= deadline) return Error("LIVE_BUSY", "The user is writing or editing text; try again in a moment.");
            await Task.Delay(TimeSpan.FromMilliseconds(100), time);
            if (!ReferenceEquals(main.Document, doc)) return Error("LIVE_DOC_NOT_FOUND", "A different file was opened meanwhile.");
        }
        // The user may have disconnected or lowered the access while this waited.
        var now = granted.GetValueOrDefault((doc.Session.SessionId, client), LiveAccess.None);
        if (now < needs)
            return Refused(needs, now);
        // On the UI thread from the copy to the commit, so no edit of the user's comes in between.
        var result = commands!.Run(doc, id, args, stdin);
        var response = Wrap(result);
        if (result.ExitCode != 0 || result.Operations.Count == 0) return response;
        var before = doc.Session.Current;
        try { doc.Session.Execute(new EditCommand(Authorship.AiLabel, result.Operations)); }
        catch (EditConflictException e) { return Error("LIVE_CONFLICT", $"The edit no longer fits the document: {e.Message}"); }
        string batch = response["result"]?["batchId"]?.GetValue<string>() ?? "";
        var run = AiRun.From(batch, time.GetLocalNow(), client, before, result.Operations);
        main.AiNotes?.Record(run);
        var places = main.AiNotes?.Places(run) ?? [];
        string status = places.Count == 0 ? main.Texts.Format("live.applied", ("client", client))
            : main.Texts.Format("live.added", ("client", client), ("count", places.Sum(p => p.Count)), ("pages", Pages([.. places.Select(p => p.Page + 1)])));
        if (main.AiNotes?.IsHidden == true && run.Created.Count > 0)
        {
            status += " " + main.Texts.Get("live.appliedHidden");
            if (response["result"] is JsonObject r)
            {
                if (r["warnings"] is not JsonArray warnings) r["warnings"] = warnings = [];
                warnings.Add(new JsonObject { ["code"] = "AI_NOTES_HIDDEN", ["message"] = "The user has hidden AI writing; what was added stays hidden until they show it again." });
            }
        }
        Written = null;
        main.Status = status;
        if (places.Count > 0)
        {
            writtenStatus = status;
            Written = run;
            main.AiNotes?.FlashInView(run);
        }
        return response;
    }

    private static string Pages(List<int> pages) => pages.Count <= 3 ? string.Join(", ", pages.Select(p => $"p.{p}")) : $"p.{pages[0]}–{pages[^1]}";

    private bool Busy() => viewport.IsToolActive || main.TextStyle.IsEditingText || main.IsBusy || main.Workflow.IsConfirming;

    // ----- helpers -----

    private static JsonObject Ok(JsonObject result) => new() { ["ok"] = true, ["exit"] = 0, ["result"] = result };

    private static JsonObject Wrap(LiveCommandResult result) => new()
    {
        ["ok"] = result.ExitCode == 0,
        ["exit"] = result.ExitCode,
        ["result"] = JsonNode.Parse(result.Json),
    };

    private static JsonObject Error(string code, string message, JsonObject? details = null) => new()
    {
        ["ok"] = false,
        ["exit"] = code is "INTERNAL" ? 4 : code is "LIVE_BUSY" or "LIVE_CONSENT_TIMEOUT" ? 3 : 1,
        ["result"] = new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["details"] = details ?? new JsonObject() } },
    };

    private static string Client(string? name)
    {
        string text = (name ?? "").Trim();
        if (text.Length == 0) return "AI";
        text = new string([.. text.Where(c => !char.IsControl(c))]);
        return text.Length > 40 ? text[..40] : text;
    }

    private static string Name(LiveAccess access) => access switch
    {
        LiveAccess.Read => "read",
        LiveAccess.AiWrite => "ai-write",
        LiveAccess.FullEdit => "full-edit",
        _ => "none",
    };

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static string? Option(List<string> args, string name)
    {
        int i = args.IndexOf(name);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    private static double N(double v) => Math.Round(v, 2);
    private static string Rect(Rect2 r) => string.Join(',', new[] { r.X, r.Y, r.Width, r.Height }.Select(v => N(v).ToString(CultureInfo.InvariantCulture)));
    private static JsonArray RectArray(Rect2 r) => new(N(r.X), N(r.Y), N(r.Width), N(r.Height));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
