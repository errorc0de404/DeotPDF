using System.Globalization;
using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Session;
using PdfNotes.Tests.Core;

namespace PdfNotes.Tests.App;

/// <summary>D14d: the editor's file flow, tool switch and command helpers run without the WPF shell.</summary>
public sealed class EditorSessionTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));

    public EditorSessionTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        host.Workflow?.Detach()?.Dispose();
        foreach (var d in host.Released) d.Dispose();
        Directory.Delete(folder, true);
    }

    private sealed class Shell : IDocumentHost, IEditorDialogs
    {
        public DocumentWorkflow? Workflow;
        public readonly List<string> Log = [];
        public readonly List<OpenDocument> Released = [];
        public Queue<Answer> Answers = new();
        public Func<SavePathRequest, string?> SavePath = _ => null;

        public void FinishTextEditing() => Log.Add("finish");
        public void CancelTool() => Log.Add("cancel");
        public void DocumentLeaving(OpenDocument? previous) => Log.Add("leaving");
        public void DocumentReplaced(OpenDocument? previous, OpenDocument next)
        {
            Assert.Same(next, Workflow!.Document);
            if (previous is not null) Released.Add(previous);
            Log.Add("replaced");
        }
        public void SetStatus(string text) => Log.Add("status:" + text);
        public void Loading(string? name) => Log.Add("loading:" + name);
        public void StateChanged() => Log.Add("state:" + Workflow!.IsBusy);
        public void Opened(string path) => Log.Add("opened");
        public void Saved(string path) => Log.Add("saved:" + Path.GetFileName(path));
        public string? ShownElsewhere { get; set; }
        public bool ShowOpenElsewhere(string path) => string.Equals(path, ShownElsewhere, StringComparison.OrdinalIgnoreCase);
        public Answer Ask(string text, AskButtons buttons, NoticeKind kind) { Log.Add("ask:" + buttons); return Answers.Dequeue(); }
        public void Tell(string text, NoticeKind kind) => Log.Add("tell:" + kind);
        public string? ChooseSavePath(SavePathRequest request) { Log.Add("choose:" + request.SuggestedName); return SavePath(request); }
    }

    private readonly Shell host = new();

    private DocumentWorkflow NewWorkflow()
    {
        var workflow = new DocumentWorkflow(host, host);
        host.Workflow = workflow;
        workflow.Replace(OpenDocument.CreateNote(NewPageSpec.Default));
        return workflow;
    }

    private static void Draw(OpenDocument doc) =>
        doc.Session.Execute(DocumentEdits.AddAnnotation(doc.Session.Current, doc.Session.Current.Pages[0].Id, TestModels.Ink()));

    [Fact]
    public async Task UnsavedChangesAskAndCancelKeepsTheDocument()
    {
        var workflow = NewWorkflow();
        var first = workflow.Document!;
        Assert.Equal(CloseStep.Close, workflow.CloseStep(backupRunning: false));
        Draw(first);
        Assert.Equal(CloseStep.Confirm, workflow.CloseStep(backupRunning: false));
        Assert.Equal(CloseStep.WaitForBackup, workflow.CloseStep(backupRunning: true));

        host.Answers.Enqueue(Answer.Cancel);
        Assert.False(await workflow.CreateNoteAsync(NewPageSpec.Default));
        Assert.Same(first, workflow.Document);

        host.Answers.Enqueue(Answer.No);
        Assert.True(await workflow.CreateNoteAsync(NewPageSpec.Default));
        Assert.NotSame(first, workflow.Document);
        Assert.Contains(first, host.Released);
        Assert.Equal(["finish", "cancel", "ask:YesNoCancel", "leaving", "replaced", "status:"], host.Log.Skip(host.Log.LastIndexOf("ask:YesNoCancel") - 2));
    }

    [Fact]
    public async Task SaveAsksForAPathOnceAndCancelledDialogWritesNothing()
    {
        var workflow = NewWorkflow();
        Draw(workflow.Document!);
        Assert.False(await workflow.SaveAsync(saveAs: false));
        Assert.Contains("choose:새 노트.pdf", host.Log);
        Assert.Empty(Directory.GetFiles(folder));

        string file = Path.Combine(folder, "a.pdf");
        host.SavePath = _ => file;
        host.Log.Clear();
        Assert.True(await workflow.SaveAsync(saveAs: false));
        Assert.Equal((file, false), (workflow.Document!.FilePath, workflow.Document.Session.IsDirty));
        Assert.Equal(["finish", "choose:새 노트.pdf", "finish", "state:True", "status:저장 중…", "saved:a.pdf", "status:저장됨", "state:False"], host.Log);

        host.Log.Clear();
        Draw(workflow.Document);
        Assert.True(await workflow.SaveAsync(saveAs: false)); // a saved file needs no dialog
        Assert.DoesNotContain(host.Log, l => l.StartsWith("choose:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpeningShowsTheLoadingCardUntilReadAndClosesItBeforeAFailureNotice()
    {
        var workflow = NewWorkflow();
        string file = Path.Combine(folder, "a.pdf");
        Assert.True(await workflow.SaveToAsync(file));

        host.Log.Clear();
        Assert.True(await workflow.OpenFileAsync(file));
        var loading = host.Log.Where(l => l.StartsWith("loading:", StringComparison.Ordinal)).ToList();
        Assert.Equal("loading:a.pdf", loading[0]);
        Assert.True(host.Log.IndexOf("loading:a.pdf") < host.Log.IndexOf("replaced"));
        Assert.True(host.Log.IndexOf("loading:") > host.Log.IndexOf("replaced"));
        Assert.Equal("loading:", loading[^1]);

        string broken = Path.Combine(folder, "broken.pdf");
        File.WriteAllText(broken, "not a pdf");
        host.Log.Clear();
        Assert.False(await workflow.OpenFileAsync(broken));
        Assert.True(host.Log.IndexOf("loading:") < host.Log.IndexOf("tell:OpenFailure"));
    }

    [Fact]
    public async Task FlattenExportOverTheOwnFileNeedsConfirmation()
    {
        var workflow = NewWorkflow();
        string file = Path.Combine(folder, "b.pdf");
        Assert.True(await workflow.SaveToAsync(file));
        byte[] saved = File.ReadAllBytes(file);
        host.SavePath = r => { Assert.Equal("b_flattened.pdf", r.SuggestedName); return file; };
        host.Answers.Enqueue(Answer.No);
        Assert.False(await workflow.ExportFlattenedAsync());
        Assert.Equal(saved, File.ReadAllBytes(file));

        string other = Path.Combine(folder, "b_flattened.pdf");
        host.SavePath = _ => other;
        Assert.True(await workflow.ExportFlattenedAsync());
        Assert.True(File.Exists(other));
        Assert.Equal(file, workflow.Document!.FilePath);
    }

    [Fact]
    public void ExportTargetsNumberSeveralFiles()
    {
        Assert.Equal([Path.Combine("x", "p.pdf")], DocumentWorkflow.ExportTargets(Path.Combine("x", "p.pdf"), 1));
        Assert.Equal([Path.Combine("x", "p_1.pdf"), Path.Combine("x", "p_2.pdf")], DocumentWorkflow.ExportTargets(Path.Combine("x", "p"), 2));
    }

    [Fact]
    public void ToolSwitchRemembersTheShapeAndEraserChoiceAndReturnsFromAVisit()
    {
        var settings = AppSettings.Load(Path.Combine(folder, "settings.json"));
        var tools = new ToolSwitch(settings);
        Assert.Equal(ToolKind.Pen, tools.Current);
        Assert.Equal(ToolKind.Select, tools.Choose(ToolKind.Pen, notesHidden: true));
        tools.Choose(ToolKind.Curve, notesHidden: false);
        tools.Choose(ToolKind.AreaEraser, notesHidden: false);
        Assert.Equal((ToolKind.Curve, ToolKind.AreaEraser), (tools.ForButton("ShapeGroup"), tools.ForButton("Eraser")));
        Assert.Equal(ToolKind.Lasso, tools.ForButton("Lasso"));

        tools.Choose(ToolKind.Select, notesHidden: false);
        tools.Return = ToolKind.Lasso;
        Assert.Equal(EscapeStep.CancelOperation, tools.Escape(operationActive: true, hasSelection: true));
        Assert.Equal(EscapeStep.ClearSelection, tools.Escape(false, true));
        Assert.Equal(EscapeStep.ReturnToTool, tools.Escape(false, false));
        Assert.Equal(ToolKind.Lasso, tools.TakeReturn());
        Assert.Null(tools.TakeReturn());
        Assert.Equal(EscapeStep.None, tools.Escape(false, false));
        tools.Choose(ToolKind.Pen, false);
        Assert.Equal(EscapeStep.SelectTool, tools.Escape(false, false));
        Assert.True(ToolSwitch.KeepsSelection(ToolKind.Lasso));
        Assert.False(ToolSwitch.KeepsSelection(ToolKind.Pen));
    }

    [Fact]
    public void ZoomStepsAndParsing()
    {
        Assert.Equal(1.25, ZoomSteps.Step(1, 1));
        Assert.Equal(0.75, ZoomSteps.Step(1, -1));
        Assert.Equal(8, ZoomSteps.Step(8, 1));
        Assert.Equal(0.1, ZoomSteps.Step(0.1, -1));
        Assert.Equal(1.5, ZoomSteps.Step(1.3, 1));
        Assert.True(ZoomSteps.TryParse(" 125 % ", CultureInfo.InvariantCulture, out double zoom));
        Assert.Equal(1.25, zoom);
        Assert.False(ZoomSteps.TryParse("0", CultureInfo.InvariantCulture, out _));
        Assert.Equal("150%", ZoomSteps.Format(1.5, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AnnotationPacketsRoundTripAndRejectGarbage()
    {
        using var doc = OpenDocument.CreateNote(NewPageSpec.Default);
        Draw(doc);
        var model = doc.Session.Current;
        var ink = model.Pages[0].Annotations[0];
        string? packet = AnnotationTransfer.Encode(model, model.Pages[0].Id, [ink.Id]);
        Assert.NotNull(packet);
        Assert.Null(AnnotationTransfer.Encode(model, model.Pages[0].Id, []));
        var copied = AnnotationTransfer.Decode(packet, _ => { });
        var back = Assert.IsType<InkStroke>(Assert.Single(copied.Annotations));
        Assert.Equal(((InkStroke)ink).Points.Length, back.Points.Length);
        Assert.All(back.Points.Zip(((InkStroke)ink).Points), p => Assert.True(Math.Abs(p.First.X - p.Second.X) + Math.Abs(p.First.Y - p.Second.Y) < 1e-3));
        Assert.Equal((ink.Id, ((InkStroke)ink).Color, ((InkStroke)ink).WidthPt), (back.Id, back.Color, back.WidthPt));
        Assert.Throws<InvalidDataException>(() => AnnotationTransfer.Decode(null, _ => { }));
        Assert.Throws<InvalidDataException>(() => AnnotationTransfer.Decode("{}", _ => { }));
    }

    [Fact]
    public void ImagesFitIntoEightyPercentOfThePage()
    {
        var page = Page.Create(TestModels.A4() with { WidthPt = 200, HeightPt = 100 });
        var resource = Resource.Create(Guid.NewGuid(), "image/png", [1]);
        var big = ImagePlacement.Place(resource, 1000, 1000, 72, page);
        Assert.Equal(new Rect2(0, 0, 80, 80), big.LocalBounds);
        Assert.Equal(Affine.Translation(60, 10), big.Transform);
        var small = ImagePlacement.Place(resource, 96, 48, 0, page, new Point2(50, 50));
        Assert.Equal(new Rect2(0, 0, 72, 36), small.LocalBounds);
        Assert.Equal(Affine.Translation(14, 32), small.Transform);
    }

    [Fact]
    public void ShapeStylesWriteBackToFillOrBorder()
    {
        var style = ShapeStyle.Default with { FillOpacity = 0, BorderEnabled = true };
        var red = new RgbColor(255, 0, 0);
        var filled = ToolStyles.WithStroke(style, ToolStyles.ShapeStroke(style, fill: true) with { Color = red }, fill: true);
        Assert.Equal((red, 1.0), (filled.FillColor, filled.FillOpacity)); // picking a color for a missing fill turns it on
        var noBorder = ToolStyles.WithStroke(style with { BorderEnabled = false, FillOpacity = 0 }, ToolStyles.ShapeStroke(style, false), fill: false);
        Assert.True(noBorder.BorderEnabled); // a shape keeps a visible fill or border
        Assert.Equal((false, true, false), ToolStyles.Mixed([new StrokeStyle(red, 1, 0), new StrokeStyle(red, 2, 0)]));
    }
}
