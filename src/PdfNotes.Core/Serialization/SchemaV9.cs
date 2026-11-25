using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Serialization;

/// <summary>
/// v9 = v8 plus the <c>table</c> annotation type and the text box <c>autoWidth</c> field (1.4, docs/file-format.md). Every v8
/// field keeps its meaning and the header always has <c>tags</c> (possibly empty). A model without tables and auto-width text
/// boxes is still written as v8 or lower, so such files stay readable by older versions; older readers see a v9 header as
/// unknown and keep the appearances read-only.
/// </summary>
public static class SchemaV9
{
    public const int Version = 9;

    public static SerializedDocument Serialize(DocumentModel model)
    {
        if (!model.AllAnnotations.Any(a => a is TableAnnotation or TextBoxAnnotation { AutoWidth: true })) return SchemaV8.Serialize(model);
        // v8 (or v7/v6 when there are no pressures or tags) writes everything with v9 data allowed; v9 only renumbers it.
        var data = SchemaV8.Serialize(model, allowV9: true);
        var header = JsonNode.Parse(data.Header)!.AsObject();
        header["schemaVersion"] = Version;
        header["tags"] ??= new JsonArray();
        byte[] headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, SchemaV1.JsonOptions);
        if (headerBytes.Length > SchemaV1.MaxHeaderBytes) throw new InvalidDataException("Document metadata exceeds its size limit.");
        return data with
        {
            Header = headerBytes,
            Annotations = data.Annotations.Select(a => a with { Envelope = SchemaV2.SetVersion(a.Envelope, Version) }).ToImmutableArray()
        };
    }

    /// <summary>Reads v9, and v1–v8 through their own readers (which reject tables and auto-width text boxes).</summary>
    public static DocumentReadResult Read(byte[]? header, IReadOnlyList<byte[]> envelopes, Func<Guid, byte[]?> resources,
        IReadOnlyList<PageFrame>? actualPages = null)
    {
        if (header is { Length: > SchemaV1.MaxHeaderBytes } || SchemaV2.GetVersion(header) != Version)
            return SchemaV8.Read(header, envelopes, resources, actualPages);
        // Envelopes of another version than the header are rejected, as in v2–v8.
        return SchemaV8.Read(SchemaV2.SetVersion(header!, SchemaV8.Version),
            envelopes.Select(e => SchemaV2.GetVersion(e) == Version ? SchemaV2.SetVersion(e, SchemaV8.Version) : SchemaV2.SetVersion(e, 0)).ToArray(),
            resources, actualPages, allowV9: true);
    }
}
