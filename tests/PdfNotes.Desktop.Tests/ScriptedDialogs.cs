using Avalonia.Controls;
using PdfNotes.UI.Views;
using PdfNotes.Editor.Platform;

using PdfNotes.Editor.Services;
using PdfNotes.ViewModels;

namespace PdfNotes.Desktop.Tests;

/// <summary>
/// The window's modal UI for tests: each answer comes from a script when one is set, otherwise from the real dialogs.
/// Test windows get it through <see cref="TestWindows.Dialogs"/> and reach it as <c>window.Script</c>.
/// </summary>
internal sealed class ScriptedDialogs(Window owner) : IDesktopDialogs
{
    private readonly DesktopDialogs real = new(owner);

    /// <summary>Answers questions instead of a dialog.</summary>
    public Func<string, AskButtons, Answer>? PromptOverride { get; set; }

    /// <summary>Returns the save path (null = cancelled) instead of the save dialog.</summary>
    public Func<SavePathRequest, string?>? SaveDialogOverride { get; set; }

    /// <summary>Receives messages instead of a dialog.</summary>
    public Action<string>? MessageSink { get; set; }

    /// <summary>Gets each app window instead of showing it; true presses its OK (then <see cref="IAcceptDialog.Accept"/> runs).</summary>
    public Func<DialogView, bool>? WindowOverride { get; set; }

    /// <summary>Returns the files of an open dialog (null cancels).</summary>
    public Func<string[]?>? OpenDialogOverride { get; set; }

    public bool IsOpen => real.IsOpen;

    public Answer Ask(string text, AskButtons buttons, NoticeKind kind) =>
        PromptOverride is { } answer ? answer(text, buttons) : real.Ask(text, buttons, kind);

    public void Tell(string text, NoticeKind kind)
    {
        if (MessageSink is { } sink) sink(text);
        else real.Tell(text, kind);
    }

    public string? ChooseSavePath(SavePathRequest request) =>
        SaveDialogOverride is { } choose ? choose(request) : real.ChooseSavePath(request);

    public bool ShowWindow(DialogView window)
    {
        if (WindowOverride is not { } run) return real.ShowWindow(window);
        try { return Press(run, window); }
        finally { (window as IDisposable)?.Dispose(); }
    }

    public Task<bool> ShowWindowAsync(DialogView window) =>
        WindowOverride is null ? real.ShowWindowAsync(window) : Task.FromResult(ShowWindow(window));

    public Task<bool> ShowChildAsync(DialogView child, DialogView parent) =>
        WindowOverride is { } run ? Task.FromResult(Press(run, child)) : real.ShowChildAsync(child, parent);

    public Task<string?> ChooseOpenPathAsync() =>
        OpenDialogOverride is { } choose ? Task.FromResult(choose()?.FirstOrDefault()) : real.ChooseOpenPathAsync();

    public string[]? ChoosePdfFiles(string title) =>
        OpenDialogOverride is { } choose ? choose() : real.ChoosePdfFiles(title);

    public Task<string[]?> ChooseImageAsync() =>
        OpenDialogOverride is { } choose ? Task.FromResult(choose()) : real.ChooseImageAsync();

    private static bool Press(Func<DialogView, bool> run, DialogView window) => run(window) && window.Accept();
}

internal static class TestWindows
{
    /// <summary>Pass to <see cref="MainWindow"/>: the window's dialogs are scripted.</summary>
    public static readonly Func<Window, IDesktopDialogs> Dialogs = owner => new ScriptedDialogs(owner);

    /// <summary>What a test window takes from the app: these settings (default new), saved nowhere, and its own folder.</summary>
    public static AppServices App(AppSettings? settings = null, string? folder = null) =>
        new(settings ?? new AppSettings(), PdfNotes.Desktop.App.DisplayName, _ => { }, folder ?? Headless.TempFolder()) { Texts = PdfNotes.Editor.Localization.Strings.Current };

    extension(MainWindow window)
    {
        /// <summary>The scripted dialogs of a test window.</summary>
        public ScriptedDialogs Script => (ScriptedDialogs)window.Dialogs;
    }
}
