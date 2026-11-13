using PdfNotes.Editor.Services;

namespace PdfNotes.ViewModels;

/// <summary>Tabs of the top ribbon (spec 3.2 D13 implementation default). <see cref="Home"/> is the tab named 필기 (2026-10-14).</summary>
public enum RibbonTab { File, Home, Page, View, Ai }

/// <summary>The visible ribbon tab (P1 V2). Changing it never changes the current tool or the keyboard focus.</summary>
public sealed class RibbonViewModel(AppSettings settings) : ObservableObject
{
    private RibbonTab current = RibbonTab.Home;

    public RibbonTab Current
    {
        get => current;
        set
        {
            if (!SetProperty(ref current, value)) return;
            OnPropertyChanged(nameof(IsFile));
            OnPropertyChanged(nameof(IsHome));
            OnPropertyChanged(nameof(IsPage));
            OnPropertyChanged(nameof(IsView));
            OnPropertyChanged(nameof(IsAi));
            OnPropertyChanged(nameof(HomeShown));
        }
    }

    public bool IsFile => current == RibbonTab.File;
    public bool IsHome => current == RibbonTab.Home;
    public bool IsPage => current == RibbonTab.Page;
    public bool IsView => current == RibbonTab.View;
    public bool IsAi => current == RibbonTab.Ai;

    /// <summary>The 필기 commands are on screen: their tab is shown, or always in the touch layout.</summary>
    public bool HomeShown => current == RibbonTab.Home || PanelsFloat;

    /// <summary>A tab head: shows that tab (the parameter is the tab's name, as the XAML gives it).</summary>
    public Command<string> ShowCommand => show ??= new(name => { if (Enum.TryParse<RibbonTab>(name, out var tab)) Current = tab; });
    private Command<string>? show;

    /// <summary>The touch layout's tab buttons: shows that tab, or 필기 again when it is already shown (its panel closes).</summary>
    public Command<string> ToggleCommand => toggle ??= new(name =>
    {
        if (Enum.TryParse<RibbonTab>(name, out var tab)) Current = Current == tab ? RibbonTab.Home : tab;
    });
    private Command<string>? toggle;

    /// <summary>
    /// The touch layout (2026-11-08): 필기 is always shown and the other tabs open over the document, so work never
    /// opens one by itself (<see cref="Auto"/> does nothing).
    /// </summary>
    public bool PanelsFloat
    {
        get => panelsFloat;
        set { if (SetProperty(ref panelsFloat, value)) OnPropertyChanged(nameof(HomeShown)); }
    }
    private bool panelsFloat;

    /// <summary>While true, <see cref="Auto"/> does nothing (an Undo is not canvas work, spec 3.3).</summary>
    public bool Hold { get; set; }

    /// <summary>
    /// Automatic tab switch (spec 3.2, 2026-10-14): work on the page list shows 페이지, work on the canvas (selecting,
    /// drawing, typing, changing the tool) shows 필기. Only between those two — an open 파일 or 보기 tab stays — and only
    /// with the 화면 setting on.
    /// </summary>
    public void Auto(RibbonTab tab)
    {
        if (Hold || PanelsFloat || !settings.AutoSwitchTabs || Current is not (RibbonTab.Home or RibbonTab.Page) || Current == tab) return;
        Current = tab;
    }
}
