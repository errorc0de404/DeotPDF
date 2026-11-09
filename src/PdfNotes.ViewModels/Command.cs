using System.Windows.Input;

namespace PdfNotes.ViewModels;

/// <summary>
/// A command a view binds a button to (roadmap P4.5 R4). <see cref="CanExecute"/> asks the view model each time;
/// <see cref="Refresh"/> tells bound controls to ask again after the state it depends on changed.
/// </summary>
public sealed class Command(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter = null) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter = null)
    {
        if (CanExecute(parameter)) execute();
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A command with a parameter of type <typeparamref name="T"/> (a ribbon tab, a view mode).</summary>
public sealed class Command<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => parameter is T value && (canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is T value && CanExecute(value)) execute(value);
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// A command that starts asynchronous work (open, save, export). A click returns at once; <see cref="RunAsync"/> lets
/// keys and tests wait for the result. While the work runs the command cannot start again (a second click on 저장 does
/// nothing), and a failure of a click goes to <paramref name="onError"/> or, without one, to <see cref="UnhandledError"/>.
/// </summary>
public sealed class AsyncCommand(Func<Task> run, Func<bool>? canExecute = null, Action<Exception>? onError = null) : ICommand
{
    /// <summary>A failure no caller waited for (a click, <see cref="Fire.AndReport"/>): the app shows it and ends.</summary>
    public static event Action<Exception>? UnhandledError;

    public event EventHandler? CanExecuteChanged;

    /// <summary>The work of the last start is still running.</summary>
    public bool IsRunning { get; private set; }

    public bool CanExecute(object? parameter = null) => !IsRunning && (canExecute?.Invoke() ?? true);

    public void Execute(object? parameter = null) => Fire.AndReport(RunAsync(), onError);

    /// <summary>Runs the work when the command can run now; otherwise does nothing. Failures go to the caller.</summary>
    public async Task RunAsync()
    {
        if (!CanExecute()) return;
        IsRunning = true;
        Refresh();
        try { await run(); }
        finally
        {
            IsRunning = false;
            Refresh();
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    internal static void Report(Exception error, Action<Exception>? onError)
    {
        if (onError is not null) onError(error);
        else UnhandledError?.Invoke(error);
    }
}

/// <summary>Work the view starts and does not wait for (a key, a menu item): its failure is reported, not lost.</summary>
public static class Fire
{
    public static void AndReport(Task task, Action<Exception>? onError = null)
    {
        if (task.IsCompletedSuccessfully || task.IsCanceled) return;
        _ = Watch(task, onError);
    }

    private static async Task Watch(Task task, Action<Exception>? onError)
    {
        try { await task; }
        catch (OperationCanceledException) { } // a cancelled render or a closed window is not a failure
        catch (Exception e) { AsyncCommand.Report(e, onError); }
    }
}
