using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Serialization;

/// <summary>v2 retains v1 geometry/DTOs and adds the PDF searchable-text ownership contract.
/// Older readers must keep v2 appearances read-only instead of leaving stale text when editing them.</summary>
public static class SchemaV2
{
    public const int Version = 2;
    public static SerializedDocument Serialize(DocumentModel model)
    {
        var data = SchemaV1.Serialize(model);
        return data with
        {
            Header = SetVersion(data.Header, Version),
            Annotations = data.Annotations.Select(a => a with { Envelope = SetVersion(a.Envelope, Version) }).ToImmutableArray()
        };
    }
    public static DocumentReadResult Read(byte[]? header, IReadOnlyList<byte[]> envelopes, Func<Guid, byte[]?> resourcePayload,
        IReadOnlyList<PageFrame>? actualPages = null)
    {
        if (header is { Length: > SchemaV1.MaxHeaderBytes } || GetVersion(header) != Version) return SchemaV1.Read(header, envelopes, resourcePayload, actualPages);
        // Migration into the common immutable model. Do not accept mismatched envelope versions.
        return SchemaV1.Read(SetVersion(header!, SchemaV1.Version),
            envelopes.Select(e => GetVersion(e) == Version ? SetVersion(e, SchemaV1.Version) : SetVersion(e, 0)).ToArray(), resourcePayload, actualPages);
    }
    internal static int? GetVersion(byte[]? bytes)
    {
        if (bytes is null || bytes.Length > SchemaV1.MaxEnvelopeBytes) return null;
        try { return JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = 16 })?["schemaVersion"]?.GetValue<int>(); }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException or ArgumentException) { return null; }
    }
    internal static byte[] SetVersion(byte[] bytes, int version)
    {
        try
        {
            if (bytes.Length > SchemaV1.MaxEnvelopeBytes) return bytes;
            var root = JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = 16 })?.AsObject();
            if (root is null) return bytes;
            root["schemaVersion"] = version;
            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(SchemaV1.JsonOptions));
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or ArgumentException) { return bytes; }
    }
}
