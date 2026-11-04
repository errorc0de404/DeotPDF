using PdfNotes.Editor.Services;

namespace PdfNotes.ViewModels;

/// <summary>Tabs of the top ribbon (spec 3.2 D13 implementation default). <see cref="Home"/> is the tab named 필기 (2026-10-14).</summary>
public enum RibbonTab { File, Home, Page, View }

/// <summary>The visible ribbon tab (P1 V2). Changing it never changes the current tool or the keyboard focus.</summary>
public sealed class RibbonViewModel(AppSettings settings) : ObservableObject
{
    private RibbonTab current = RibbonTab.Home;

    public RibbonTab Current { get => current; set => SetProperty(ref current, value); }

    /// <summary>While true, <see cref="Auto"/> does nothing (an Undo is not canvas work, spec 3.3).</summary>
    public bool Hold { get; set; }

    /// <summary>
    /// Automatic tab switch (spec 3.2, 2026-10-14): work on the page list shows 페이지, work on the canvas (selecting,
    /// drawing, typing, changing the tool) shows 필기. Only between those two — an open 파일 or 보기 tab stays — and only
    /// with the 화면 setting on.
    /// </summary>
    public void Auto(RibbonTab tab)
    {
        if (Hold || !settings.AutoSwitchTabs || Current is not (RibbonTab.Home or RibbonTab.Page) || Current == tab) return;
        Current = tab;
    }
}
