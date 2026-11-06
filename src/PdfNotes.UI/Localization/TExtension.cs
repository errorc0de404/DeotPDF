using Avalonia.Markup.Xaml;
using PdfNotes.Editor.Localization;

namespace PdfNotes.UI.Localization;

/// <summary>XAML text from the language pack (D14e), as in the WPF app: <c>Text="{l:T ribbon.save}"</c>.</summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
