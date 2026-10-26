using System.Collections.Immutable;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Serialization;

/// <summary>
/// v3 = v2 plus the optional <c>outline</c> of ink/freeHighlight strokes: pieces left by the area eraser (D08,
/// docs/file-format.md). Everything else keeps the v1/v2 meaning. Older readers see an unknown version (or an unknown
/// field) and keep the appearances read-only instead of losing the erased shape.
/// </summary>
public static class SchemaV3
{
    public const int Version = 3;

    public static SerializedDocument Serialize(DocumentModel model) => Serialize(model, allowShape: false);

    internal static SerializedDocument Serialize(DocumentModel model, bool allowShape, bool allowV6 = false)
    {
        var data = SchemaV1.Serialize(model, allowOutline: true, allowShape, allowV6);
        return data with
        {
            Header = SchemaV2.SetVersion(data.Header, Version),
            Annotations = data.Annotations.Select(a => a with { Envelope = SchemaV2.SetVersion(a.Envelope, Version) }).ToImmutableArray()
        };
    }

    /// <summary>Reads v3, and v2/v1 through their own readers (migration into the same model).</summary>
    public static DocumentReadResult Read(byte[]? header, IReadOnlyList<byte[]> envelopes, Func<Guid, byte[]?> resourcePayload,
        IReadOnlyList<PageFrame>? actualPages = null) => Read(header, envelopes, resourcePayload, actualPages, allowShape: false);

    internal static DocumentReadResult Read(byte[]? header, IReadOnlyList<byte[]> envelopes, Func<Guid, byte[]?> resourcePayload,
        IReadOnlyList<PageFrame>? actualPages, bool allowShape, bool allowV6 = false)
    {
        if (header is { Length: > SchemaV1.MaxHeaderBytes } || SchemaV2.GetVersion(header) != Version)
            return SchemaV2.Read(header, envelopes, resourcePayload, actualPages);
        // Envelopes of another version than the header are rejected, as in v2.
        return SchemaV1.Read(SchemaV2.SetVersion(header!, SchemaV1.Version),
            envelopes.Select(e => SchemaV2.GetVersion(e) == Version ? SchemaV2.SetVersion(e, SchemaV1.Version) : SchemaV2.SetVersion(e, 0)).ToArray(),
            resourcePayload, actualPages, allowOutline: true, allowShape, allowV6);
    }
}
