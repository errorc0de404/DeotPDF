using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Editor.Services;

namespace PdfNotes.Tests.Core;

/// <summary>1.4 text box auto width, Task 1: the model flag, schema v9 and the annotation clipboard.</summary>
public sealed class TextAutoWidthModelTests
{
    private static readonly TextRun Style = new(0, 0, "Malgun Gothic", 11, RgbColor.Black, false, false, false, false);

    private static TextBoxAnnotation Box(string text, double width = 73.25, bool autoWidth = false) =>
        new(Guid.NewGuid(), false, Affine.Translation(40, 60), text, [Style with { Length = text.Length }], TextAlignment.Left, width,
            TextBoxStyle.Default, []) { AutoWidth = autoWidth };

    private static DocumentModel With(params Annotation[] annotations)
    {
        var model = TestModels.Empty();
        return model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Annotations = [.. annotations] }) };
    }

    private static DocumentReadResult Read(SerializedDocument data) =>
        SchemaV9.Read(data.Header, data.Annotations.Select(a => a.Envelope).ToArray(), _ => null);

    private static int Version(byte[] json) => JsonNode.Parse(json)!["schemaVersion"]!.GetValue<int>();

    private static byte[] WithVersion(byte[] json, int version)
    {
        var node = JsonNode.Parse(json)!;
        node["schemaVersion"] = version;
        return System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    [Fact]
    public void AutoWidthRoundTripsInV9()
    {
        var box = Box("짧은 글", autoWidth: true);
        Assert.True((box with { Text = "x" }).AutoWidth); // with copies keep the flag
        var data = SchemaV9.Serialize(With(TestModels.Ink(), box));
        Assert.Equal(9, Version(data.Header));
        Assert.All(data.Annotations, a => Assert.Equal(9, Version(a.Envelope)));
        Assert.True(JsonNode.Parse(data.Annotations[1].Envelope)!["annotation"]!["autoWidth"]!.GetValue<bool>());
        var read = Read(data);
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus);
        Assert.All(read.Annotations, a => Assert.Equal(AnnotationReadStatus.Restored, a.Status));
        var back = Assert.IsType<TextBoxAnnotation>(read.Model!.Pages[0].Annotations[1]);
        Assert.True(back.AutoWidth);
        Assert.Equal(box.WidthPt, back.WidthPt);
        // A fixed box next to an auto box in v9 writes no field and reads back fixed.
        var fixedBox = Box("고정");
        var mixed = SchemaV9.Serialize(With(box, fixedBox));
        Assert.Null(JsonNode.Parse(mixed.Annotations[1].Envelope)!["annotation"]!["autoWidth"]);
        Assert.False(Assert.IsType<TextBoxAnnotation>(Read(mixed).Model!.Pages[0].Annotations[1]).AutoWidth);
        // Writing what was read gives the same bytes.
        var again = SchemaV9.Serialize(read.Model);
        Assert.Equal(data.Header, again.Header);
        Assert.Equal(data.Annotations.Select(a => a.Envelope), again.Annotations.Select(a => a.Envelope));
        // Scaled auto boxes keep both layoutScale and autoWidth.
        var scaled = Read(SchemaV9.Serialize(With(box with { LayoutScale = 0.5 })));
        var scaledBack = Assert.IsType<TextBoxAnnotation>(scaled.Model!.Pages[0].Annotations[0]);
        Assert.Equal((0.5, true), (scaledBack.LayoutScale, scaledBack.AutoWidth));
    }

    [Fact]
    public void FixedBoxesStayV8()
    {
        var data = SchemaV9.Serialize(With(TestModels.Ink(), Box("고정")));
        Assert.True(Version(data.Header) <= 8);
        Assert.All(data.Annotations, a => Assert.DoesNotContain("autoWidth", System.Text.Encoding.UTF8.GetString(a.Envelope)));
        var back = Assert.IsType<TextBoxAnnotation>(Read(data).Model!.Pages[0].Annotations[1]);
        Assert.False(back.AutoWidth);
    }

    [Fact]
    public void ClipboardKeepsAutoWidth()
    {
        var auto = Box("자동", autoWidth: true);
        var fixedBox = Box("고정", width: 120);
        var model = With(auto, fixedBox);
        var clip = AnnotationTransfer.Decode(AnnotationTransfer.Encode(model, model.Pages[0].Id, [auto.Id, fixedBox.Id]), _ => { });
        var boxes = clip.Annotations.OfType<TextBoxAnnotation>().ToArray();
        Assert.Equal(2, boxes.Length);
        Assert.True(boxes.Single(b => b.Text == "자동").AutoWidth);
        Assert.False(boxes.Single(b => b.Text == "고정").AutoWidth);
        Assert.Equal(120, boxes.Single(b => b.Text == "고정").WidthPt);
    }

    [Fact]
    public void V8RejectsAutoWidth()
    {
        var model = With(Box("자동", autoWidth: true));
        Assert.Throws<InvalidDataException>(() => SchemaV8.Serialize(model));
        Assert.Throws<InvalidDataException>(() => SchemaV7.Serialize(model));
        // An autoWidth field in a v8 file is not part of v8: that envelope is rejected.
        var data = SchemaV9.Serialize(model);
        var v8 = SchemaV9.Read(WithVersion(data.Header, 8), [WithVersion(data.Annotations[0].Envelope, 8)], _ => null);
        Assert.Equal(AnnotationReadStatus.Rejected, v8.Annotations[0].Status);
    }

    [Fact]
    public void TableCellBoxesAreNeverAutoWidth()
    {
        var table = TableAnnotation.Create(Guid.NewGuid(), new(10, 10), 2, 2, Style);
        table = table with { Cells = table.Cells.SetItem(0, new TableCell("칸", [Style with { Length = 1 }], TextAlignment.Left, [])) };
        Func<TextBoxAnnotation, Rect2> bounds = _ => new Rect2(0, 0, 10, 10);
        var layout = TableGeometry.Measure(table, bounds);
        for (int r = 0; r < table.Rows; r++)
            for (int c = 0; c < table.Columns; c++)
                Assert.False(TableGeometry.CellBox(table, layout, r, c).AutoWidth);
    }
}
