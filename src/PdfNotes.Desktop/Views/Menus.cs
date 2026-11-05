using Avalonia.Controls;
using PdfNotes.Desktop.Themes;

namespace PdfNotes.Desktop.Views;

/// <summary>Menu items built in code (context menus, the ▾ menus).</summary>
public static class Menus
{
    public static MenuItem Item(object header, Action run, bool isChecked = false, bool enabled = true, string? glyph = null, string? outline = null)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (isChecked) { item.ToggleType = MenuItemToggleType.CheckBox; item.IsChecked = true; }
        if (glyph is not null || outline is not null) item.Icon = new AppIcon { Glyph = glyph, Outline = outline };
        item.Click += (_, _) => run();
        return item;
    }

    /// <summary>A submenu.</summary>
    public static MenuItem Sub(string header, bool enabled, IEnumerable<Control> items)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        foreach (var child in items) item.Items.Add(child);
        return item;
    }
}
