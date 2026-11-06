using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Desktop.Views;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Desktop.Tests;

/// <summary>
/// Behaviors the A5 code review (기록/로드맵_P4_A5_코드리뷰.md) found to differ from the WPF app, each as the WPF app does
/// them: keys while the page list has the keyboard, typing into a box only after the text tool, the note card before a
/// save, one outline jump, bar fields, page turning in single page mode and with a held arrow, editing keeps the box
/// selected, a new box from the menu takes the text tool, image resolution, Backspace, scroll bars only when needed.
/// </summary>
public sealed class ReviewFixTests
{
    private static MainWindow Window(double width = 1200, double height = 1000)
    {
        var window = new MainWindow(new AppSettings(), _ => { }, Headless.TempFolder(), TestWindows.Dialogs) { Width = width, Height = height };
        window.Script.PromptOverride = (_, buttons) => buttons == AskButtons.YesNo ? Answer.Yes : Answer.No;
        window.Script.MessageSink = _ => { };
        window.EditorClipboard.InMemory = true;
        window.Show();
        window.DocumentCanvas.SetZoom(1);
        window.DocumentCanvas.GoToPage(0);
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(MainWindow w, string name) where T : Control =>
        w.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? w.FindControl<T>(name) ?? throw new InvalidOperationException(name);

    private static Point At(MainWindow w, Point2 page)
    {
        var dip = w.DocumentCanvas.PageTransform(0).PageToDip.Transform(page);
        return w.DocumentCanvas.TranslatePoint(new Point(dip.X, dip.Y), w)!.Value;
    }

    private static void Drag(MainWindow w, params Point2[] points)
    {
        w.MouseMove(At(w, points[0]));
        w.MouseDown(At(w, points[0]), MouseButton.Left);
        foreach (var p in points.Skip(1)) w.MouseMove(At(w, p));
        w.MouseUp(At(w, points[^1]), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(MainWindow w, Point2 p)
    {
        w.MouseMove(At(w, p));
        w.MouseDown(At(w, p), MouseButton.Left);
        w.MouseUp(At(w, p), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Key(MainWindow w, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        w.KeyPress(key, modifiers, PhysicalKey.None, null);
        w.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static IReadOnlyList<Annotation> Annotations(MainWindow w) => w.Document!.Session.Current.Pages[0].Annotations;

    [Fact]
    public void UndoWorksWithThePageListFocused() => Headless.Run(async () =>
    {
        string pdf = Path.Combine(Headless.TempFolder(), "p.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        var w = Window();
        Assert.True(await w.OpenFileAsync(pdf));
        Dispatcher.UIThread.RunJobs();
        w.PageListBox.SelectedIndex = 1;
        w.PageListBox.ContainerFromIndex(1)!.Focus(); // a row has the keyboard after a click
        Key(w, Avalonia.Input.Key.Delete); // the list's own key (asks first: yes)
        Assert.True(w.Document!.Session.Current.Pages.Count == 2, $"focus {w.FocusManager?.GetFocusedElement()?.GetType().Name}, selected {w.PageListBox.SelectedItems?.Count}");
        Key(w, Avalonia.Input.Key.Z, RawInputModifiers.Control); // the window's Undo still works there
        Assert.Equal(3, w.Document.Session.Current.Pages.Count);
        w.Close();
    });

    [Fact]
    public void TypingEditsABoxOnlyAfterTheTextToolAndNeverWithSpace() => Headless.Run(() =>
    {
        var w = Window();
        w.SetTool(ToolKind.Text);
        Click(w, new(100, 400));
        w.KeyTextInput("abc");
        w.FinishTextEditing();
        var box = Assert.IsType<TextBoxAnnotation>(Assert.Single(Annotations(w)));
        Thread.Sleep(700);
        Click(w, new Point2(box.Transform.OffsetX + 6, box.Transform.OffsetY + 8)); // the text tool selects it
        Assert.Equal([box.Id], w.Selection.SelectedIds);
        // A space with the box selected from the text tool is the pan hand, not a character.
        w.DocumentCanvas.Focus();
        w.KeyTextInput(" ");
        Assert.False(w.IsEditingText);
        // The selection tool chosen directly: typing does nothing to the box.
        w.SetTool(ToolKind.Select);
        w.Selection.Select(box.Id);
        w.DocumentCanvas.Focus();
        w.KeyTextInput("x");
        Assert.False(w.IsEditingText);
        Assert.Equal("abc", ((TextBoxAnnotation)Annotations(w)[0]).Text);
        // Backspace does not delete the selection (only Delete does, as in the WPF window).
        Key(w, Avalonia.Input.Key.Back);
        Assert.Single(Annotations(w));
        w.Close();
    });

    [Fact]
    public void EditingKeepsTheBoxSelectedAndABlankBoxReturnsToTheTextTool() => Headless.Run(() =>
    {
        var w = Window();
        w.SetTool(ToolKind.Text);
        Click(w, new(100, 400));
        w.KeyTextInput("x");
        w.FinishTextEditing();
        var box = (TextBoxAnnotation)Annotations(w)[0];
        Thread.Sleep(700);
        var inside = new Point2(box.Transform.OffsetX + 6, box.Transform.OffsetY + 8);
        Click(w, inside); // the text tool selects it
        Click(w, inside); // a second press edits it
        Assert.True(w.IsEditingText);
        Assert.Equal([box.Id], w.Selection.SelectedIds);
        w.ActiveTextEditor!.Model!.SelectAll();
        w.ActiveTextEditor.Model.Key(PdfNotes.TextEditor.EditorKey.Delete);
        w.FinishTextEditing(); // blank: the box goes, and the visit of the selection tool ends
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(Annotations(w));
        Assert.Equal(ToolKind.Text, w.CurrentTool);
        w.Close();
    });

    [Fact]
    public void ATextBoxFromTheQuickMenuTakesTheTextTool() => Headless.Run(() =>
    {
        var w = Window();
        w.SetTool(ToolKind.Pen);
        w.BeginNewTextAtClick(0, new(200, 300));
        Assert.True(w.IsEditingText);
        Assert.Equal(ToolKind.Text, w.CurrentTool);
        w.Close();
    });

    [Fact]
    public void AnOpenNoteIsCommittedBeforeASave() => Headless.Run(async () =>
    {
        var w = Window();
        string target = Path.Combine(Headless.TempFolder(), "note.pdf");
        w.Script.SaveDialogOverride = _ => target;
        w.SetTool(ToolKind.Note);
        Click(w, new(200, 200));
        w.NoteTextBox!.Text = "저장 전 메모";
        Key(w, Avalonia.Input.Key.S, RawInputModifiers.Control); // the note box keeps the keyboard: no focus change closes it
        Headless.Pump(() => File.Exists(target) && !w.ViewModel.IsBusy);
        Assert.Null(w.OpenNoteAnnotation);
        Assert.Contains(Annotations(w), a => a is StickyNote { Text: "저장 전 메모" });
        await Task.CompletedTask;
        w.Close();
    });

    [Fact]
    public void OneOutlineClickIsOneJump() => Headless.Run(async () =>
    {
        string pdf = Path.Combine(Headless.TempFolder(), "l.pdf");
        ProbeSamplePdf.CreateLinked(pdf, 5);
        var w = Window();
        Assert.True(await w.OpenFileAsync(pdf));
        w.SetOutlineVisible(true);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        var header = w.OutlineView.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "2장"); // the item's title
        var center = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), w)!.Value;
        w.MouseDown(center, MouseButton.Left);
        w.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, w.DocumentCanvas.CurrentPageIndex);
        Assert.True(w.ViewModel.Navigation.GoBack());
        Assert.False(w.ViewModel.Navigation.CanGoBack); // one jump remembered, not two
        w.Close();
    });

    [Fact]
    public void EscInABarFieldKeepsTheValueAndEnterAppliesOnce() => Headless.Run(() =>
    {
        var w = Window();
        w.SetTool(ToolKind.Pen);
        Drag(w, new(100, 100), new(160, 120), new(220, 140));
        var ink = Annotations(w)[0];
        w.SetTool(ToolKind.Select);
        w.Selection.Select(ink.Id);
        Dispatcher.UIThread.RunJobs();
        var box = Find<TextBox>(w, "WidthBox");
        double before = ((InkStroke)Annotations(w)[0]).WidthPt;
        int steps = w.Document!.Session.UndoCount;
        box.Focus();
        box.Text = "9";
        Key(w, Avalonia.Input.Key.Escape);
        Assert.Equal(before, ((InkStroke)Annotations(w)[0]).WidthPt, 6);
        Assert.Equal(steps, w.Document.Session.UndoCount);
        box.Focus();
        box.Text = "2.25";
        Key(w, Avalonia.Input.Key.Enter);
        Assert.Equal(2.25, ((InkStroke)Annotations(w)[0]).WidthPt, 6); // not rounded by a second apply
        Assert.Equal(steps + 1, w.Document.Session.UndoCount);
        w.Close();
    });

    [Fact]
    public void SinglePageModeTurnsPagesWithTheWheelAndAHeldArrowTurnsEvery300ms() => Headless.Run(async () =>
    {
        string pdf = Path.Combine(Headless.TempFolder(), "s.pdf");
        ProbeSamplePdf.Create(pdf, 4);
        var w = Window(1200, 900);
        Assert.True(await w.OpenFileAsync(pdf));
        var canvas = w.DocumentCanvas;
        canvas.SetMode(ViewMode.SinglePage);
        canvas.SetZoom(1.5);
        canvas.GoToPage(0);
        Dispatcher.UIThread.RunJobs();
        var middle = canvas.TranslatePoint(new Point(300, 300), w)!.Value;
        for (int i = 0; i < 40 && canvas.CurrentPageIndex == 0; i++) w.MouseWheel(middle, new Vector(0, -1));
        Assert.True(canvas.CurrentPageIndex == 1, $"offset {canvas.Offset} extent {canvas.ExtentHeight} viewport {canvas.ViewportHeight} mode {canvas.Mode}"); // down at the page's bottom: the next page
        w.MouseWheel(middle, new Vector(0, -1)); // a little into the page
        w.MouseWheel(middle, new Vector(0, 1));
        Assert.Equal(1, canvas.CurrentPageIndex); // up inside the page first
        w.MouseWheel(middle, new Vector(0, 1));
        Assert.Equal(0, canvas.CurrentPageIndex); // at its top: the previous page, from its bottom
        Assert.True(canvas.Offset.Y > 0);
        canvas.SetMode(ViewMode.Continuous);
        canvas.SetZoom(0.5); // narrower than the view: ←/→ turn pages at once
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        canvas.GoToPage(0);
        Assert.True(canvas.ArrowSideways(1, repeat: false, now: 0));
        Assert.Equal(1, canvas.CurrentPageIndex);
        Assert.False(canvas.ArrowSideways(1, repeat: true, now: 100)); // held: not yet
        Assert.True(canvas.ArrowSideways(1, repeat: true, now: 350));
        Assert.Equal(2, canvas.CurrentPageIndex);
        w.Close();
    });

    [Fact]
    public void ScrollBarsAppearOnlyWhenThePagesDoNotFit() => Headless.Run(() =>
    {
        var w = Window();
        var canvas = w.DocumentCanvas;
        var bars = canvas.Children.OfType<Avalonia.Controls.Primitives.ScrollBar>().ToList();
        canvas.SetMode(ViewMode.SinglePage);
        canvas.FitPage();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.All(bars, b => Assert.False(b.IsVisible));
        canvas.SetZoom(3);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.All(bars, b => Assert.True(b.IsVisible));
        w.Close();
    });

    [Fact]
    public void AnImageKeepsTheResolutionStoredInItsFile() => Headless.Run(() =>
    {
        using var bitmap = new SkiaSharp.SKBitmap(300, 150);
        bitmap.Erase(SkiaSharp.SKColors.SteelBlue);
        using var encoded = SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        byte[] plain = encoded.ToArray();
        // The same PNG with a pHYs chunk of 300 dpi (11811 pixels per metre) after IHDR.
        var phys = new byte[21];
        phys[3] = 9;
        "pHYs"u8.CopyTo(phys.AsSpan(4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(8), 11811);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(12), 11811);
        phys[16] = 1;
        byte[] dense = [.. plain.AsSpan(0, 33), .. phys, .. plain.AsSpan(33)];
        Assert.Equal(96, ImageFiles.Dpi(plain), 3);
        Assert.Equal(300, ImageFiles.Dpi(dense), 0);
        var w = Window();
        Assert.True(w.InsertImage(plain));
        Assert.True(w.InsertImage(dense));
        var images = Annotations(w).OfType<ImageAnnotation>().ToArray();
        double Width(ImageAnnotation i) => AnnotationGeometry.Bounds([i]).Width;
        Assert.Equal(Width(images[0]) * 96 / 300, Width(images[1]), 1);
        Assert.Throws<InvalidDataException>(() => ImageFiles.Check([.. plain.AsSpan(0, 40)])); // a cut-off file is refused
        w.Close();
    });

    // ----- second review (2026-11-04) -----

    [Fact]
    public void UndoWhileEditingGoesBackAWordNotTheWholeBox() => Headless.Run(() =>
    {
        var w = Window();
        w.SetTool(ToolKind.Text);
        Click(w, new(100, 400));
        foreach (string piece in new[] { "hello", " ", "world" }) { w.KeyTextInput(piece); Dispatcher.UIThread.RunJobs(); }
        w.Undo(); // the quick button: the editor's own step (spec 13.2), the box and the edit stay
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.IsEditingText);
        string text = ((TextBoxAnnotation)Assert.Single(Annotations(w))).Text;
        Assert.StartsWith("hello", text);
        Assert.DoesNotContain("world", text);
        w.Redo();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("hello world", ((TextBoxAnnotation)Annotations(w)[0]).Text);
        w.Close();
    });

    [Fact]
    public void AFirstSyllableLeftForAFieldStaysInTheBoxAndTheFieldTypes() => Headless.Run(() =>
    {
        var w = Window();
        w.SetTool(ToolKind.Text);
        Click(w, new(100, 400));
        w.KeyTextInput("가");
        w.FinishTextEditing();
        var box = Assert.IsType<TextBoxAnnotation>(Assert.Single(Annotations(w)));
        Thread.Sleep(700);
        Click(w, new Point2(box.Transform.OffsetX + 10, box.Transform.OffsetY + 8));
        w.DocumentCanvas.Focus();
        Dispatcher.UIThread.RunJobs();
        w.DocumentCanvas.ProbePreedit("ㅎ");
        Assert.True(w.IsEditingText);
        var size = Find<TextBox>(w, "TextSizeBox");
        size.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("가ㅎ", ((TextBoxAnnotation)Annotations(w)[0]).Text); // committed as it was shown
        size.Text = "";
        w.KeyTextInput("2");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("2", size.Text); // the field's own typing, not the box's
        Assert.True(size.IsFocused);
        w.Close();
    });

    [Fact]
    public void LayersOverThePagesReachTheEdgeWithoutScrollBars() => Headless.Run(() =>
    {
        var w = Window();
        w.DocumentCanvas.SetZoom(0.2); // the whole page fits: no bars
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new Thickness(0), Find<Avalonia.Controls.Canvas>(w, "TextLayer").Margin);
        w.DocumentCanvas.SetZoom(3);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(12, Find<Avalonia.Controls.Canvas>(w, "NoteLayer").Margin.Right);
        w.Close();
    });
}
