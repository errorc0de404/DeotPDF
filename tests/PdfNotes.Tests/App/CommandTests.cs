using PdfNotes.ViewModels;

namespace PdfNotes.Tests.App;

/// <summary>Roadmap P4.5 R4a: the commands views bind their buttons to.</summary>
public sealed class CommandTests
{
    [Fact]
    public void ACommandRunsOnlyWhenItCanAndTellsWhenThatMayHaveChanged()
    {
        bool allowed = false;
        int runs = 0, changes = 0;
        var command = new Command(() => runs++, () => allowed);
        command.CanExecuteChanged += (_, _) => changes++;
        command.Execute();
        Assert.Equal(0, runs);
        allowed = true;
        command.Refresh();
        Assert.Equal(1, changes);
        Assert.True(command.CanExecute());
        command.Execute();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void AParameterCommandTakesOnlyItsType()
    {
        string? got = null;
        var command = new Command<string>(s => got = s);
        Assert.False(command.CanExecute(3));
        command.Execute(3);
        Assert.Null(got);
        command.Execute("View");
        Assert.Equal("View", got);
    }

    [Fact]
    public async Task AnAsyncCommandCanBeAwaitedAndDoesNothingWhenItCannotRun()
    {
        bool allowed = true;
        int runs = 0;
        var command = new AsyncCommand(async () => { await Task.Yield(); runs++; }, () => allowed);
        await command.RunAsync();
        Assert.Equal(1, runs);
        allowed = false;
        await command.RunAsync();
        Assert.Equal(1, runs);
    }

    /// <summary>Review 2026-11-08 D2: a second click while the work runs starts nothing.</summary>
    [Fact]
    public async Task ACommandRunningDoesNotStartAgain()
    {
        var gate = new TaskCompletionSource();
        int runs = 0, changes = 0;
        var command = new AsyncCommand(async () => { runs++; await gate.Task; });
        command.CanExecuteChanged += (_, _) => changes++;
        command.Execute();
        Assert.True(command.IsRunning);
        Assert.False(command.CanExecute());
        command.Execute(); // the second click
        await command.RunAsync();
        Assert.Equal(1, runs);
        gate.SetResult();
        await Task.Yield();
        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute());
        Assert.Equal(2, changes); // started, ended
    }

    /// <summary>Review 2026-11-08 D2: a click's failure is reported, not lost with its task.</summary>
    [Fact]
    public void AFailingCommandReportsItsException()
    {
        var mine = new List<Exception>();
        var failure = new InvalidOperationException("boom");
        new AsyncCommand(() => Task.FromException(failure), onError: mine.Add).Execute();
        Assert.Same(failure, Assert.Single(mine));

        var reported = new List<Exception>();
        void Report(Exception e) => reported.Add(e);
        AsyncCommand.UnhandledError += Report;
        try
        {
            new AsyncCommand(() => Task.FromException(failure)).Execute();
            Fire.AndReport(Task.FromCanceled(new CancellationToken(true))); // a cancellation is not a failure
            Fire.AndReport(Task.FromException(failure));
        }
        finally { AsyncCommand.UnhandledError -= Report; }
        Assert.Equal([failure, failure], reported);
    }

    [Fact]
    public void TheRibbonShowCommandShowsTheNamedTab()
    {
        var ribbon = new RibbonViewModel(new PdfNotes.Editor.Services.AppSettings());
        var changed = new List<string?>();
        ribbon.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        ribbon.ShowCommand.Execute("Page");
        Assert.Equal(RibbonTab.Page, ribbon.Current);
        Assert.True(ribbon.IsPage);
        Assert.Contains(nameof(RibbonViewModel.IsPage), changed);
        Assert.Contains(nameof(RibbonViewModel.IsHome), changed);
        ribbon.ShowCommand.Execute("NoSuchTab");
        Assert.Equal(RibbonTab.Page, ribbon.Current);
    }
}
