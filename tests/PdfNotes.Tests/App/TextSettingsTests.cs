using System.IO;
using PdfNotes.App.Services;
using PdfNotes.Core.Commands;

namespace PdfNotes.Tests.App;

public sealed class TextSettingsTests
{
    [Fact] public void FiveSlotsAndDefaultSurviveReloadWithoutBeingDocumentState()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-D06-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "settings.json"); var settings = new AppSettings();
            settings.DefaultText = TextEditing.Default with { FontFamily = "Segoe UI", SizePt = 21, Italic = true };
            settings.TextSlots[4] = TextEditing.Default with { SizePt = 32, Bold = true, Underline = true, Strikethrough = true };
            settings.Save(path); var again = AppSettings.Load(path);
            Assert.Equal(5, again.TextSlots.Length); Assert.Equal(settings.DefaultText, again.DefaultText);
            Assert.Equal(settings.TextSlots[4], again.TextSlots[4]); Assert.All(again.TextSlots.Take(4), s => Assert.Null(s));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact] public void DamagedSlotsAndOldSettingsFallBackWithoutDestroyingOtherSettings()
    {
        string path = Path.Combine(Path.GetTempPath(), "PdfNotes-D06-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, "{\"viewMode\":\"singlePage\",\"textSlots\":[null],\"defaultText\":null}");
            var settings = AppSettings.Load(path); Assert.Equal(TextEditing.Default, settings.DefaultText);
            Assert.Equal(5, settings.TextSlots.Length); Assert.Equal(PdfNotes.Editor.Layout.ViewMode.SinglePage, settings.ViewMode);
            settings.DefaultText = TextEditing.Default with { SizePt = 900 }; settings.TextSlots[0] = settings.DefaultText;
            settings.Save(path); settings = AppSettings.Load(path); Assert.Equal(12, settings.DefaultText.SizePt); Assert.Null(settings.TextSlots[0]);
        }
        finally { File.Delete(path); }
    }
}
