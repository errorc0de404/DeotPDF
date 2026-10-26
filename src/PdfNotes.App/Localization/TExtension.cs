using System.Windows.Markup;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App.Localization;

/// <summary>XAML text from the language pack (D14e): <c>Text="{l:T ribbon.save}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
