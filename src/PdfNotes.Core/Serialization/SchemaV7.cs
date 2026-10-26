using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Serialization;

/// <summary>
/// v7 = v6 plus the document's annotation tag table (author, batchId, sourceKey, groupKey; cli_plan 6, docs/file-format.md)
/// as the header field <c>tags</c>. Every v6 field and every annotation envelope keeps its meaning. A model without tags
/// is still written as v6, so files the CLI never tagged stay readable by older versions of the app; older readers see a
/// v7 header as unknown and keep the appearances read-only.
/// </summary>
public static class SchemaV7
{
    public const int Version = 7;

    public static SerializedDocument Serialize(DocumentModel model)
    {
        var data = SchemaV6.Serialize(model);
        var tags = model.LiveTags.ToArray();
        if (tags.Length == 0) return data;
        var header = JsonNode.Parse(data.Header)!.AsObject();
        header["schemaVersion"] = Version;
        header["tags"] = new JsonArray(tags.Select(t => (JsonNode)Tag(t.Annotation.Id, t.Tag)).ToArray());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(header, SchemaV1.JsonOptions);
        if (bytes.Length > SchemaV1.MaxHeaderBytes) throw new InvalidDataException("Document metadata exceeds its size limit.");
        return data with
        {
            Header = bytes,
            Annotations = data.Annotations.Select(a => a with { Envelope = SchemaV2.SetVersion(a.Envelope, Version) }).ToImmutableArray()
        };
    }

    /// <summary>Reads v7, and v1–v6 through their own readers (which see no tags).</summary>
    public static DocumentReadResult Read(byte[]? header, IReadOnlyList<byte[]> envelopes, Func<Guid, byte[]?> resources,
        IReadOnlyList<PageFrame>? actualPages = null)
    {
        if (header is { Length: > SchemaV1.MaxHeaderBytes } || SchemaV2.GetVersion(header) != Version)
            return SchemaV6.Read(header, envelopes, resources, actualPages);
        Dictionary<Guid, AnnotationTag> tags;
        byte[] v6Header;
        try
        {
            var root = JsonNode.Parse(header!, documentOptions: new() { MaxDepth = 16 })!.AsObject();
            tags = [];
            foreach (var node in root["tags"]?.AsArray() ?? throw new InvalidDataException())
            {
                var t = node!.AsObject();
                if (t.Any(p => p.Key is not ("annotationId" or "author" or "batchId" or "sourceKey" or "groupKey"))) throw new InvalidDataException();
                var tag = new AnnotationTag(t["author"]!.GetValue<string>(), t["batchId"]?.GetValue<string>(), t["sourceKey"]?.GetValue<string>(),
                    t["groupKey"]?.GetValue<string>());
                if (tag.Validate() is not null || !tags.TryAdd(t["annotationId"]!.GetValue<Guid>(), tag)) throw new InvalidDataException();
            }
            root.Remove("tags");
            root["schemaVersion"] = SchemaV6.Version;
            v6Header = JsonSerializer.SerializeToUtf8Bytes(root, SchemaV1.JsonOptions);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or InvalidOperationException or ArgumentException or NullReferenceException or FormatException)
        {
            return new(HeaderStatus.Invalid, null, envelopes.Select(_ => new AnnotationReadResult(AnnotationReadStatus.Rejected, null, "Invalid v7 data.")).ToImmutableArray(),
                ["Invalid v7 data; appearances remain read-only."]);
        }
        // Envelopes of another version than the header are rejected, as in v2–v6.
        var read = SchemaV6.Read(v6Header,
            envelopes.Select(e => SchemaV2.GetVersion(e) == Version ? SchemaV2.SetVersion(e, SchemaV6.Version) : SchemaV2.SetVersion(e, 0)).ToArray(),
            resources, actualPages);
        if (read.Model is not { } model) return read;
        // A tag of an annotation that was not restored (rejected or blank) is dropped with it.
        var live = model.AllAnnotations.Select(a => a.Id).ToHashSet();
        return read with { Model = model with { Tags = tags.Where(t => live.Contains(t.Key)).ToImmutableDictionary() } };
    }

    private static JsonObject Tag(Guid id, AnnotationTag tag)
    {
        var node = new JsonObject { ["annotationId"] = id, ["author"] = tag.Author };
        if (tag.BatchId is not null) node["batchId"] = tag.BatchId;
        if (tag.SourceKey is not null) node["sourceKey"] = tag.SourceKey;
        if (tag.GroupKey is not null) node["groupKey"] = tag.GroupKey;
        return node;
    }
}
