using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;

namespace PdfNotes.Tests.Core;

public class SchemaV1Tests
{
    private static DocumentReadResult ReadBack(SerializedDocument data, IReadOnlyList<byte[]>? envelopes = null,
        Func<Guid, byte[]?>? payloads = null, IReadOnlyList<PageFrame>? frames = null, byte[]? header = null) =>
        SchemaV1.Read(header ?? data.Header, envelopes ?? data.Annotations.Select(a => a.Envelope).ToArray(),
            payloads ?? (id => data.Resources.FirstOrDefault(r => r.Id == id)?.Payload.ToArray()), frames);

    private static byte[] EditEnvelope(byte[] envelope, Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(envelope)!.AsObject();
        edit(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    [Fact]
    public void EveryAnnotationKindRoundTripsExactlyIncludingIdsOrderAndAwkwardNumbers()
    {
        var model = TestModels.AllKinds(out var resource);
        var data = SchemaV1.Serialize(model);
        var result = ReadBack(data);

        Assert.Equal(HeaderStatus.Valid, result.HeaderStatus);
        Assert.Empty(result.Warnings);
        Assert.All(result.Annotations, a => Assert.Equal(AnnotationReadStatus.Restored, a.Status));
        var restored = result.Model!;
        Assert.Equal(model.Id, restored.Id);
        Assert.Equal(model.Pages.Select(p => p.Id), restored.Pages.Select(p => p.Id));
        for (int p = 0; p < model.Pages.Count; p++)
        {
            Assert.Equal(model.Pages[p].Setup with { Source = PageSource.Pdf(p) }, restored.Pages[p].Setup);
            Assert.Equal(model.Pages[p].Annotations.Count, restored.Pages[p].Annotations.Count);
            for (int i = 0; i < model.Pages[p].Annotations.Count; i++)
                AssertSameAnnotation(model.Pages[p].Annotations[i], restored.Pages[p].Annotations[i]);
        }
        Assert.Equal(resource.Payload.AsEnumerable(), restored.Resources[resource.Id].Payload.AsEnumerable());
        // Serializing the restored model reproduces the exact same bytes: no drift on repeated saves.
        var again = SchemaV1.Serialize(restored);
        Assert.Equal(data.Header, again.Header);
        Assert.Equal(data.Annotations.Select(a => Convert.ToBase64String(a.Envelope)), again.Annotations.Select(a => Convert.ToBase64String(a.Envelope)));
    }

    [Fact]
    public void HeaderDeclaresAppVersionUnitsAndIdsAndEnvelopesCarryDocumentAndPageIds()
    {
        var model = TestModels.AllKinds(out _);
        var data = SchemaV1.Serialize(model);
        var header = JsonNode.Parse(data.Header)!;
        Assert.Equal("PdfNotes", (string?)header["app"]);
        Assert.Equal(1, (int?)header["schemaVersion"]);
        Assert.Equal(SchemaV1.CoordinateSpace, (string?)header["coordinateSpace"]);
        Assert.Equal(SchemaV1.TextIndexUnit, (string?)header["textIndexUnit"]);
        Assert.Equal(model.Id, Guid.Parse((string)header["documentId"]!));
        var envelope = JsonNode.Parse(data.Annotations[0].Envelope)!;
        Assert.Equal(model.Id, Guid.Parse((string)envelope["documentId"]!));
        Assert.Equal(model.Pages[0].Id, Guid.Parse((string)envelope["pageId"]!));
        Assert.Equal("ink", (string?)envelope["annotation"]!["type"]);
        Assert.DoesNotContain("source", header["pages"]![0]!.AsObject().Select(p => p.Key)); // page source is positional in the PDF
    }

    [Fact]
    public void UnsupportedOrMalformedHeaderKeepsEverythingReadOnly()
    {
        var data = SchemaV1.Serialize(TestModels.AllKinds(out _));
        var future = JsonNode.Parse(data.Header)!.AsObject();
        future["schemaVersion"] = 2;
        var unsupported = ReadBack(data, header: Encoding.UTF8.GetBytes(future.ToJsonString()));
        Assert.Equal(HeaderStatus.Unsupported, unsupported.HeaderStatus);
        Assert.Null(unsupported.Model);
        Assert.All(unsupported.Annotations, a => Assert.Equal(AnnotationReadStatus.Rejected, a.Status));
        Assert.Single(unsupported.Warnings);

        var extra = JsonNode.Parse(data.Header)!.AsObject();
        extra["writtenByNewerApp"] = true; // unknown members are rejected so newer data is never dropped on re-save
        Assert.Equal(HeaderStatus.Invalid, ReadBack(data, header: Encoding.UTF8.GetBytes(extra.ToJsonString())).HeaderStatus);
        Assert.Equal(HeaderStatus.Invalid, ReadBack(data, header: "{broken"u8.ToArray()).HeaderStatus);
        Assert.Equal(HeaderStatus.Invalid, ReadBack(data, header: "{\"app\":\"Other\",\"schemaVersion\":1}"u8.ToArray()).HeaderStatus);
        var noHeader = SchemaV1.Read(null, [data.Annotations[0].Envelope], _ => null);
        Assert.Null(noHeader.Model);
        Assert.Empty(noHeader.Warnings);
        Assert.Equal(AnnotationReadStatus.Rejected, noHeader.Annotations.Single().Status);
    }

    [Fact]
    public void PageGeometryDifferentFromThePdfInvalidatesTheHeader()
    {
        var model = TestModels.AllKinds(out _);
        var data = SchemaV1.Serialize(model);
        var frames = model.Pages.Select(p => p.Setup.Frame).ToArray();
        Assert.Equal(HeaderStatus.Valid, ReadBack(data, frames: frames).HeaderStatus);
        Assert.Equal(HeaderStatus.Valid, ReadBack(data, frames: [new(frames[0].WidthPt + 5e-4, frames[0].HeightPt, 0), frames[1]]).HeaderStatus);
        Assert.Equal(HeaderStatus.Invalid, ReadBack(data, frames: [frames[0], new(frames[1].WidthPt, frames[1].HeightPt, 180)]).HeaderStatus);
        Assert.Equal(HeaderStatus.Invalid, ReadBack(data, frames: [frames[0]]).HeaderStatus);
    }

    public static TheoryData<string> CorruptAnnotationEdits =>
    [
        "unknown-member", "unknown-type", "negative-width", "opacity-above-one", "scaled-transform",
        "wrong-document", "wrong-page", "bad-color", "odd-point-list", "number-overflow", "null-transform"
    ];

    [Theory]
    [MemberData(nameof(CorruptAnnotationEdits))]
    public void InvalidAnnotationIsRejectedAloneWhileTheRestIsRestored(string corruption)
    {
        var model = TestModels.AllKinds(out _);
        var data = SchemaV1.Serialize(model);
        var envelopes = data.Annotations.Select(a => a.Envelope).ToArray();
        envelopes[0] = corruption == "number-overflow"
            ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(envelopes[0]).Replace("\"widthPt\":2", "\"widthPt\":1e400"))
            : EditEnvelope(envelopes[0], e =>
            {
                var a = e["annotation"]!.AsObject();
                switch (corruption)
                {
                    case "unknown-member": a["pressure"] = 0.5; break;
                    case "unknown-type": a["type"] = "hologram"; break;
                    case "negative-width": a["widthPt"] = -1; break;
                    case "opacity-above-one": a["opacity"] = 1.5; break;
                    case "scaled-transform": a["transform"] = new JsonArray(2, 0, 0, 2, 0, 0); break;
                    case "wrong-document": e["documentId"] = Guid.NewGuid().ToString(); break;
                    case "wrong-page": e["pageId"] = model.Pages[1].Id.ToString(); break;
                    case "bad-color": a["color"] = "red"; break;
                    case "odd-point-list": a["points"] = new JsonArray(1, 2, 3); break;
                    case "null-transform": a["transform"] = null; break;
                }
            });
        var result = ReadBack(data, envelopes);
        Assert.Equal(HeaderStatus.Valid, result.HeaderStatus);
        Assert.Equal(AnnotationReadStatus.Rejected, result.Annotations[0].Status);
        Assert.All(result.Annotations.Skip(1), a => Assert.Equal(AnnotationReadStatus.Restored, a.Status));
        Assert.Equal(model.AllAnnotations.Count() - 1, result.Model!.AllAnnotations.Count());
        Assert.DoesNotContain(result.Model.AllAnnotations, a => a.Id == model.Pages[0].Annotations[0].Id);
        Assert.Contains(result.Warnings, w => w.Contains("could not be restored"));
    }

    [Fact]
    public void TextRangeSplittingAGraphemeOrSurrogatePairIsRejected()
    {
        var model = TestModels.AllKinds(out _);
        var box = model.Pages[0].Annotations.OfType<TextBoxAnnotation>().Single();
        int emoji = box.Text.IndexOf("😀", StringComparison.Ordinal);
        var data = SchemaV1.Serialize(model);
        int index = data.Annotations.ToList().FindIndex(a => a.AnnotationId == box.Id);
        foreach (int split in new[] { emoji + 1, box.Text.IndexOf('́') }) // inside 😀, between e and its combining accent
        {
            var envelopes = data.Annotations.Select(a => a.Envelope).ToArray();
            envelopes[index] = EditEnvelope(envelopes[index], e =>
            {
                var runs = e["annotation"]!["runs"]!.AsArray();
                runs[1]!["length"] = split - 3;
                runs[2]!["start"] = split;
                runs[2]!["length"] = box.Text.Length - split;
            });
            Assert.Equal(AnnotationReadStatus.Rejected, ReadBack(data, envelopes).Annotations[index].Status);
        }
    }

    [Fact]
    public void DuplicateUnlistedAndForeignEnvelopesAreRejected()
    {
        var model = TestModels.AllKinds(out _);
        var data = SchemaV1.Serialize(model);
        var other = SchemaV1.Serialize(TestModels.AllKinds(out _));
        var envelopes = data.Annotations.Select(a => a.Envelope).Append(data.Annotations[1].Envelope).Append(other.Annotations[0].Envelope).ToArray();
        var result = ReadBack(data, envelopes);
        Assert.Equal(AnnotationReadStatus.Rejected, result.Annotations[^2].Status); // duplicate of an already restored id
        Assert.Equal(AnnotationReadStatus.Rejected, result.Annotations[^1].Status); // other document
        Assert.Equal(model.AllAnnotations.Count(), result.Model!.AllAnnotations.Count());
    }

    [Fact]
    public void DamagedImageResourceRejectsOnlyTheImage()
    {
        var model = TestModels.AllKinds(out var resource);
        var data = SchemaV1.Serialize(model);
        var damaged = resource.Payload.ToArray();
        damaged[^1] ^= 0xFF;
        var result = ReadBack(data, payloads: id => id == resource.Id ? damaged : null);
        var image = model.AllAnnotations.OfType<ImageAnnotation>().Single();
        Assert.Equal(AnnotationReadStatus.Rejected, result.Annotations.Single(a => a.AnnotationId == image.Id).Status);
        Assert.Equal(model.AllAnnotations.Count() - 1, result.Model!.AllAnnotations.Count());
        Assert.Empty(result.Model.Resources);
        Assert.Contains(result.Warnings, w => w.Contains("image resource"));
        Assert.Equal(HeaderStatus.Valid, ReadBack(data, payloads: _ => null).HeaderStatus);
    }

    [Fact]
    public void BlankTextBoxesAreNeverWrittenAndAreDiscardedWhenFoundOnRead()
    {
        var model = TestModels.AllKinds(out _);
        var box = model.Pages[0].Annotations.OfType<TextBoxAnnotation>().Single();
        var blank = box with { Text = " \t\n", Runs = [box.Runs[0] with { Length = 3 }], Highlights = [] };
        var withBlank = model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Annotations = model.Pages[0].Annotations.Replace(box, blank) }) };
        Assert.Throws<InvalidDataException>(() => SchemaV1.Serialize(withBlank));

        var data = SchemaV1.Serialize(model);
        int index = data.Annotations.ToList().FindIndex(a => a.AnnotationId == box.Id);
        var envelopes = data.Annotations.Select(a => a.Envelope).ToArray();
        envelopes[index] = EditEnvelope(envelopes[index], e =>
        {
            var a = e["annotation"]!.AsObject();
            a["text"] = "  ";
            a["runs"] = new JsonArray(JsonNode.Parse("{\"start\":0,\"length\":2,\"fontFamily\":\"Malgun Gothic\",\"sizePt\":12,\"color\":\"#000000\",\"bold\":false,\"italic\":false,\"underline\":false,\"strikethrough\":false}"));
            a["highlights"] = new JsonArray();
        });
        var result = ReadBack(data, envelopes);
        Assert.Equal(AnnotationReadStatus.DiscardedEmpty, result.Annotations[index].Status);
        Assert.DoesNotContain(result.Model!.AllAnnotations, a => a.Id == box.Id);
        Assert.Empty(result.Warnings); // an intentionally discarded blank box is not "missing"
    }

    [Fact]
    public void ModelValidatorRejectsBrokenReferencesAndNumbers()
    {
        var model = TestModels.AllKinds(out var resource);
        Assert.Null(ModelValidator.ValidateDocument(model));
        Assert.NotNull(ModelValidator.ValidateDocument(model with { Resources = ImmutableDictionary<Guid, Resource>.Empty }));
        Assert.NotNull(ModelValidator.ValidateDocument(model with { Pages = [] }));
        var page = model.Pages[0];
        Assert.NotNull(ModelValidator.ValidateDocument(model with { Pages = model.Pages.Add(page) })); // duplicate ids
        Assert.NotNull(ModelValidator.ValidatePageSetup(page.Setup with { Rotation = 45 }));
        Assert.NotNull(ModelValidator.ValidatePageSetup(page.Setup with { WidthPt = double.PositiveInfinity }));
        var line = model.Pages[1].Annotations.OfType<LineAnnotation>().Single();
        Assert.NotNull(ModelValidator.ValidateAnnotation(line with { LineKind = LineKind.Quadratic }, _ => true));
        var highlight = model.Pages[1].Annotations.OfType<PdfTextHighlight>().Single();
        Assert.NotNull(ModelValidator.ValidateAnnotation(highlight with { Transform = Affine.Translation(1, 0) }, _ => true));
        Assert.NotNull(ModelValidator.ValidateAnnotation(highlight with { HeightMode = HighlightHeightMode.Auto }, _ => true)); // manual height left over
        Assert.NotNull(ModelValidator.ValidateResource(resource with { MediaType = "image/svg+xml" }, false));
        Assert.NotNull(ModelValidator.ValidateResource(Resource.Create(Guid.NewGuid(), "image/jpeg", TestModels.TinyPng), false));
    }

    private static void AssertSameAnnotation(Annotation expected, Annotation actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Locked, actual.Locked);
        Assert.Equal(expected.Transform, actual.Transform); // exact: JSON doubles round-trip bit for bit
        switch (expected)
        {
            case StrokeAnnotation s:
                var rs = (StrokeAnnotation)actual;
                Assert.Equal(s.Points.AsEnumerable(), rs.Points.AsEnumerable()); // ImmutableArray equality is by reference
                Assert.Equal((s.WidthPt, s.Color, s.Opacity), (rs.WidthPt, rs.Color, rs.Opacity));
                break;
            case LineAnnotation l:
                var rl = (LineAnnotation)actual;
                Assert.Equal(l.Controls.AsEnumerable(), rl.Controls.AsEnumerable());
                Assert.Equal(l with { Controls = rl.Controls }, rl);
                break;
            case PdfTextHighlight h:
                var rh = (PdfTextHighlight)actual;
                Assert.Equal(h.Quads.AsEnumerable(), rh.Quads.AsEnumerable());
                Assert.Equal(h with { Quads = rh.Quads }, rh);
                break;
            case TextBoxAnnotation t:
                var rt = (TextBoxAnnotation)actual;
                Assert.Equal(t.Runs.AsEnumerable(), rt.Runs.AsEnumerable());
                Assert.Equal(t.Highlights.AsEnumerable(), rt.Highlights.AsEnumerable());
                Assert.Equal(t with { Runs = rt.Runs, Highlights = rt.Highlights }, rt);
                break;
            case ImageAnnotation i:
                Assert.Equal(i, actual);
                break;
            default:
                throw new InvalidOperationException("Unhandled kind.");
        }
    }
}
