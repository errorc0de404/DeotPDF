using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Platform;

namespace PdfNotes.Tests.App;

/// <summary>P1 V7: tool styles, the style bar state and its commands on the tool and on selected annotations.</summary>
public sealed class ToolStylesViewModelTests : IDisposable
{
    private readonly Shell shell = new();
    private readonly AppSettings settings = new();
    private readonly FakeAppShell appShell = new();
    private readonly MainViewModel model;
    private readonly SelectTool select;
    private int saves, shown;
    private ToolStylesViewModel Styles => model.ToolStyles;
    private DocumentSession Session => model.Document!.Session;

    public ToolStylesViewModelTests()
    {
        model = new MainViewModel(shell, shell, settings, "DeotPDF", _ => saves++, TestFolders.New(), new ManualTime(), shell: appShell);
        model.Workflow.Replace(OpenDocument.CreateNote(NewPageSpec.Default));
        select = new SelectTool(new FakeCanvas(), () => model.Document?.Session);
        Styles.Attach(select);
        Styles.Changed += (_, _) => shown++;
    }

    public void Dispose()
    {
        model.Workflow.Detach()?.Dispose();
        foreach (var d in shell.Released) d.Dispose();
    }

    private sealed class FakeCanvas : ICanvasView
    {
        public PageAnchor? HitTestPage(Point2 viewportPoint) => null;
        public ViewTransform PageTransform(int index) => throw new NotSupportedException();
        public double DpiScale => 1;
        public Point2 Offset => default;
        public void SetHorizontalOffset(double value) { }
        public void SetVerticalOffset(double value) { }
        public void SetPreview(int pageIndex, ICanvasPreview? preview, bool mark = false) { }
        public void RefreshPreview() { }
        public void RefreshOverlays() { }
        public void RefreshOverlay(int pageIndex) { }
    }

    private sealed class Shell : IMainView, IEditorDialogs
    {
        public readonly List<OpenDocument> Released = [];
        public void FinishTextEditing() { }
        public void CancelTool() { }
        public void DocumentLeaving(OpenDocument? previous) { }
        public void DocumentReplaced(OpenDocument? previous, OpenDocument next) { if (previous is not null) Released.Add(previous); }
        public void DocumentChanged() { }
        public void WaitForFirstPages() { }
        public void Opened(string path) { }
        public void Saved(string path) { }
        public bool ShowOpenElsewhere(string path) => false;
        public void CommandsChanged() { }
        public void OnUiThread(Action action) => action();
        public IDocumentViewport Viewport { get; } = new FakeViewport();
        public IPageListView Pages { get; } = new FakePageList();
        public IWindowCoordinator Window { get; } = new FakeWindow();
        public Answer Ask(string text, AskButtons buttons, NoticeKind kind) => Answer.No;
        public void Tell(string text, NoticeKind kind) { }
        public string? ChooseSavePath(SavePathRequest request) => null;
    }

    private T Add<T>(T annotation) where T : Annotation
    {
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, annotation));
        return annotation;
    }

    private static InkStroke Ink(RgbColor color, double width) => new(Guid.NewGuid(), false, Affine.Identity, [new(10, 10), new(80, 60)], width, color, 1);

    private void Use(ToolKind kind) => model.Tools.Choose(kind, notesHidden: false);

    [Fact]
    public void ToolChangesAreRememberedAndUserColorsCollected()
    {
        Use(ToolKind.Pen);
        Assert.Equal((Styles.Pen, AnnotationKind.Ink), Styles.StyleTarget);
        var odd = new RgbColor(0x12, 0x34, 0x56);
        Styles.SetColor(odd);
        Assert.Equal(odd, Styles.Pen.Color);
        Assert.Equal(odd.ToHex(), settings.Pen.Color);
        Assert.Equal([odd], Styles.UserColors); // outside the basic palette
        Styles.SetColor(ColorPalette.Basic[0]);
        Assert.Single(Styles.UserColors);
        Assert.Equal(2, saves);
        Assert.Equal(2, shown);

        Styles.SetWidth(3.5, continuous: true); // a slider drag: stored when committed
        Assert.Equal(3.5, Styles.Pen.WidthPt);
        Assert.Equal(2, saves);
        Styles.Commit();
        Assert.Equal(3, saves);

        Styles.RemoveUserColor(odd);
        Assert.Empty(Styles.UserColors);
        Assert.Empty(settings.RecentColors);

        Use(ToolKind.FreeHighlight);
        Styles.SetMarkKind(TextMarkupKind.Underline);
        Assert.Equal(AnnotationKind.TextMarkup, Styles.StyleTarget?.Kind);
        Styles.SetTransparency(40);
        Assert.Equal(40, Styles.MarkupStyle.TransparencyPercent, 6);
        Assert.NotEqual(40, Styles.Highlighter.TransparencyPercent, 6); // each kind remembers its own
        var favorite = Styles.CurrentFavorite();
        Assert.Equal((ToolKind.FreeHighlight, TextMarkupKind.Underline), (favorite.Tool, favorite.MarkKind));

        Use(ToolKind.Pen);
        Styles.UseFavorite(favorite);
        Assert.Equal(TextMarkupKind.Underline, Styles.MarkKind);
        Assert.Equal(TextMarkupKind.Underline, settings.MarkKind);
    }

    [Fact]
    public void TheBarEditsTheSelectionAsOneUndoUnitAndShowsMixedValues()
    {
        Use(ToolKind.Select);
        Assert.Null(Styles.StyleTarget);
        Assert.Null(Styles.Bar.Target);

        var a = Add(Ink(RgbColor.Black, 2));
        var b = Add(Ink(new(0xD0, 0, 0), 4));
        select.SetSelection([a.Id, b.Id]);
        Assert.Equal(2, Styles.StyleSelection.Length);
        Assert.Equal((true, true, false), Styles.StyleMixed);
        Assert.Equal((true, true, false), Styles.Bar.Mixed);

        int undo = Session.UndoCount;
        Styles.SetWidth(6);
        Assert.Equal(undo + 1, Session.UndoCount);
        Assert.All(Styles.StyleSelection, s => Assert.Equal(6, ((InkStroke)s).WidthPt));
        Assert.False(Styles.StyleMixed.Width);

        Styles.SetTransparency(20, continuous: true); // slider steps merge into one unit
        Styles.SetTransparency(30, continuous: true);
        Styles.Commit();
        Assert.Equal(undo + 2, Session.UndoCount);

        var note = Add(new StickyNote(Guid.NewGuid(), false, Affine.Identity, "", new(0xFF, 0xD5, 0x4F)));
        select.SetSelection([note.Id]);
        Assert.False(Styles.Bar.TransparencyEnabled || Styles.Bar.WidthEnabled); // a note has a color only
    }

    [Fact]
    public void LineAndShapeOptionsFollowTheToolAndTheSelection()
    {
        Use(ToolKind.Curve);
        Assert.True(Styles.Bar.LineOptionsShown);
        Assert.Equal(LineShapeChoice.CurveTool, Styles.LineBar.Shapes);
        Styles.SetLineShape(2);
        Assert.Equal(2, Styles.LineOptions.CurveControls);
        Styles.SetLineArrows(end: true);
        Assert.True(Styles.LineOptions.EndArrow && Styles.LineBar.EndArrow);

        Use(ToolKind.Shape);
        Assert.True(Styles.Bar.ShapeOptionsShown && !Styles.Bar.LineOptionsShown);
        Styles.SetShapeBorder(DashStyle.Dash);
        Assert.Equal(1 + (int)DashStyle.Dash, Styles.ShapeBar.BorderIndex);
        Assert.True(settings.Shape.ToStyle().BorderEnabled);
        Styles.SetShapeColorTarget(false);
        Assert.False(Styles.ShapeBar.FillTarget);
        Assert.Equal(Styles.Shape.BorderColor, Styles.StyleTarget?.Style.Color);

        Use(ToolKind.AreaEraser);
        Assert.True(Styles.Bar.Eraser);
        Assert.True(Styles.SetEraserSize(500));
        Assert.Equal(settings.AreaEraserPt, Styles.EraserSize);
        Assert.True(Styles.EraserSize <= 100);
        Use(ToolKind.Pen);
        Assert.False(Styles.SetEraserSize(10));
    }

    [Fact]
    public void SetToolPutsTheToolInPlaceAndTheButtonsFollow()
    {
        var bar = model.ToolBar;
        var changed = new List<string?>();
        var used = new List<ToolKind>(); // what the view puts on the canvas
        bar.PropertyChanged += (_, e) => { changed.Add(e.PropertyName); if (e.PropertyName == nameof(ToolBarViewModel.Current)) used.Add(bar.Current); };
        model.Ribbon.Current = RibbonTab.View;
        int before = shown;

        bar.SetTool(ToolKind.Curve);
        Assert.Equal(ToolKind.Curve, bar.Current);
        Assert.Equal([ToolKind.Curve], used);
        Assert.True(bar.Shape.IsChecked);
        Assert.False(bar.Pen.IsChecked);
        Assert.Contains(nameof(ToolBarViewModel.Buttons), changed);
        Assert.True(bar.Buttons.ShapeGroup);
        Assert.Equal(ToolKind.Curve, settings.ShapeGroupTool); // the shape button selects it next time
        Assert.True(shown > before); // the style bar follows

        bar.Press("ShapeGroup");
        Assert.Single(used); // already the shape group's tool
        bar.Press("Eraser");
        Assert.Equal(settings.Eraser, bar.Current);
        Assert.True(bar.Buttons.EraserOn);
        Assert.True(bar.Eraser.IsChecked);

        bar.SetShapeKind(ShapeKind.Ellipse);
        Assert.Equal((ToolKind.Shape, ShapeKind.Ellipse), (bar.Current, bar.Buttons.ShapeKind));
        Assert.Throws<ArgumentOutOfRangeException>(() => bar.SetShapeKind(ShapeKind.Polyline));

        model.Display.NotesHidden = true;
        bar.SetTool(ToolKind.Select);
        Assert.Empty(appShell.NotesHidden); // the selection tool keeps the notes hidden
        bar.SetTool(ToolKind.Pen);
        Assert.Equal([false], appShell.NotesHidden);
    }
}
