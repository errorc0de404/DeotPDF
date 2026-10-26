using System.Collections.Immutable;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Serialization;

/// <summary>
/// v5 = v4 plus the <c>shape</c> annotation type (D11 shape tool, docs/file-format.md). Every v4 field keeps its
/// meaning. Older readers see an unknown version and keep all appearances read-only instead of dropping shapes;
/// older writers refuse a model with shapes.
/// </summary>
public static class SchemaV5
{
    public const int Version = 5;

    public static SerializedDocument Serialize(DocumentModel model)
    {
        var data = SchemaV4.Serialize(model, allowShape: true);
        return data with
        {
            Header = SchemaV2.SetVersion(data.Header, Version),
            Annotations = data.Annotations.Select(a => a with { Envelope = SchemaV2.SetVersion(a.Envelope, Version) }).ToImmutableArray()
        };
    }

    /// <summary>Reads v5, and v1–v4 through their own readers (which reject shapes).</summary>
    public static DocumentReadResult Read(byte[]? header, IReadOnlyList<byte[]> envelopes, Func<Guid, byte[]?> resources,
        IReadOnlyList<PageFrame>? actualPages = null)
    {
        if (header is { Length: > SchemaV1.MaxHeaderBytes } || SchemaV2.GetVersion(header) != Version)
            return SchemaV4.Read(header, envelopes, resources, actualPages);
        // Envelopes of another version than the header are rejected, as in v2–v4.
        return SchemaV4.Read(SchemaV2.SetVersion(header!, SchemaV4.Version),
            envelopes.Select(e => SchemaV2.GetVersion(e) == Version ? SchemaV2.SetVersion(e, SchemaV4.Version) : SchemaV2.SetVersion(e, 0)).ToArray(),
            resources, actualPages, allowShape: true);
    }
}
