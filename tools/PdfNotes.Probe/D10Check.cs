using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.App;
using PdfNotes.App.Canvas;
using PdfNotes.App.Tools;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.Probe;

/// <summary>
/// D10a real-app check: page panel (visible by default, toggle, thumbnails), click to go/activate, Ctrl/Shift selection,
/// drag reorder, Ctrl+C/V/Delete in the panel, page-only copy, blank page insert with the reference size, rotation,
/// last-page protection, Undo, save → separate-process reopen. <c>logic</c> calls the window's page commands directly.
/// </summary>
internal static class D10Check
{
    private static MainWindow window = null!;
    private static string folder = "";
    private static bool logic;
    private static readonly List<object> checks = [];
    private static readonly List<string> messages = [];
    private static DocumentSession Session => window.Document.Session;
    private static DocumentView View => (DocumentView)window.FindName("View");
    private static ListBox List => (ListBox)window.FindName("PageList");
    private static Guid[] Order => [.. Session.Current.Pages.Select(p => p.Id)];

    private static string Metadata(DocumentModel model)
    {
        var data = SchemaV3.Serialize(model);
        return JsonSerializer.Serialize(new { data.Header, Envelopes = data.Annotations.Select(a => a.Envelope) });
    }

    private static void Check(string name, bool passed, string detail = "")
    {
        checks.Add(new { Name = name, Passed = passed, Detail = detail });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        if (!passed) throw new InvalidOperationException(name + ": " + detail);
    }

    public static int Run(string output, bool reopen, bool logicOnly)
    {
        folder = output; logic = logicOnly;
        Environment.SetEnvironmentVariable("PDFNOTES_SETTINGS_DIR", Path.Combine(folder, "settings"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                window = new MainWindow { Width = 1420, Height = 900, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                window.MessageSink = messages.Add; window.PromptOverride = (text, _) => text.Contains("삭제할까요") ? MessageBoxResult.Yes : MessageBoxResult.No;
                window.Show(); await Settle();
                if (reopen) await Reopen(); else await Edit();
                result = 0;
            }
            catch (Exception e) { checks.Add(new { Name = "harness", Passed = false, Detail = e.ToString() }); Console.WriteLine(e); }
            finally
            {
                string name = reopen ? "d10-reopen-result.json" : logic ? "d10-logic-result.json" : "d10-result.json";
                File.WriteAllText(Path.Combine(folder, name), JsonSerializer.Serialize(new { Passed = result == 0, Input = reopen || logic ? "tool-calls" : "os-mouse", Checks = checks, Messages = messages },
                    new JsonSerializerOptions { WriteIndented = true }));
                window.PromptOverride = (_, _) => MessageBoxResult.No; window.Close(); app.Shutdown();
            }
        };
        app.Run();
        if (!reopen && result == 0)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("d10-reopen"); start.ArgumentList.Add(folder);
            using var child = Process.Start(start)!; child.WaitForExit(); result = child.ExitCode;
        }
        return result;
    }

    private static async Task WaitThumbnails()
    {
        for (int i = 0; i < 40 && window.PageThumbnails.Any(t => t.Image is null); i++) await Task.Delay(100);
    }

    private static async Task Edit()
    {
        Check("the app starts with the pen (eraser mode buttons do not switch tools)", window.CurrentTool == ToolKind.Pen &&
            ((ToggleButton)window.FindName("PenToolButton")).IsChecked == true, $"{window.CurrentTool}");
        var panel = (Border)window.FindName("PagePanel");
        Check("page panel is shown by default", panel.Visibility == Visibility.Visible && ((ToggleButton)window.FindName("PagePanelButton")).IsChecked == true);
        string pdf = Path.Combine(folder, "source.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        Check("open", await window.OpenFileAsync(pdf));
        await WaitThumbnails();
        Check("one thumbnail per page, in order", window.PageThumbnails.Select(t => t.PageId).SequenceEqual(Order) &&
            window.PageThumbnails.Select(t => t.Number).SequenceEqual([1, 2, 3]) && window.PageThumbnails.All(t => t.Image is not null),
            string.Join(",", window.PageThumbnails.Select(t => $"{t.Number}:{t.ImageWidth:0}x{t.ImageHeight:0}")));
        Check("a turned page has a turned thumbnail", window.PageThumbnails[1].ImageWidth > window.PageThumbnails[1].ImageHeight);
        var ids = Order;
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, ids[0], new InkStroke(Guid.NewGuid(), false, Affine.Identity,
            [new(100, 300), new(300, 300)], 6, new(220, 30, 30), 1)));
        await Settle(); await WaitThumbnails();

        // ----- activate and select -----
        if (!logic) await Foreground();
        await ClickRow(2);
        Check("click goes to the page and makes it active", View.CurrentPageIndex == 2 && window.ActivePageIndex == 2 && window.PageThumbnails[2].IsActive);
        await ClickRow(0, control: true);
        Check("Ctrl+click adds a page to the selection", window.SelectedPageIds.SequenceEqual([ids[0], ids[2]]), $"{window.SelectedPageIds.Length}");
        await ClickRow(0);
        await ClickRow(2, shift: true);
        Check("Shift+click selects a range", window.SelectedPageIds.SequenceEqual(ids), $"{window.SelectedPageIds.Length}");

        // ----- drag reorder -----
        await ClickRow(2);
        int undo = Session.UndoCount;
        await DragRow(2, 0);
        Check("dragging a thumbnail moves the page", Order.SequenceEqual([ids[2], ids[0], ids[1]]) && Session.UndoCount == undo + 1 && Session.UndoLabel == "undo.movePages",
            string.Join(",", Order.Select(id => Array.IndexOf(ids, id))) + $" undo +{Session.UndoCount - undo} {Session.UndoLabel}, messages {string.Join("|", messages)}");
        Check("panel follows the new order", window.PageThumbnails.Select(t => t.PageId).SequenceEqual(Order));
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("move undo", Order.SequenceEqual(ids));

        // ----- copy / paste / delete with the panel focused -----
        await ClickRow(0);
        await Key(0x43, control: true); // Ctrl+C: page + annotations by default
        await Key(0x56, control: true); // Ctrl+V: after the active page
        var copy = Session.Current.Pages[1];
        Check("Ctrl+C/V copies the page with its annotations after the active page", Session.Current.Pages.Count == 4 && !ids.Contains(copy.Id) &&
            copy.Setup == Session.Current.Pages[0].Setup && copy.Annotations.Count == 1 && copy.Annotations[0].Id != Session.Current.Pages[0].Annotations[0].Id &&
            window.SelectedPageIds.SequenceEqual([copy.Id]));
        window.CopySelectedPages(PageCopyMode.PageOnly);
        Check("page-only copy is on the page clipboard (for this one copy: Ctrl+C copies notes again, spec 11.4, D14g)", window.HasPageClipboard);
        await ClickRow(3);
        await Key(0x56, control: true);
        var pageOnly = Session.Current.Pages[4];
        Check("page-only paste has no app annotations", Session.Current.Pages.Count == 5 && pageOnly.Annotations.Count == 0 && Session.UndoLabel == "undo.pastePagesOnly");
        await ClickRow(4);
        await Key(0x2E); // Delete
        Check("Delete removes the selected page", Session.Current.Pages.Count == 4 && Session.UndoLabel == "undo.deletePages");
        List.Focus(); List.SelectAll(); await Settle();
        messages.Clear();
        await Key(0x2E);
        Check("deleting every page is refused as a whole", Session.Current.Pages.Count == 4 && messages.Any(m => m.Contains("마지막")), string.Join("|", messages));

        // ----- rotate, blank page with the reference size -----
        await ClickRow(0);
        Check("rotate right", window.RotateSelectedPages(true) && Session.Current.Pages[0].Setup.Rotation == 90);
        await Settle(); await WaitThumbnails(); await Task.Delay(400);
        Check("the thumbnail turns with the page", window.PageThumbnails[0].ImageWidth > window.PageThumbnails[0].ImageHeight);
        var reference = Session.Current.Pages[0].Setup;
        var (w, h) = PageEdits.DisplayedSize(reference);
        Check("insert a blank page after the active one", window.AddBlankPage(true, new NewPageSpec(Units.PointsToMillimeters(w), Units.PointsToMillimeters(h), PageBackground.Grid)));
        var blank = Session.Current.Pages[1].Setup;
        Check("same size uses the displayed size of the turned reference", blank.Rotation == 0 && Math.Abs(blank.WidthPt - w) < 1e-6 && Math.Abs(blank.HeightPt - h) < 1e-6 &&
            blank.Background.Kind == PageBackgroundKind.Grid, $"{blank.WidthPt:0.##}x{blank.HeightPt:0.##} vs {w:0.##}x{h:0.##}");
        Check("add a blank page at the end", window.AddBlankPage(false, NewPageSpec.Default) && Session.Current.Pages[^1].Setup.Source.Kind == PageSourceKind.Generated);
        await Settle(); await WaitThumbnails();
        Check("thumbnails for new pages", window.PageThumbnails.Count == Session.Current.Pages.Count && window.PageThumbnails.All(t => t.Image is not null));

        // ----- the document view follows -----
        View.GoToPage(3); await Task.Delay(400);
        Check("scrolling the document moves the active page", window.ActivePageIndex == 3 && window.PageThumbnails[3].IsActive);

        var panelButton = (ToggleButton)window.FindName("PagePanelButton");
        panelButton.IsChecked = false; panelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Settle();
        Check("toggle hides the panel", panel.Visibility == Visibility.Collapsed);
        panelButton.IsChecked = true; panelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Settle();
        Check("toggle shows it again", panel.Visibility == Visibility.Visible);

        string path = Path.Combine(folder, "editable.pdf");
        Check("save", await window.SaveToAsync(path) && !Session.IsDirty);
        File.WriteAllText(Path.Combine(folder, "expected.json"), Metadata(Session.Current));
        View.GoToPage(0); await Settle(); await WaitThumbnails();
        Snapshot(logic ? "d10-logic.png" : "d10.png");
    }

    private static async Task Reopen()
    {
        Check("open", await window.OpenFileAsync(Path.Combine(folder, "editable.pdf")));
        Check("restart: same pages, order, rotation and annotations", Metadata(Session.Current) == File.ReadAllText(Path.Combine(folder, "expected.json")));
        await WaitThumbnails();
        Check("reopened thumbnails", window.PageThumbnails.Count == Session.Current.Pages.Count && window.PageThumbnails.All(t => t.Image is not null));
        var copies = Session.Current.Pages.Where(p => p.Annotations.Count == 1).ToArray();
        Check("the page and its copy both carry editable ink", copies.Length == 2 && copies[0].Annotations[0].Id != copies[1].Annotations[0].Id);
        Check("copy and paste again after reopening", window.CopySelectedPages(PageCopyMode.WithAnnotations) && window.PastePages());
        string path = Path.Combine(folder, "reedited.pdf");
        Check("save again", await window.SaveToAsync(path));
        var bytes = File.ReadAllBytes(path);
        var read = EditablePdfReader.Read(bytes, PdfPageGeometryReader.Read(bytes));
        Check("re-edit round trip", read.ReadOnlyCount == 0 && Metadata(read.Model) == Metadata(Session.Current), $"{read.Model.Pages.Count} pages");
    }

    // ----- input -----

    private static ListBoxItem Row(int index)
    {
        List.ScrollIntoView(List.Items[index]); List.UpdateLayout();
        return (ListBoxItem)List.ItemContainerGenerator.ContainerFromIndex(index);
    }

    private static async Task ClickRow(int index, bool control = false, bool shift = false)
    {
        var row = Row(index);
        if (logic)
        {
            if (control) { if (row.IsSelected) List.SelectedItems.Remove(row.DataContext); else List.SelectedItems.Add(row.DataContext); }
            else if (shift) { int from = List.SelectedIndex; List.SelectedItems.Clear(); for (int i = Math.Min(from, index); i <= Math.Max(from, index); i++) List.SelectedItems.Add(List.Items[i]); }
            else List.SelectedItem = row.DataContext;
            List.Focus(); await Settle(); return;
        }
        await EnsureForeground();
        var p = row.PointToScreen(new Point(row.ActualWidth / 2, row.ActualHeight / 3));
        SetCursorPos((int)p.X, (int)p.Y); await Task.Delay(60);
        if (control) keybd_event(0x11, 0, 0, 0);
        if (shift) keybd_event(0x10, 0, 0, 0);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(40); mouse_event(4, 0, 0, 0, 0); await Task.Delay(40);
        if (shift) keybd_event(0x10, 0, 2, 0);
        if (control) keybd_event(0x11, 0, 2, 0);
        await Settle();
    }

    private static async Task DragRow(int from, int to)
    {
        if (logic) { window.MoveSelectedPages(to); await Settle(); return; }
        await EnsureForeground();
        var source = Row(from); var target = Row(to);
        var a = source.PointToScreen(new Point(source.ActualWidth / 2, source.ActualHeight / 3));
        var b = target.PointToScreen(new Point(target.ActualWidth / 2, 6));
        SetCursorPos((int)a.X, (int)a.Y); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(60);
        for (int i = 1; i <= 12; i++) { SetCursorPos((int)(a.X + (b.X - a.X) * i / 12), (int)(a.Y + (b.Y - a.Y) * i / 12)); await Task.Delay(30); }
        await Task.Delay(80);
        mouse_event(4, 0, 0, 0, 0);
        await Settle();
    }

    private static async Task Key(byte key, bool control = false)
    {
        if (logic)
        {
            if (key == 0x2E) window.DeleteSelectedPages();
            else if (key == 0x43) window.CopySelectedPages();
            else if (key == 0x56) window.PastePages();
            await Settle(); return;
        }
        await EnsureForeground();
        if (!List.IsKeyboardFocusWithin) { List.Focus(); (List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) as ListBoxItem)?.Focus(); }
        if (control) keybd_event(0x11, 0, 0, 0);
        keybd_event(key, 0, 0, 0); await Task.Delay(30); keybd_event(key, 0, 2, 0);
        if (control) keybd_event(0x11, 0, 2, 0);
        await Settle();
    }

    private static async Task Foreground()
    {
        await EnsureForeground();
        Check("foreground", GetForegroundWindow() == new WindowInteropHelper(window).Handle);
    }

    private static async Task EnsureForeground()
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (GetForegroundWindow() == handle) return;
        messages.Add($"foreground regained before input (was {GetForegroundWindow()})");
        for (int attempt = 0; attempt < 5 && GetForegroundWindow() != handle; attempt++)
        { keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); window.Activate(); SetForegroundWindow(handle); await Task.Delay(200); }
        if (GetForegroundWindow() != handle) throw new InvalidOperationException("The app window could not get the foreground for real input.");
        await Settle();
    }

    private static Task Settle() => Task.Delay(200);

    private static void Snapshot(string name)
    {
        double dpi = View.DpiScale;
        var bitmap = new RenderTargetBitmap((int)Math.Round(window.ActualWidth * dpi), (int)Math.Round(window.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
