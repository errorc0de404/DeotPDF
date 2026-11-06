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
/// keys and tests wait for the result.
/// </summary>
public sealed class AsyncCommand(Func<Task> run, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter = null) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter = null) => _ = RunAsync();

    /// <summary>Runs the work when the command can run now; otherwise does nothing.</summary>
    public Task RunAsync() => CanExecute() ? run() : Task.CompletedTask;

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
