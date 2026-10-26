using System.Windows;

namespace PdfNotes.App.Themes;

/// <summary>
/// State marks of tool buttons beyond the four button states (spec 3.1). <see cref="IsReturnTargetProperty"/>: the tool an
/// automatic selection visit goes back to (spec 5.4, D14g) — the ToolButton style draws its border in the accent color.
/// </summary>
public static class ToolMarks
{
    public static readonly DependencyProperty IsReturnTargetProperty =
        DependencyProperty.RegisterAttached("IsReturnTarget", typeof(bool), typeof(ToolMarks), new PropertyMetadata(false));

    public static bool GetIsReturnTarget(DependencyObject element) => (bool)element.GetValue(IsReturnTargetProperty);
    public static void SetIsReturnTarget(DependencyObject element, bool value) => element.SetValue(IsReturnTargetProperty, value);
}
