using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Serialization;

/// <summary>
/// v8 = v7 plus pen pressure (spec 7.6, docs/file-format.md): an <c>ink</c> annotation may carry <c>pressures</c>, one
/// number 0–1 per point. Every v7 field keeps its meaning and the header always has <c>tags</c> (possibly empty). A model
/// without pressure strokes is still written as v7 (or v6), so files without pressure stay readable by older versions;
/// older readers see a v8 header as unknown and keep the appearances read-only.
/// </summary>
public static class SchemaV8
{
    public const int Version = 8;

    public static SerializedDocument Serialize(DocumentModel model)
    {
        var pressured = model.AllAnnotations.OfType<InkStroke>().Where(s => s.HasPressure).ToDictionary(s => s.Id, s => s.Pressures);
        if (pressured.Count == 0) return SchemaV7.Serialize(model);
        if (ModelValidator.ValidateDocument(model) is { } error) throw new InvalidDataException(error);
        // v7 writes the rest; the pressures are added to the ink envelopes afterwards.
        var plain = model with
        {
            Pages = model.Pages.Select(p => p with
            {
                Annotations = p.Annotations.Select(a => a is InkStroke { HasPressure: true } s ? s with { Pressures = default } : a).ToImmutableList()
            }).ToImmutableList()
        };
        var data = SchemaV7.Serialize(plain);
        var header = JsonNode.Parse(data.Header)!.AsObject();
        header["schemaVersion"] = Version;
        header["tags"] ??= new JsonArray();
        byte[] headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, SchemaV1.JsonOptions);
        if (headerBytes.Length > SchemaV1.MaxHeaderBytes) throw new InvalidDataException("Document metadata exceeds its size limit.");
        var annotations = data.Annotations.Select(a =>
        {
            var root = JsonNode.Parse(a.Envelope, documentOptions: new() { MaxDepth = 16 })!.AsObject();
            root["schemaVersion"] = Version;
            if (pressured.TryGetValue(a.AnnotationId, out var pressures))
                root["annotation"]!.AsObject()["pressures"] = new JsonArray(pressures.Select(p => (JsonNode)JsonValue.Create(Math.Round(p, 4))).ToArray());
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(root, SchemaV1.JsonOptions);
            if (bytes.Length > SchemaV1.MaxEnvelopeBytes) throw new InvalidDataException("Annotation metadata exceeds its size limit.");
            return a with { Envelope = bytes };
        }).ToImmutableArray();
        return data with { Header = headerBytes, Annotations = annotations };
    }

    /// <summary>Reads v8, and v1–v7 through their own readers (which see no pressures).</summary>
    public static DocumentReadResult Read(byte[]? header, IReadOnlyList<byte[]> envelopes, Func<Guid, byte[]?> resources,
        IReadOnlyList<PageFrame>? actualPages = null)
    {
        if (header is { Length: > SchemaV1.MaxHeaderBytes } || SchemaV2.GetVersion(header) != Version)
            return SchemaV7.Read(header, envelopes, resources, actualPages);
        byte[] v7Header;
        try
        {
            if (JsonNode.Parse(header!, documentOptions: new() { MaxDepth = 16 }) is not JsonObject root || root["tags"] is not JsonArray) return Invalid(envelopes);
            root["schemaVersion"] = SchemaV7.Version;
            v7Header = JsonSerializer.SerializeToUtf8Bytes(root, SchemaV1.JsonOptions);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            return Invalid(envelopes);
        }
        // Pressures are taken out of each envelope before v7 reads it (v7 refuses unknown fields). An envelope of another
        // version than the header is rejected, as in v2–v7.
        var pressures = new Dictionary<Guid, ImmutableArray<double>>();
        var warnings = ImmutableArray.CreateBuilder<string>();
        var v7Envelopes = envelopes.Select(e =>
        {
            if (SchemaV2.GetVersion(e) != Version) return SchemaV2.SetVersion(e, 0);
            try
            {
                if (JsonNode.Parse(e, documentOptions: new() { MaxDepth = 16 }) is not JsonObject root) return SchemaV2.SetVersion(e, 0);
                root["schemaVersion"] = SchemaV7.Version;
                if (root["annotation"] is JsonObject annotation && annotation["pressures"] is { } node)
                {
                    annotation.Remove("pressures");
                    if (annotation["type"]?.GetValue<string>() != "ink" || annotation["id"] is not { } id) return SchemaV2.SetVersion(e, 0);
                    if (node is not JsonArray values || values.Any(v => v is null)) return SchemaV2.SetVersion(e, 0);
                    pressures[id.GetValue<Guid>()] = [.. values.Select(v => v!.GetValue<double>())];
                }
                return JsonSerializer.SerializeToUtf8Bytes(root, SchemaV1.JsonOptions);
            }
            catch (Exception x) when (x is JsonException or InvalidOperationException or ArgumentException or FormatException)
            {
                return SchemaV2.SetVersion(e, 0);
            }
        }).ToArray();
        var read = SchemaV7.Read(v7Header, v7Envelopes, resources, actualPages);
        if (read.Model is not { } model || pressures.Count == 0) return read;
        // A stroke keeps its pressures only when they fit its points; otherwise it is drawn at its even width.
        var pages = model.Pages.Select(p => p with
        {
            Annotations = p.Annotations.Select(a =>
            {
                if (a is not InkStroke ink || !pressures.TryGetValue(ink.Id, out var values)) return a;
                var with = ink with { Pressures = values };
                if (ModelValidator.ValidateAnnotation(with, _ => true) is null) return with;
                warnings.Add($"Pressures of stroke {ink.Id} are invalid and were dropped.");
                return a;
            }).ToImmutableList()
        }).ToImmutableList();
        return read with { Model = model with { Pages = pages }, Warnings = read.Warnings.AddRange(warnings) };
    }

    private static DocumentReadResult Invalid(IReadOnlyList<byte[]> envelopes) =>
        new(HeaderStatus.Invalid, null, envelopes.Select(_ => new AnnotationReadResult(AnnotationReadStatus.Rejected, null, "Invalid v8 data.")).ToImmutableArray(),
            ["Invalid v8 data; appearances remain read-only."]);
}
