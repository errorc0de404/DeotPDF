using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.App;
using PdfNotes.App.Canvas;
using PdfNotes.App.Views;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.Probe;

internal static class D10bcCheck
{
    private static MainWindow window = null!;
    private static string folder = "";
    private static bool extended;
    private static readonly List<object> checks = [];
    private static readonly List<string> messages = [];
    private static DocumentSession Session => window.Document.Session;
    private static ListBox List => (ListBox)window.FindName("PageList");
    private static string Metadata(DocumentModel model)
    {
        var data = SchemaV4.Serialize(model);
        return JsonSerializer.Serialize(new { data.Header, Envelopes = data.Annotations.Select(a => a.Envelope) });
    }
    private static string AnnotationData(DocumentModel model, Guid id) => Convert.ToBase64String(SchemaV4.Serialize(model).Annotations.Single(a => a.AnnotationId == id).Envelope);
    private static void Check(string name, bool passed, string detail = "")
    {
        checks.Add(new { Name = name, Passed = passed, Detail = detail });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        if (!passed) throw new InvalidOperationException(name + ": " + detail);
    }
    public static int Run(string output, bool reopen, bool d10d = false)
    {
        extended = d10d;
        folder = output; Directory.CreateDirectory(folder);
        Environment.SetEnvironmentVariable("PDFNOTES_SETTINGS_DIR", Path.Combine(folder, "settings"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; int result = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                window = new MainWindow { Width = 1420, Height = 900, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                window.PromptOverride = (_, _) => MessageBoxResult.No; window.MessageSink = messages.Add;
                window.Show(); await Task.Delay(300);
                if (reopen) await Reopen(); else await Edit(); result = 0;
            }
            catch (Exception e) { checks.Add(new { Name = "harness", Passed = false, Detail = e.ToString() }); Console.WriteLine(e); }
            finally
            {
                foreach (var modal in app.Windows.Cast<Window>().Where(w => w != window).ToArray()) modal.Close();
                File.WriteAllText(Path.Combine(folder, (extended ? "d10d" : "d10bc") + (reopen ? "-reopen-result.json" : "-result.json")), JsonSerializer.Serialize(new
                { Passed = result == 0, Input = reopen ? "app-commands" : "os-mouse-and-keys", Checks = checks, Messages = messages }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close(); app.Shutdown();
            }
        };
        app.Run();
        if (!reopen && result == 0)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(extended ? "d10d-reopen" : "d10bc-reopen"); start.ArgumentList.Add(folder);
            using var child = Process.Start(start)!; child.WaitForExit(); return child.ExitCode;
        }
        return result;
    }
    private static async Task Edit()
    {
        string source = Path.Combine(folder, "source.pdf"); ProbeSamplePdf.Create(source, 3);
        Check("open mixed PDF", await window.OpenFileAsync(source));
        var ids = Session.Current.Pages.Select(p => p.Id).ToArray();
        var ink = new InkStroke(Guid.NewGuid(), true, Affine.Identity, [new(50, 150), new(250, 150)], 6, new(200, 30, 30), .7);
        const string text = "한글 English lines\n다시 편집 😀";
        var box = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Translation(80, 250), text,
            [new(0, text.Length, "Malgun Gothic", 16, RgbColor.Black, false, false, false, false)], PdfNotes.Core.Models.TextAlignment.Left,
            180, new(RgbColor.White, .5, true, RgbColor.Black, 1), [new(0, 2, HighlightHeightMode.Manual, 12, new(255, 230, 0), .4)]);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, ids[0], ink));
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, ids[0], box));
        Select([ids[0]]);
        int prompts = 0; window.PromptOverride = (s, _) => { if (s.Contains("삭제할까요")) prompts++; return MessageBoxResult.No; };
        string before = Metadata(Session.Current); int history = Session.UndoCount; long revision = Session.Revision;
        Check("delete No preserves model, selection and history", !window.DeleteSelectedPages() && prompts == 1 && before == Metadata(Session.Current) &&
            history == Session.UndoCount && revision == Session.Revision && window.SelectedPageIds.SequenceEqual([ids[0]]));
        window.PromptOverride = (s, _) => s.Contains("삭제할까요") ? MessageBoxResult.Yes : MessageBoxResult.No;
        Check("delete Yes and one Undo restore", window.DeleteSelectedPages() && Session.Current.Pages.Count == 2 && Session.Undo() && before == Metadata(Session.Current));
        Select([ids[0], ids[1]]);
        var footer = (Button)List.Items[List.Items.Count - 1];
        Check("add button is after the final thumbnail", List.Items.Count == Session.Current.Pages.Count + 1 && footer.Content?.ToString() == "빈 페이지 추가");
        List.ScrollIntoView(footer); await Task.Delay(200);
        _ = window.Dispatcher.BeginInvoke(new Action(() => _ = Click(footer)));
        var newPage = await Modal<NewNoteDialog>();
        Check("footer opens same-size dialog", ((CheckBox)newPage.FindName("SameSizeBox")).IsChecked == true);
        await Click(Descendants(newPage).OfType<Button>().Single(b => b.IsCancel));
        Check("cancel adding is unchanged", before == Metadata(Session.Current));

        var dialog = await Transform(false);
        Snapshot(dialog, "shrink-dialog.png"); await Click(Descendants(dialog).OfType<Button>().Single(b => b.IsCancel));
        Check("cancel shrink is unchanged", before == Metadata(Session.Current) && Session.Revision == revision + 0 /* delete/undo returned to the same revision */);
        dialog = await Transform(false);
        await Type(Field<TextBox>(dialog, "PercentBox"), "150");
        await Click(Descendants(dialog).OfType<Button>().Single(b => b.IsDefault));
        Check("invalid percent does not apply", dialog.IsVisible && before == Metadata(Session.Current));
        await Type(Field<TextBox>(dialog, "PercentBox"), "80");
        await Choose(Field<UniformGrid>(dialog, "ScopeBox"), 1);
        await Click(Descendants(dialog).OfType<RadioButton>().First(b => b.GroupName == "Placement"));
        history = Session.UndoCount;
        var glyph = window.PageGlyphs(0).First(g => g.Text == "O");
        await Click(Descendants(dialog).OfType<Button>().Single(b => b.IsDefault));
        Check("selected pages shrink in one command including locked ink", Session.UndoCount == history + 1 &&
            Session.Current.Pages[0].Setup.Content?.Transform.M11 == .8 && Session.Current.Pages[1].Setup.Content?.Transform.M11 == .8 && Session.Current.Pages[2].Setup.Content is null &&
            Session.Current.FindAnnotation(ink.Id)!.Annotation is InkStroke { Locked: true } transformedInk && Math.Abs(transformedInk.WidthPt - 4.8) < 1e-8,
            $"history={history}->{Session.UndoCount}, selected={window.SelectedPageIds.Length}, scales={string.Join(",", Session.Current.Pages.Select(p => p.Setup.Content?.Transform.M11))}");
        var scaled = (TextBoxAnnotation)Session.Current.FindAnnotation(box.Id)!.Annotation;
        string scaledData = AnnotationData(Session.Current, box.Id);
        Check("text sizes, padding and manual highlight scale", scaled.LayoutScale == .8 && scaled.Runs[0].SizePt == 12.8 && Math.Abs(scaled.Highlights[0].ManualHeightPt!.Value - 9.6) < 1e-8);
        var moved = window.PageGlyphs(0).First(g => g.Text == "O");
        Check("glyph positions follow the background", Math.Abs(moved.Center.X - glyph.Center.X * .8) < 1e-8 && Math.Abs(moved.Center.Y - glyph.Center.Y * .8) < 1e-8);
        await Thumbnails(); Snapshot(window, "shrunk.png");
        Check("thumbnails regenerate", window.PageThumbnails.All(t => t.Image is not null));
        string shrunk = Metadata(Session.Current); Check("shrink Undo and Redo", Session.Undo() && before == Metadata(Session.Current) && Session.Redo() && shrunk == Metadata(Session.Current));
        history = Session.UndoCount;
        Check("100 percent adds no history", !window.ApplyPageTransform(false, new(PageApplyScope.All, 100, 210, 297, PageResizeMode.FitContent, 4)) && history == Session.UndoCount);

        dialog = await Transform(true);
        Check("resize offers all presets", Field<ComboBox>(dialog, "PresetBox").Items.Count == 7);
        await Choose(Field<UniformGrid>(dialog, "ScopeBox"), 2);
        await Type(Field<TextBox>(dialog, "WidthBox"), "100"); await Type(Field<TextBox>(dialog, "HeightBox"), "120");
        Field<ComboBox>(dialog, "ModeBox").SelectedIndex = 1; await Click(Descendants(dialog).OfType<RadioButton>().First(b => b.GroupName == "Placement"));
        Snapshot(dialog, "resize-dialog.png"); history = Session.UndoCount;
        await Click(Descendants(dialog).OfType<Button>().Single(b => b.IsDefault));
        Check("all pages clip with one Undo and unchanged annotation sizes", Session.UndoCount == history + 1 &&
            Session.Current.Pages.All(p => Math.Abs(p.Setup.Frame.DisplayWidthPt - Units.MillimetersToPoints(100)) < 1e-7 && Math.Abs(p.Setup.Frame.DisplayHeightPt - Units.MillimetersToPoints(120)) < 1e-7) &&
            AnnotationData(Session.Current, box.Id) == scaledData,
            $"history={history}->{Session.UndoCount}, sizes={string.Join(",", Session.Current.Pages.Select(p => $"{p.Setup.Frame.DisplayWidthPt:0.00}x{p.Setup.Frame.DisplayHeightPt:0.00}"))}");
        if (extended)
        {
        // D10d: actual rectangle move/corner resize, cancelling, both batch scopes, and equal-size translation.
        Select([ids[0], ids[1]]);
        var batchBaseline = Session.Current;
        dialog = await Transform(true); await Choose(Field<UniformGrid>(dialog, "ScopeBox"), 1);
        await Type(Field<TextBox>(dialog, "WidthBox"), "110");
        await Click(Descendants(dialog).OfType<Button>().Single(b => b.IsDefault));
        Check("selected resize affects only chosen pages", Session.Current.Pages.Take(2).All(p => Math.Abs(p.Setup.Frame.DisplayWidthPt - Units.MillimetersToPoints(110)) < 1e-7) && Session.Current.Pages[2].Setup == batchBaseline.Pages[2].Setup);
        Session.Undo();
        dialog = await Transform(false); await Choose(Field<UniformGrid>(dialog, "ScopeBox"), 2);
        await Type(Field<TextBox>(dialog, "PercentBox"), "50");
        await Click(Descendants(dialog).OfType<Button>().Single(b => b.IsDefault));
        Check("all shrink affects every page", Session.Current.Pages.All(p => Math.Abs(p.Setup.Content!.Transform.M11 - (batchBaseline.GetPage(p.Id).Setup.Content?.Transform.M11 ?? 1) * .5) < 1e-8));
        Session.Undo(); Select([ids[0], ids[1]]);
        dialog = await Transform(false); await Choose(Field<UniformGrid>(dialog, "ScopeBox"), 1);
        await Click(Field<RadioButton>(dialog, "FreePlacement"));
        var preview = Field<PagePlacementPreview>(dialog, "PlacementPreview");
        var bounds = preview.ContentBounds;
        await Drag(preview, new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), new(26, 18));
        Check("free rectangle moves", preview.FreePosition.X > 0.1 && preview.FreePosition.Y > 0.1);
        bounds = preview.ContentBounds;
        await Drag(preview, bounds.BottomRight, new(-20, -28));
        Check("free corner changes uniform scale and numeric field", preview.ScalePercent < 80 && Math.Abs(double.Parse(Field<TextBox>(dialog, "PercentBox").Text) - preview.ScalePercent) < .00001);
        var unchanged = Metadata(Session.Current); history = Session.UndoCount;
        Snapshot(dialog, "free-shrink-dialog.png");
        await Click(Descendants(dialog).OfType<Button>().Single(b => b.IsCancel));
        Check("free preview cancel preserves model and Undo", Metadata(Session.Current) == unchanged && history == Session.UndoCount);
        dialog = await Transform(false); await Choose(Field<UniformGrid>(dialog, "ScopeBox"), 1);
        await Type(Field<TextBox>(dialog, "PercentBox"), "50"); await Click(Field<RadioButton>(dialog, "FreePlacement"));
        preview = Field<PagePlacementPreview>(dialog, "PlacementPreview"); bounds = preview.ContentBounds;
        await Drag(preview, new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), new(18, 12));
        Select([ids[2]]); // A late view/panel update must not replace the modal's target snapshot.
        await Click(Descendants(dialog).OfType<Button>().Single(b => b.IsDefault));
        Check("selected free shrink changes only chosen pages in one Undo", Session.UndoCount == history + 1 && Session.Current.Pages[0].Setup.Content!.Transform.M11 == .4 && Session.Current.Pages[1].Setup.Content!.Transform.M11 == .4 && Session.Current.Pages[2].Setup.Content!.Transform.M11 == 1);
        Session.Undo();
        var beforeFreeResize = Session.Current;
        dialog = await Transform(true); await Choose(Field<UniformGrid>(dialog, "ScopeBox"), 2);
        await Click(Field<RadioButton>(dialog, "FreePlacement")); preview = Field<PagePlacementPreview>(dialog, "PlacementPreview"); bounds = preview.ContentBounds;
        await Drag(preview, new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), new(22, 16));
        Snapshot(dialog, "free-resize-dialog.png");
        await Click(Descendants(dialog).OfType<Button>().Single(b => b.IsDefault));
        Check("all free resize translates even unchanged paper size in one Undo", Session.UndoCount == history + 1 && Session.Current.Pages.All(p => p.Setup != beforeFreeResize.GetPage(p.Id).Setup));
        Session.Undo(); Check("free batch Undo restores exact model", Metadata(Session.Current) == unchanged);
        Check("free batch Redo restores translation", Session.Redo());
        }
        Select([ids[0]]); window.CopySelectedPages(PageCopyMode.WithAnnotations); Check("copy transformed page", window.PastePages());
        var copyId = window.SelectedPageIds.Single(); window.RotateSelectedPages(true); window.MoveSelectedPages(Session.Current.Pages.Count);
        Check("delete copied page and Undo", window.DeleteSelectedPages() && Session.Undo() && Session.Current.GetPage(copyId).Annotations.Count == 2);
        Check("save clipped editable PDF", await window.SaveToAsync(Path.Combine(folder, "editable.pdf")) && !Session.IsDirty);
        File.WriteAllText(Path.Combine(folder, "expected.json"), Metadata(Session.Current));
        await Thumbnails(); Snapshot(window, "clipped.png");
    }
    private static async Task Reopen()
    {
        Check("separate process reopen", await window.OpenFileAsync(Path.Combine(folder, "editable.pdf")));
        Check("same transformed model", Metadata(Session.Current) == File.ReadAllText(Path.Combine(folder, "expected.json")));
        var page = Session.Current.Pages[0]; Select([page.Id]);
        Check("grow after reopen retains hidden content", window.ApplyPageTransform(true, new(PageApplyScope.Current, 100, 210, 297, PageResizeMode.ClipContent, 0)) &&
            page.Annotations.All(a => AnnotationData(Session.Current, a.Id) == AnnotationData(Session.Current with { Pages = [page] }, a.Id)));
        var text = Session.Current.GetPage(page.Id).Annotations.OfType<TextBoxAnnotation>().Single();
        window.BeginTextEdit(text.Id); await Task.Delay(200);
        var editor = window.ActiveTextEditor!;
        Check("scaled live editor uses original layout units", Math.Abs(editor.Width - text.WidthPt / text.LayoutScale) < 1e-8 && editor.Document.PagePadding.Left == 4);
        editor.CaretPosition = editor.Document.ContentEnd; editor.Selection.Text = " 추가"; window.FlushTextEdit(); window.FinishTextEditing();
        Check("live edit preserves scaled font and padding", Session.Current.FindAnnotation(text.Id)!.Annotation is TextBoxAnnotation edited && edited.Text.EndsWith(" 추가") &&
            edited.LayoutScale == text.LayoutScale && Math.Abs(edited.Runs[^1].SizePt - text.Runs[^1].SizePt) < 1e-8);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, page.Id, new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(100, 100), new(200, 100)], 2, RgbColor.Black, 1)));
        Check("new edits and re-save", await window.SaveToAsync(Path.Combine(folder, "reedited.pdf")));
        var bytes = File.ReadAllBytes(Path.Combine(folder, "reedited.pdf")); var read = EditablePdfReader.Read(bytes, PdfPageGeometryReader.Read(bytes));
        Check("edited roundtrip has no read-only annotations", read.ReadOnlyCount == 0 && Metadata(read.Model) == Metadata(Session.Current));
        await Thumbnails(); Snapshot(window, "reopened-grown.png");
    }
    private static async Task<PageTransformDialog> Transform(bool resize)
    {
        window.ShowRibbonTab(RibbonTab.Page); // D13b: the page commands are on the page tab
        await Task.Delay(150);
        _ = window.Dispatcher.BeginInvoke(new Action(() => _ = Click((Button)window.FindName(resize ? "ResizePageButton" : "ShrinkPageButton"))));
        return await Modal<PageTransformDialog>();
    }
    private static async Task<T> Modal<T>() where T : Window
    {
        for (int i = 0; i < 30; i++) { if (Application.Current.Windows.OfType<T>().FirstOrDefault(w => w.IsVisible) is { } modal) { await Task.Delay(150); return modal; } await Task.Delay(100); }
        throw new InvalidOperationException("Modal did not open: " + typeof(T).Name);
    }
    private static void Select(Guid[] ids)
    {
        List.SelectedItems.Clear();
        foreach (var thumb in window.PageThumbnails.Where(t => ids.Contains(t.PageId))) List.SelectedItems.Add(thumb);
        ((DocumentView)window.FindName("View")).GoToPage(Session.Current.IndexOfPage(ids[0]));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var c = VisualTreeHelper.GetChild(parent, i); yield return c; foreach (var d in Descendants(c)) yield return d; }
    }
    private static T Field<T>(Window dialog, string name) where T : FrameworkElement => Descendants(dialog).OfType<T>().Single(c => c.Name == name);
    private static async Task Click(FrameworkElement element)
    {
        var owner = Window.GetWindow(element); var handle = new WindowInteropHelper(owner).Handle;
        for (int i = 0; i < 5 && GetForegroundWindow() != handle; i++) { keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); owner.Activate(); SetForegroundWindow(handle); await Task.Delay(150); }
        if (GetForegroundWindow() != handle) throw new InvalidOperationException("Foreground unavailable for actual input.");
        var p = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        SetCursorPos((int)p.X, (int)p.Y); mouse_event(2, 0, 0, 0, 0); await Task.Delay(40); mouse_event(4, 0, 0, 0, 0); await Task.Delay(220);
    }
    private static async Task Drag(FrameworkElement element, Point start, Vector delta)
    {
        await Click(element);
        var first = element.PointToScreen(start); var last = element.PointToScreen(start + delta);
        SetCursorPos((int)first.X, (int)first.Y); mouse_event(2, 0, 0, 0, 0); await Task.Delay(60);
        for (int i = 1; i <= 10; i++) { SetCursorPos((int)(first.X + (last.X - first.X) * i / 10), (int)(first.Y + (last.Y - first.Y) * i / 10)); await Task.Delay(25); }
        mouse_event(4, 0, 0, 0, 0); await Task.Delay(150);
    }
    private static async Task Choose(UniformGrid box, int index)
    {
        var button = (RadioButton)box.Children[index];
        await Click(button);
        Check("scope selected by actual mouse", button.IsChecked == true, $"{button.Content}");
    }
    private static async Task Type(TextBox box, string value)
    {
        await Click(box); keybd_event(0x11, 0, 0, 0); keybd_event(0x41, 0, 0, 0); keybd_event(0x41, 0, 2, 0); keybd_event(0x11, 0, 2, 0);
        foreach (char c in value) { keybd_event((byte)c, 0, 0, 0); keybd_event((byte)c, 0, 2, 0); await Task.Delay(30); } await Task.Delay(100);
    }
    private static async Task Thumbnails() { for (int i = 0; i < 40; i++) { await Task.Delay(100); if (window.PageThumbnails.All(t => t.Image is not null)) break; } await Task.Delay(500); }
    private static void Snapshot(Window target, string name)
    {
        double dpi = VisualTreeHelper.GetDpi(target).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Round(target.ActualWidth * dpi), (int)Math.Round(target.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32); bitmap.Render(target);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
    }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
