using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Serialization;

/// <summary>v4: independent source placement, persistent source identity, and scaled text layout metrics.</summary>
public static class SchemaV4
{
    public const int Version = 4;
    public static SerializedDocument Serialize(DocumentModel model) => Serialize(model, allowShape: false);

    internal static SerializedDocument Serialize(DocumentModel model, bool allowShape, bool allowV6 = false)
    {
        if (ModelValidator.ValidateDocument(model) is { } error) throw new InvalidDataException(error);
        var legacy = model with { Pages = model.Pages.Select(p => p with { Setup = p.Setup with { Content = null },
            Annotations = p.Annotations.Select(a => a is TextBoxAnnotation t ? t with { LayoutScale = 1 } : a).ToImmutableList() }).ToImmutableList() };
        var data = SchemaV3.Serialize(legacy, allowShape, allowV6);
        var header = JsonNode.Parse(data.Header)!.AsObject(); header["schemaVersion"] = Version;
        for (int i = 0; i < model.Pages.Count; i++)
        {
            var setup = model.Pages[i].Setup; var page = header["pages"]![i]!;
            page["source"] = new JsonObject { ["kind"] = setup.Source.Kind == PageSourceKind.Generated ? "generated" : "pdf", ["index"] = setup.Source.PdfPageIndex };
            if (setup.Content is { } c) page["content"] = new JsonObject { ["widthPt"] = c.WidthPt, ["heightPt"] = c.HeightPt,
                ["transform"] = JsonSerializer.SerializeToNode(c.Transform.ToArray()) };
        }
        return data with { Header = Bytes(header), Annotations = data.Annotations.Select(a =>
        {
            var envelope = JsonNode.Parse(a.Envelope)!.AsObject(); envelope["schemaVersion"] = Version;
            if (model.FindAnnotation(a.AnnotationId)!.Annotation is TextBoxAnnotation t) envelope["annotation"]!["layoutScale"] = t.LayoutScale;
            return a with { Envelope = Bytes(envelope) };
        }).ToImmutableArray() };
    }

    public static DocumentReadResult Read(byte[]? header, IReadOnlyList<byte[]> envelopes, Func<Guid, byte[]?> resources,
        IReadOnlyList<PageFrame>? actualPages = null) => Read(header, envelopes, resources, actualPages, allowShape: false);

    internal static DocumentReadResult Read(byte[]? header, IReadOnlyList<byte[]> envelopes, Func<Guid, byte[]?> resources,
        IReadOnlyList<PageFrame>? actualPages, bool allowShape, bool allowV6 = false)
    {
        if (header is { Length: > SchemaV1.MaxHeaderBytes } || SchemaV2.GetVersion(header) != Version)
            return SchemaV3.Read(header, envelopes, resources, actualPages);
        try
        {
            var root = JsonNode.Parse(header!, documentOptions: new() { MaxDepth = 16 })!.AsObject();
            var sources = new List<(PageSource Source, SourcePlacement? Content)>();
            foreach (var node in root["pages"]!.AsArray())
            {
                var p = node!.AsObject(); var s = p["source"]!.AsObject();
                if (s.Count != 2 || !s.ContainsKey("kind") || !s.ContainsKey("index")) throw new InvalidDataException();
                var source = s["kind"]!.GetValue<string>() switch { "generated" when s["index"] is null => PageSource.Generated,
                    "pdf" => PageSource.Pdf(s["index"]!.GetValue<int>()), _ => throw new InvalidDataException() };
                SourcePlacement? content = null;
                if (p["content"] is { } c)
                {
                    if (c.AsObject().Count != 3) throw new InvalidDataException();
                    content = new(c["widthPt"]!.GetValue<double>(), c["heightPt"]!.GetValue<double>(),
                        Affine.FromArray(c["transform"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray()));
                }
                sources.Add((source, content)); p.Remove("source"); p.Remove("content");
            }
            root["schemaVersion"] = 3;
            var scales = new Dictionary<Guid, double>();
            var migrated = envelopes.Select(e =>
            {
                if (SchemaV2.GetVersion(e) != Version) return SchemaV2.SetVersion(e, 0);
                var n = JsonNode.Parse(e, documentOptions: new() { MaxDepth = 16 })!.AsObject(); var a = n["annotation"]!.AsObject();
                if (a.ContainsKey("layoutScale"))
                {
                    if (a["type"]!.GetValue<string>() != "textBox") throw new InvalidDataException();
                    scales.Add(a["id"]!.GetValue<Guid>(), a["layoutScale"]!.GetValue<double>()); a.Remove("layoutScale");
                }
                n["schemaVersion"] = 3; return Bytes(n);
            }).ToArray();
            var read = SchemaV3.Read(Bytes(root), migrated, resources, actualPages, allowShape, allowV6);
            if (read.Model is not { } model) return read;
            model = model with { Pages = model.Pages.Select((p, i) => p with { Setup = p.Setup with { Source = sources[i].Source, Content = sources[i].Content },
                Annotations = p.Annotations.Select(a => a is TextBoxAnnotation t ? t with { LayoutScale = scales.GetValueOrDefault(t.Id, 1) } : a).ToImmutableList() }).ToImmutableList() };
            if (ModelValidator.ValidateDocument(model) is not null) throw new InvalidDataException();
            return read with { Model = model };
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or InvalidOperationException or ArgumentException or NullReferenceException or KeyNotFoundException or FormatException)
        {
            return new(HeaderStatus.Invalid, null, envelopes.Select(_ => new AnnotationReadResult(AnnotationReadStatus.Rejected, null, "Invalid v4 data.")).ToImmutableArray(), ["Invalid v4 data; appearances remain read-only."]);
        }
    }
    private static byte[] Bytes(JsonNode node) => JsonSerializer.SerializeToUtf8Bytes(node, SchemaV1.JsonOptions);
}
