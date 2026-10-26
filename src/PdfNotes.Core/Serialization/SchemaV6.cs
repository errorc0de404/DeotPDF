using System.Collections.Immutable;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Serialization;

/// <summary>
/// v6 = v5 plus the <c>textMarkup</c> (underline/strikeout on PDF glyphs, spec 24.8) and <c>note</c> (sticky note, 24.9)
/// annotation types (D13i, docs/schema-v6.md). Every v5 field keeps its meaning. Older readers see an unknown version and
/// keep the appearances read-only; older writers refuse a model with the new types.
/// </summary>
public static class SchemaV6
{
    public const int Version = 6;

    public static SerializedDocument Serialize(DocumentModel model)
    {
        var data = SchemaV4.Serialize(model, allowShape: true, allowV6: true);
        return data with
        {
            Header = SchemaV2.SetVersion(data.Header, Version),
            Annotations = data.Annotations.Select(a => a with { Envelope = SchemaV2.SetVersion(a.Envelope, Version) }).ToImmutableArray()
        };
    }

    /// <summary>Reads v6, and v1–v5 through their own readers (which reject the new types).</summary>
    public static DocumentReadResult Read(byte[]? header, IReadOnlyList<byte[]> envelopes, Func<Guid, byte[]?> resources,
        IReadOnlyList<PageFrame>? actualPages = null)
    {
        if (header is { Length: > SchemaV1.MaxHeaderBytes } || SchemaV2.GetVersion(header) != Version)
            return SchemaV5.Read(header, envelopes, resources, actualPages);
        // Envelopes of another version than the header are rejected, as in v2–v5.
        return SchemaV4.Read(SchemaV2.SetVersion(header!, SchemaV4.Version),
            envelopes.Select(e => SchemaV2.GetVersion(e) == Version ? SchemaV2.SetVersion(e, SchemaV4.Version) : SchemaV2.SetVersion(e, 0)).ToArray(),
            resources, actualPages, allowShape: true, allowV6: true);
    }
}
