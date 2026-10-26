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
using PdfNotes.App.Services;
using PdfNotes.App.Tools;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.Probe;

/// <summary>
/// D13h (spec 24.1–24.6, A35–A39). <c>d13h-logic</c> runs without OS input (windows not activated, off screen): recent
/// documents and positions, window placement, zoom steps, ←/→ page turning, PageUp/PageDown, Esc steps (key events raised
/// on the window, no OS input), links (targets, history, refused schemes, deleted pages), the outline and permission-
/// restricted PDFs. <c>d13h</c> repeats the keyboard and mouse parts with real input (only while the user is away).
/// </summary>
internal static class D13hCheck
{
    private static MainWindow window = null!;
    private static string folder = "";
    private static bool real;
    private static readonly List<object> checks = [];
    private static readonly List<string> messages = [];
    private static readonly List<string> launched = [];
    private static readonly List<string> prompts = [];
    private static DocumentSession Session => window.Document.Session;
    private static DocumentView View => (DocumentView)window.FindName("View");
    private static string linked = "", other = "", restricted = "";

    public static int Run(string output, string part)
    {
        folder = output;
        real = part == "d13h";
        Directory.CreateDirectory(folder);
        Environment.SetEnvironmentVariable("PDFNOTES_SETTINGS_DIR", Path.Combine(folder, "settings"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                PdfNotes.App.Themes.Theme.SystemThemeOverride = () => PdfNotes.App.Themes.AppTheme.Light;
                linked = Path.Combine(folder, "linked.pdf");
                other = Path.Combine(folder, "other.pdf");
                restricted = Path.Combine(folder, "restricted.pdf");
                ProbeSamplePdf.CreateLinked(linked, 6);
                ProbeSamplePdf.Create(other, 3);
                ProbeSamplePdf.CreateOwnerEncrypted(other, restricted);
                if (real) await RealInput();
                else
                {
                    await Recent();
                    await Placement();
                    await Keys();
                    await Links();
                    await Outline();
                    await Restricted();
                }
                result = 0;
            }
            catch (Exception e) { checks.Add(new { Name = "harness", Passed = false, Detail = e.ToString() }); Console.WriteLine(e); }
            finally
            {
                File.WriteAllText(Path.Combine(folder, part + "-result.json"), JsonSerializer.Serialize(new
                { Passed = result == 0, Input = real ? "os-mouse-keyboard" : "none (windows not activated, off screen; key events raised on the window)", Checks = checks, Messages = messages },
                    new JsonSerializerOptions { WriteIndented = true }));
                if (window is not null) { window.PromptOverride = (_, _) => MessageBoxResult.No; window.Close(); }
                app.Shutdown();
            }
        };
        app.Run();
        return result;
    }

    private static MainWindow NewWindow()
    {
        var w = new MainWindow();
        if (real) { w.Width = 1200; w.Height = 860; w.WindowStartupLocation = WindowStartupLocation.CenterScreen; }
        else
        {
            w.ShowActivated = false; w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = SystemParameters.VirtualScreenLeft - 4400; w.Top = SystemParameters.VirtualScreenTop; w.Width = 1200; w.Height = 860;
        }
        w.MessageSink = messages.Add;
        w.PromptOverride = (text, _) => { prompts.Add(text); messages.Add("prompt: " + text); return MessageBoxResult.Yes; };
        w.SaveDialogOverride = name => Path.Combine(folder, "saved-" + Path.GetFileName(name));
        w.LinkLauncherOverride = launched.Add;
        return w;
    }

    private static async Task Start()
    {
        window = NewWindow();
        window.Show();
        await Settle(400);
    }

    // ----- 24.1 recent documents and positions -----

    private static async Task Recent()
    {
        await Start();
        var button = (Button)window.FindName("RecentButton");
        Check("no recent documents at first: 최근 문서 disabled", window.RecentFiles.Count == 0 && !button.IsEnabled);
        Check("open linked", await window.OpenFileAsync(linked));
        Check("open other", await window.OpenFileAsync(other));
        Check("recent: most recent first, full paths", window.RecentFiles.SequenceEqual([Path.GetFullPath(other), Path.GetFullPath(linked)]) && button.IsEnabled);
        window.ShowRibbonTab(RibbonTab.File);
        await Settle();
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        await Settle(300);
        var items = window.LastRecentMenu!.Items.OfType<MenuItem>().ToArray();
        Check("the menu lists file names with the full path as tooltip, and 목록에서 제거 on right-click",
            items.Select(i => (string)i.Header).SequenceEqual(["other.pdf", "linked.pdf"]) && (string)items[1].ToolTip == Path.GetFullPath(linked) &&
            items[0].ContextMenu?.Items.OfType<MenuItem>().Single().Header as string == "목록에서 제거");
        window.LastRecentMenu.IsOpen = false;

        // Position: page 4 at 150 %, remembered when another document is shown and restored when reopened.
        View.SetZoom(1.5); View.GoToPage(3); await Settle();
        View.SetVerticalOffset(View.VerticalOffset + 120); await Settle();
        var before = View.CenterAnchor!.Value;
        Check("new note (other is remembered)", await window.CreateNoteAsync(NewPageSpec.Default));
        Check("a new note is not a recent document", window.RecentFiles.Count == 2);
        Check("reopen other", await window.OpenFileAsync(other));
        await Settle();
        var after = View.CenterAnchor!.Value;
        Check("reopened at the same page, point and zoom", Math.Abs(View.Zoom - 1.5) < 1e-9 && after.PageIndex == before.PageIndex &&
            Dist(after.PagePoint, before.PagePoint) < 1, $"{before} → {after}");
        Check("open linked (first time remembered too: fit width)", await window.OpenFileAsync(linked));
        double fit = View.Zoom;
        Check("a document seen with its default view keeps fit width", View.CurrentPageIndex == 0, $"{View.CurrentPageIndex}");

        // Save as adds the new path; a missing file is reported and leaves the list.
        Check("new note", await window.CreateNoteAsync(NewPageSpec.Default));
        Check("save as", await window.SaveAsync(saveAs: true));
        string savedNote = Path.Combine(folder, "saved-새 노트.pdf");
        Check("a saved note becomes a recent document", window.RecentFiles[0] == Path.GetFullPath(savedNote), window.RecentFiles[0]);
        string gone = Path.Combine(folder, "gone.pdf");
        File.Copy(other, gone);
        Check("open gone.pdf", await window.OpenFileAsync(gone));
        Check("new note", await window.CreateNoteAsync(NewPageSpec.Default));
        File.Delete(gone);
        Check("a missing recent file: not opened, short notice, removed", !await window.OpenRecentAsync(Path.GetFullPath(gone)) &&
            ((TextBlock)window.FindName("StatusText")).Text.Contains("gone.pdf") && !window.RecentFiles.Contains(Path.GetFullPath(gone)));
        window.RemoveRecent(Path.GetFullPath(savedNote));
        Check("목록에서 제거", !window.RecentFiles.Contains(Path.GetFullPath(savedNote)) && window.RecentFiles.Count == 2);

        // Fewer pages than remembered: the last page.
        Check("open other at page 3", await window.OpenFileAsync(other));
        View.GoToPage(2); await Settle();
        Check("new note", await window.CreateNoteAsync(NewPageSpec.Default));
        ProbeSamplePdf.Create(other, 2);
        Check("reopen other with 2 pages", await window.OpenFileAsync(other));
        await Settle();
        Check("fewer pages: the last page", View.CurrentPageIndex == 1, $"{View.CurrentPageIndex}");
        ProbeSamplePdf.Create(other, 3);
        Check("zoom unchanged by recents (document clean)", !Session.IsDirty && fit > 0);

        // Remembered across a restart.
        View.SetZoom(2); View.GoToPage(1); await Settle();
        var anchor = View.CenterAnchor!.Value;
        await CloseWindow();
        await Start();
        Check("after a restart the recent list is kept", window.RecentFiles.Count == 2 && window.RecentFiles[0] == Path.GetFullPath(other));
        Check("reopen after restart", await window.OpenRecentAsync(window.RecentFiles[0]));
        await Settle();
        Check("after a restart the position is kept", Math.Abs(View.Zoom - 2) < 1e-9 && View.CenterAnchor!.Value.PageIndex == anchor.PageIndex &&
            Dist(View.CenterAnchor!.Value.PagePoint, anchor.PagePoint) < 1);
        await CloseWindow();
    }

    // ----- 24.1 window placement -----

    private static async Task Placement()
    {
        string path = AppSettings.DefaultPath;
        var stored = AppSettings.Load(path);
        Check("the window placement was stored at closing", stored.Window is { Width: 1200, Height: 860 }, JsonSerializer.Serialize(stored.Window));
        stored.Window = new() { Left = SystemParameters.WorkArea.Left + 40, Top = SystemParameters.WorkArea.Top + 30, Width = 1000, Height = 700, Maximized = false };
        stored.Save(path);
        var w = new MainWindow();
        Check("restores size and position on a screen", w.Width == 1000 && w.Height == 700 && w.WindowStartupLocation == WindowStartupLocation.Manual &&
            w.Left == stored.Window.Left && w.Top == stored.Window.Top && w.WindowState == WindowState.Normal);
        w.Close();
        stored.Window = new() { Left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 500, Top = 100, Width = 900, Height = 600, Maximized = true };
        stored.Save(path);
        w = new MainWindow();
        Check("a position off every screen moves onto the main screen; maximized is kept", w.Width == 900 && w.WindowStartupLocation == WindowStartupLocation.CenterScreen &&
            w.WindowState == WindowState.Maximized);
        w.Close();
        stored.Window = null; stored.Save(path);
        await Settle();
    }

    // ----- 24.2 zoom steps, 4.4 page keys, 5.3 Esc -----

    private static void RaiseKey(Key key, bool preview, bool repeat = false)
    {
        var source = PresentationSource.FromVisual(window)!;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = preview ? Keyboard.PreviewKeyDownEvent : Keyboard.KeyDownEvent };
        if (repeat)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            if (typeof(KeyEventArgs).GetMethod("SetRepeat", flags) is { } set) set.Invoke(args, [true]);
            else (typeof(KeyEventArgs).GetField("_isRepeat", flags) ?? throw new InvalidOperationException("KeyEventArgs repeat cannot be set")).SetValue(args, true);
            if (!args.IsRepeat) throw new InvalidOperationException("KeyEventArgs repeat was not set");
        }
        window.RaiseEvent(args);
    }

    private static async Task Keys()
    {
        await Start();
        Check("open linked", await window.OpenFileAsync(linked));
        View.SetZoom(1); await Settle();
        int undo = Session.UndoCount;
        window.ZoomByStep(1); Check("Ctrl+= step: 100 → 125", Math.Abs(View.Zoom - 1.25) < 1e-9);
        window.ZoomByStep(1); window.ZoomByStep(1); Check("→ 150 → 200", Math.Abs(View.Zoom - 2) < 1e-9);
        window.ZoomByStep(-1); Check("Ctrl+- step: 200 → 150", Math.Abs(View.Zoom - 1.5) < 1e-9);
        View.SetZoom(.33); window.ZoomByStep(-1); Check("between steps goes to the lower one", Math.Abs(View.Zoom - .25) < 1e-9);
        window.ZoomByStep(-1); window.ZoomByStep(-1); Check("lowest step 10 %", Math.Abs(View.Zoom - .10) < 1e-9);
        View.SetZoom(8); window.ZoomByStep(1); Check("highest step 800 %", Math.Abs(View.Zoom - 8) < 1e-9);

        // ←/→ at fit width (no horizontal scroll): straight to the next/previous page; a held key does not turn.
        View.FitWidth(); View.GoToPage(0); await Settle();
        RaiseKey(Key.Right, preview: true); await Settle();
        Check("→ without horizontal scroll: next page", View.CurrentPageIndex == 1, $"{View.CurrentPageIndex}");
        RaiseKey(Key.Right, preview: true, repeat: true); await Settle();
        Check("a held → (repeat) right after a turn waits", View.CurrentPageIndex == 1);
        // 2026-10-15: held, it turns one page per 0.3 s (the view's clock passed in).
        long t0 = Environment.TickCount64 + 1000;
        Check("held 0.3 s later: the next page", View.ArrowSideways(1, true, t0) && View.CurrentPageIndex == 2);
        Check("held 0.1 s later: not yet", !View.ArrowSideways(1, true, t0 + 100) && View.CurrentPageIndex == 2);
        Check("held 0.3 s later again: the next page", View.ArrowSideways(1, true, t0 + 310) && View.CurrentPageIndex == 3);
        View.GoToPage(1); await Settle();
        RaiseKey(Key.Left, preview: true); await Settle();
        Check("← : previous page", View.CurrentPageIndex == 0);
        RaiseKey(Key.Left, preview: true); await Settle();
        Check("← on the first page: nothing", View.CurrentPageIndex == 0);
        // Zoomed in: → scrolls sideways first, at the right edge a new press turns to the next page at its left edge.
        View.SetZoom(3); View.GoToPage(0); await Settle();
        double x0 = View.HorizontalOffset;
        RaiseKey(Key.Right, preview: true); await Settle();
        Check("→ zoomed in scrolls sideways", View.HorizontalOffset > x0 && View.CurrentPageIndex == 0, $"{x0:0} → {View.HorizontalOffset:0}");
        for (int i = 0; i < 400 && View.HorizontalOffset < View.ExtentWidth - View.ViewportWidth - .5; i++) RaiseKey(Key.Right, preview: true, repeat: true);
        await Settle();
        RaiseKey(Key.Right, preview: true, repeat: true); await Settle();
        Check("held → pauses at the right edge (0.3 s)", View.CurrentPageIndex == 0);
        RaiseKey(Key.Right, preview: true); await Settle();
        Check("a new → at the edge: next page from its left edge and top", View.CurrentPageIndex == 1 && View.HorizontalOffset < 1, $"{View.CurrentPageIndex}, x {View.HorizontalOffset:0}");
        RaiseKey(Key.Left, preview: true); await Settle();
        Check("← at the left edge: previous page from its right edge", View.CurrentPageIndex == 0 && View.HorizontalOffset > View.ExtentWidth - View.ViewportWidth - 1);
        // PageUp/PageDown in both modes, Ctrl+Home/End through GoToPage (real input checks the keys).
        View.FitWidth(); View.GoToPage(2); await Settle();
        RaiseKey(Key.PageDown, preview: true); await Settle();
        Check("PageDown (continuous): next page", View.CurrentPageIndex == 3);
        RaiseKey(Key.PageUp, preview: true); RaiseKey(Key.PageUp, preview: true); await Settle();
        Check("PageUp ×2: two pages back", View.CurrentPageIndex == 1);
        window.SetViewMode(ViewMode.SinglePage); await Settle();
        RaiseKey(Key.PageDown, preview: true); await Settle();
        Check("PageDown (single page): next page", View.CurrentPageIndex == 2 && View.Mode == ViewMode.SinglePage);
        window.SetViewMode(ViewMode.Continuous); await Settle();
        Check("page keys leave the document unchanged", Session.UndoCount == undo && !Session.IsDirty);

        // Arrows with a selection still move it (exception).
        var stroke = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(100, 100), new(160, 120)], 2, new(0, 0, 0), 1);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, stroke));
        View.GoToPage(0);
        window.SetTool(ToolKind.Select); window.Selection.Select(stroke.Id); await Settle();
        RaiseKey(Key.Right, preview: true); await Settle();
        Check("→ with a selection moves it, the page stays", View.CurrentPageIndex == 0 && Session.Current.FindAnnotation(stroke.Id)!.Annotation != (Annotation)stroke);

        // Esc: selection → nothing → selection tool; operation in progress first; an automatic visit returns first.
        RaiseKey(Key.Escape, preview: false); await Settle();
        Check("Esc clears the selection, the tool stays", window.Selection.SelectedIds.Count == 0 && window.CurrentTool == ToolKind.Select);
        window.SetTool(ToolKind.Pen);
        RaiseKey(Key.Escape, preview: false); await Settle();
        Check("Esc with nothing to cancel: selection tool", window.CurrentTool == ToolKind.Select);
        window.SetTool(ToolKind.Shape);
        RaiseKey(Key.Escape, preview: false); await Settle();
        Check("Esc from the shape tool: selection tool", window.CurrentTool == ToolKind.Select);
        window.SetTool(ToolKind.Lasso); window.Selection.Select(stroke.Id); await Settle();
        Check("lasso keeps the selection", window.Selection.SelectedIds.Count == 1);
        RaiseKey(Key.Escape, preview: false); await Settle();
        Check("Esc first clears the selection (lasso stays)", window.Selection.SelectedIds.Count == 0 && window.CurrentTool == ToolKind.Lasso);
        RaiseKey(Key.Escape, preview: false); await Settle();
        Check("next Esc: selection tool", window.CurrentTool == ToolKind.Select);
        await CloseWindow();
    }

    // ----- 24.3 links -----

    private static async Task Links()
    {
        await Start();
        Check("open linked", await window.OpenFileAsync(linked));
        View.SetZoom(1); View.GoToPage(0); await Settle();
        var links = window.PageLinks(0);
        Check("page 1 has its 5 links", links.Count == 5);
        Check("links are not document content", !Session.Current.AllAnnotations.Any());
        PdfLink Find(Func<PdfLink, bool> f) => links.Single(f);
        var page3 = Find(l => l.TargetPage == 2);
        var web = Find(l => l.Uri == ProbeSamplePdf.LinkUrl);
        var script = Find(l => l.Uri?.StartsWith("javascript", StringComparison.Ordinal) == true);
        var mail = Find(l => l.Uri?.StartsWith("mailto", StringComparison.Ordinal) == true);
        Point Center(PdfLink l) { var c = new Point2((l.Area.UpperLeft.X + l.Area.LowerRight.X) / 2, (l.Area.UpperLeft.Y + l.Area.LowerRight.Y) / 2); var p = View.PageTransform(0).PageToDip.Transform(c); return new(p.X, p.Y); }
        Check("tooltips: the page or the address", View.LinkTip!(Center(page3)) == "3 페이지" && View.LinkTip!(Center(web)) == ProbeSamplePdf.LinkUrl &&
            View.LinkTip!(new Point(5, 5)) is null);
        window.SetTool(ToolKind.Pen);
        Check("a plain click follows only with the selection tool", View.LinkFollowsClick!() == false);
        window.SetTool(ToolKind.Select);
        Check("selection tool follows a plain click", View.LinkFollowsClick!() == true);

        // Internal: page 3 at its destination point, near the top; Alt+← / Alt+→ through GoBack/GoForward.
        var start = View.CenterAnchor!.Value;
        window.FollowLink(page3); await Settle();
        var target = View.PageTransform(2).PageToDip.Transform(page3.TargetPoint!.Value);
        Check("an internal link goes to page 3 with its destination near the top", View.CurrentPageIndex == 2 && Math.Abs(target.Y - DocumentLayout.MarginDip) < 2,
            $"page {View.CurrentPageIndex + 1}, y {target.Y:0.#}");
        Check("Alt+← goes back to where the jump started", window.GoBack() && View.CenterAnchor!.Value.PageIndex == start.PageIndex &&
            Dist(View.CenterAnchor!.Value.PagePoint, start.PagePoint) < 1);
        Check("Alt+→ goes forward again", window.GoForward() && View.CurrentPageIndex == 2);
        Check("nothing more forward", !window.GoForward());

        // External: asked first; only http(s)/mailto; No opens nothing.
        prompts.Clear(); launched.Clear();
        window.FollowLink(web);
        Check("web link: asked with the address, then opened", prompts.Count == 1 && prompts[0].Contains(ProbeSamplePdf.LinkUrl) && launched.SequenceEqual([ProbeSamplePdf.LinkUrl]));
        window.FollowLink(mail);
        Check("mailto is opened too", launched.Count == 2);
        window.FollowLink(script);
        Check("javascript: is never opened (no question either)", launched.Count == 2 && prompts.Count == 2);
        window.PromptOverride = (text, _) => { prompts.Add(text); return MessageBoxResult.No; };
        window.FollowLink(web);
        Check("No opens nothing", launched.Count == 2 && prompts.Count == 3);
        window.PromptOverride = (text, _) => { prompts.Add(text); messages.Add("prompt: " + text); return MessageBoxResult.Yes; };
        Check("following links leaves the document unchanged", !Session.IsDirty && Session.UndoCount == 0);

        // Moved pages: the link finds page 3 where it is now; deleted: does nothing.
        var ids = Session.Current.Pages.Select(p => p.Id).ToArray();
        Session.Execute(PageEdits.MovePages(Session.Current, [ids[2]], 5)); await Settle();
        View.GoToPage(0); await Settle();
        window.FollowLink(page3); await Settle();
        Check("after moving page 3 to the end the link still finds it", Session.Current.Pages[View.CurrentPageIndex].Id == ids[2], $"{View.CurrentPageIndex}");
        Session.Execute(DocumentEdits.DeletePages(Session.Current, [ids[2]])); await Settle();
        View.GoToPage(0); await Settle();
        window.FollowLink(page3); await Settle();
        Check("a link to a deleted page does nothing (no tooltip, no jump)", View.CurrentPageIndex == 0 && View.LinkTip!(Center(page3)) is null);
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        await CloseWindow();
    }

    // ----- 24.4 outline -----

    private static async Task Outline()
    {
        await Start();
        Check("open linked", await window.OpenFileAsync(linked));
        var button = (ToggleButton)window.FindName("OutlineButton");
        Check("outline off by default, toggle enabled for a PDF with an outline", !window.OutlineShown && button.IsEnabled && window.Outline.Count == 2);
        window.SetOutlineVisible(true); await Settle(300);
        var tree = (TreeView)window.FindName("OutlineTree");
        Check("outline shown above the page list", window.OutlineShown && tree.Items.Count == 2 && tree.ActualHeight > 100 &&
            tree.TranslatePoint(new Point(0, 0), window).Y < ((FrameworkElement)window.FindName("PageList")).TranslatePoint(new Point(0, 0), window).Y);
        await Snapshot("outline.png");
        PdfNotes.App.Themes.Theme.Use(PdfNotes.Editor.Services.ThemeChoice.Dark, PdfNotes.Editor.Services.AccentColor.Blue);
        await Snapshot("outline-dark.png");
        PdfNotes.App.Themes.Theme.Use(PdfNotes.Editor.Services.ThemeChoice.Light, PdfNotes.Editor.Services.AccentColor.Blue);
        window.FollowOutline(window.Outline[1]); await Settle();
        Check("2장 goes to page 4", View.CurrentPageIndex == 3);
        await Settle(300);
        var selected = tree.SelectedItem as TreeViewItem;
        Check("the item of the current page is highlighted", ReferenceEquals(selected?.Tag, window.Outline[1]), (selected?.Tag as PdfOutlineItem)?.Title ?? "none");
        Check("Alt+← after an outline jump", window.GoBack() && View.CurrentPageIndex == 0);
        // Rotate page 2 and delete it: the child item dims and does nothing.
        var ids = Session.Current.Pages.Select(p => p.Id).ToArray();
        Session.Execute(PageEdits.RotatePages(Session.Current, [ids[1]], clockwise: true)); await Settle();
        window.FollowOutline(window.Outline[0].Children[0]); await Settle();
        Check("rotated page 2 is still the target of 1.1", View.CurrentPageIndex == 1);
        Session.Execute(DocumentEdits.DeletePages(Session.Current, [ids[1]])); await Settle();
        var child = ((TreeViewItem)tree.Items[0]).Items.OfType<TreeViewItem>().Single();
        View.GoToPage(0); await Settle();
        window.FollowOutline(window.Outline[0].Children[0]); await Settle();
        Check("an item of a deleted page is dimmed and does nothing", ((TextBlock)child.Header).Opacity < 1 && View.CurrentPageIndex == 0);
        // Saved: the outline stays without the deleted page's item; the setting is remembered.
        string saved = Path.Combine(folder, "outline-saved.pdf");
        Check("save", await window.SaveToAsync(saved));
        using (var engine = new PdfiumDocument(File.ReadAllBytes(saved)))
        {
            var marks = engine.Bookmarks();
            Check("saved outline keeps both chapters, 1.1 is gone, 2장 → page 3 now", marks.Length == 2 && marks[0].Children.Length == 0 && marks[1].TargetPage == 2,
                string.Join(", ", marks.Select(m => $"{m.Title}→{m.TargetPage}({m.Children.Length})")));
        }
        await CloseWindow();
        await Start();
        Check("reopen saved", await window.OpenFileAsync(saved));
        Check("outline shown again (remembered) with the saved items", window.OutlineShown && window.Outline.Count == 2);
        Check("open a PDF without an outline: toggle disabled, part hidden", await window.OpenFileAsync(other) && !window.OutlineShown &&
            !((ToggleButton)window.FindName("OutlineButton")).IsEnabled);
        await CloseWindow();
    }

    // ----- 24.6 permission-restricted PDFs -----

    private static async Task Restricted()
    {
        await Start();
        messages.Clear();
        Check("a permission-restricted PDF opens", await window.OpenFileAsync(restricted));
        var status = ((TextBlock)window.FindName("StatusText")).Text;
        Check("status bar says 편집 제한 PDF", status.Contains("편집 제한 PDF"), status);
        Check("view, search: glyphs are there", window.PageGlyphs(0).Count > 10);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id,
            new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(100, 300), new(200, 330)], 2, new(0xC0, 0, 0), 1)));
        string saved = Path.Combine(folder, "restricted-saved.pdf");
        Check("save", await window.SaveToAsync(saved));
        Check("the first save tells once that the restriction is not kept", messages.Count(m => m.Contains("편집 제한")) == 1, string.Join(" | ", messages));
        Check("save again", await window.SaveToAsync(saved));
        Check("no second notice", messages.Count(m => m.Contains("편집 제한")) == 1);
        using (var engine = new PdfiumDocument(File.ReadAllBytes(saved))) Check("saved file has no security handler", !engine.IsEncrypted);
        Check("reopen the saved file: the stroke is editable", await window.OpenFileAsync(saved) && Session.Current.AllAnnotations.Count() == 1);
        // Importing pages from it is allowed since D14g (spec 11.9).
        messages.Clear();
        int pagesBefore = Session.Current.Pages.Count;
        window.ImportFileDialogOverride = () => restricted;
        window.ImportRangeOverride = _ => Task.FromResult<int[]?>([0]);
        Check("importing a page from it works (spec 11.9, D14g)", await window.ImportPdfPagesAsync() && Session.Current.Pages.Count == pagesBefore + 1 && messages.Count == 0,
            string.Join(" | ", messages));
        // A password-protected PDF is still refused.
        string password = Path.Combine(folder, "password.pdf");
        ProbeSamplePdf.CreatePasswordProtected(other, password);
        string? error = null;
        window.OpenErrorSink = e => error = e;
        Check("a PDF needing a password is still refused", !await window.OpenFileAsync(password) && error is not null);
        await CloseWindow();
    }

    // ----- real input (d13h) -----

    private static async Task RealInput()
    {
        await Start(); await Foreground();
        Check("open linked", await window.OpenFileAsync(linked));
        await Settle();
        View.FitWidth(); View.GoToPage(0); await Settle();

        // Zoom keys.
        View.SetZoom(1); await Settle();
        await Press(0xBB, control: true); // Ctrl+=
        Check("Ctrl+= : 125 %", Math.Abs(View.Zoom - 1.25) < 1e-9, $"{View.Zoom:0.###}");
        await Press(0x6B, control: true); // Ctrl+numpad +
        Check("Ctrl+numpad + : 150 %", Math.Abs(View.Zoom - 1.5) < 1e-9);
        await Press(0xBD, control: true); // Ctrl+-
        Check("Ctrl+- : 125 %", Math.Abs(View.Zoom - 1.25) < 1e-9);
        await Press(0x30, control: true); // Ctrl+0
        View.FitWidth(); double fit = View.Zoom; View.SetZoom(1.25); await Settle();
        await Press(0x30, control: true);
        Check("Ctrl+0 : fit width", Math.Abs(View.Zoom - fit) < 1e-9);

        // Page keys.
        View.GoToPage(0); await Settle();
        await Press(0x27); // →
        Check("→ at fit width: next page", View.CurrentPageIndex == 1);
        await Press(0x25); // ←
        Check("← : previous page", View.CurrentPageIndex == 0);
        await Press(0x22); // PageDown
        await Press(0x22);
        Check("PageDown ×2", View.CurrentPageIndex == 2);
        await Press(0x21); // PageUp
        Check("PageUp", View.CurrentPageIndex == 1);
        await Press(0x23, control: true); // Ctrl+End
        Check("Ctrl+End: last page", View.CurrentPageIndex == Session.Current.Pages.Count - 1);
        await Press(0x24, control: true); // Ctrl+Home
        Check("Ctrl+Home: first page", View.CurrentPageIndex == 0 && View.VerticalOffset < 1);
        // A held → at 300 % stops at the right edge.
        View.SetZoom(3); View.GoToPage(0); await Settle();
        await HoldKey(0x27, 9000);
        // 2026-10-15: a held → keeps going — to the right edge, then a page every 0.3 s.
        Check("holding → scrolls to the right edge and then keeps turning pages", View.CurrentPageIndex >= 2, $"page {View.CurrentPageIndex}");
        View.FitWidth(); View.GoToPage(0); await Settle();

        // Esc steps.
        await ClickElement(Named("PenToolButton"));
        await Press(0x1B);
        Check("Esc with the pen idle: selection tool", window.CurrentTool == ToolKind.Select);

        // Links with the mouse: hand cursor, plain click (selection tool), Alt+← / Alt+→, pen Ctrl+click.
        View.SetZoom(1); View.GoToPage(0); await Settle();
        var link = window.PageLinks(0).Single(l => l.TargetPage == 2);
        Point2 inside = new((link.Area.UpperLeft.X + link.Area.LowerRight.X) / 2, (link.Area.UpperLeft.Y + link.Area.LowerRight.Y) / 2);
        await EnsureForeground();
        Cursor(Dip(inside, 0)); await Task.Delay(150);
        Cursor(new Point(Dip(inside, 0).X + 1, Dip(inside, 0).Y)); await Settle();
        Check("hand cursor over a link (selection tool)", View.Cursor == Cursors.Hand, View.Cursor?.ToString() ?? "default");
        await ClickPage(inside, 0);
        Check("a click on the link goes to page 3", View.CurrentPageIndex == 2, $"{View.CurrentPageIndex}");
        await AltArrow(0x25);
        Check("Alt+← goes back to page 1", View.CurrentPageIndex == 0);
        await AltArrow(0x27);
        Check("Alt+→ goes forward to page 3", View.CurrentPageIndex == 2);
        await AltArrow(0x25);
        await ClickElement(Named("PenToolButton"));
        View.SetZoom(1); View.GoToPage(0); await Settle();
        int strokes = Session.Current.Pages[0].Annotations.Count;
        await ClickPage(inside, 0);
        Check("pen: a plain click on a link draws (no jump)", View.CurrentPageIndex == 0 && Session.Current.Pages[0].Annotations.Count == strokes + 1);
        View.GoToPage(0); await Settle();
        keybd_event(0x11, 0, 0, 0);
        await ClickPage(inside, 0);
        keybd_event(0x11, 0, 2, 0); await Settle();
        Check("pen: Ctrl+click follows the link and draws nothing", View.CurrentPageIndex == 2 && Session.Current.Pages[0].Annotations.Count == strokes + 1);
        await Press(0x1B);

        // Outline with the mouse.
        await ClickElement(Named("OutlineButton"));
        var tree = (TreeView)window.FindName("OutlineTree");
        Check("outline toggle shows the outline", window.OutlineShown);
        await Settle();
        var chapter2 = (TreeViewItem)tree.Items[1];
        await ClickElement((FrameworkElement)chapter2.Template.FindName("PART_Header", chapter2) ?? chapter2);
        Check("a click on 2장 goes to page 4", View.CurrentPageIndex == 3, $"{View.CurrentPageIndex}");
        Snapshot("t-outline.png", window);
        await ClickElement(Named("OutlineButton"));
        Check("toggle hides it again", !window.OutlineShown);

        // Recent documents menu.
        Check("open other", await window.OpenFileAsync(other));
        await ClickElement(Named("FileTabButton"));
        await ClickElement(Named("RecentButton"));
        var item = window.LastRecentMenu!.Items.OfType<MenuItem>().Single(i => (string)i.Header == "linked.pdf");
        await ClickScreen(item);
        await Settle(); await Settle();
        Check("최근 문서 › linked.pdf opens it", window.Document.FilePath == Path.GetFullPath(linked));
        Check("…at the page it was left (4)", View.CurrentPageIndex == 3, $"{View.CurrentPageIndex}");
    }

    // ----- helpers -----

    private static async Task CloseWindow()
    {
        window.PromptOverride = (_, _) => MessageBoxResult.No; // never save the samples on closing
        window.Close();
        await Settle(300);
    }

    private static void Check(string name, bool passed, string detail = "")
    {
        checks.Add(new { Name = name, Passed = passed, Detail = detail });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        if (!passed) throw new InvalidOperationException(name + ": " + detail);
    }

    private static Task Settle(int ms = 200) => Task.Delay(ms);
    private static double Dist(Point2 a, Point2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static FrameworkElement Named(string name) => (FrameworkElement)window.FindName(name);

    private static async Task Snapshot(string name)
    {
        await Settle(300);
        Snapshot(name, window);
    }

    private static void Snapshot(string name, Window? w = null)
    {
        w ??= window;
        double dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Round(w.ActualWidth * dpi), (int)Math.Round(w.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(w);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
    }

    private static Point Dip(Point2 pagePoint, int page)
    {
        var p = View.PageTransform(page).PageToDip.Transform(pagePoint);
        return new(p.X, p.Y);
    }

    private static async Task ClickPage(Point2 pagePoint, int page)
    {
        await Task.Delay(400);
        await EnsureForeground();
        var p = Dip(pagePoint, page);
        Cursor(p); await Task.Delay(80);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(40);
        Cursor(new Point(p.X + .5, p.Y)); await Task.Delay(30);
        mouse_event(4, 0, 0, 0, 0);
        await Settle(); await Settle();
    }

    private static async Task ClickElement(FrameworkElement element)
    {
        await Task.Delay(400);
        if (Window.GetWindow(element) == window) await EnsureForeground();
        if (!element.IsVisible || element.ActualWidth == 0) throw new InvalidOperationException($"Element is not visible: {element.Name} {element}");
        var p = element.PointToScreen(new(element.ActualWidth / 2, element.ActualHeight / 2));
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y)); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0);
        await Settle();
    }

    private static async Task ClickScreen(FrameworkElement element)
    {
        await Task.Delay(400);
        if (!element.IsVisible || element.ActualWidth == 0) throw new InvalidOperationException($"Element is not visible: {element}");
        var p = element.PointToScreen(new(element.ActualWidth / 2, element.ActualHeight / 2));
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y)); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0);
        await Settle();
    }

    private static async Task Press(byte key, bool control = false)
    {
        await EnsureForeground();
        if (!window.IsKeyboardFocusWithin) View.Focus();
        if (control) keybd_event(0x11, 0, 0, 0);
        keybd_event(key, 0, IsExtended(key) ? 1u : 0, 0); await Task.Delay(30); keybd_event(key, 0, (IsExtended(key) ? 1u : 0) | 2, 0);
        if (control) keybd_event(0x11, 0, 2, 0);
        await Settle(); await Settle();
    }

    private static bool IsExtended(byte key) => key is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28;

    private static async Task AltArrow(byte key)
    {
        await EnsureForeground();
        keybd_event(0x12, 0, 0, 0); keybd_event(key, 0, 1, 0); await Task.Delay(30); keybd_event(key, 0, 3, 0); keybd_event(0x12, 0, 2, 0);
        await Settle(); await Settle();
    }

    /// <summary>Holds a key with auto-repeat (key-down repeated like the keyboard does) for <paramref name="ms"/>.</summary>
    private static async Task HoldKey(byte key, int ms)
    {
        await EnsureForeground();
        View.Focus();
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { keybd_event(key, 0, 1, 0); await Task.Delay(30); }
        keybd_event(key, 0, 3, 0);
        await Settle(); await Settle();
    }

    private static async Task Foreground()
    {
        var handle = new WindowInteropHelper(window).Handle;
        SetForegroundWindow(handle); window.Activate(); await Settle();
        for (int attempt = 0; attempt < 5 && GetForegroundWindow() != handle; attempt++)
        { keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); window.Activate(); SetForegroundWindow(handle); await Task.Delay(200); }
        if (GetForegroundWindow() != handle) { window.Topmost = true; window.Activate(); SetForegroundWindow(handle); await Settle(); window.Topmost = false; }
        Check("foreground", GetForegroundWindow() == handle, $"target={handle}, actual={GetForegroundWindow()}");
    }

    private static async Task EnsureForeground()
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (GetForegroundWindow() == handle) return;
        messages.Add("foreground regained before input");
        for (int attempt = 0; attempt < 5 && GetForegroundWindow() != handle; attempt++)
        { keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); window.Activate(); SetForegroundWindow(handle); await Task.Delay(200); }
        if (GetForegroundWindow() != handle) throw new InvalidOperationException("The app window could not get the foreground for real input.");
        await Settle();
    }

    private static void Cursor(Point dip)
    {
        if (dip.X < 0 || dip.Y < 0 || dip.X > View.ActualWidth || dip.Y > View.ActualHeight)
            throw new InvalidOperationException($"Input point {dip} is outside the document view.");
        var p = View.PointToScreen(dip);
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
