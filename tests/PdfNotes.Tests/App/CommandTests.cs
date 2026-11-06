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
