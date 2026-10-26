using System.Collections.Immutable;
using System.Text.Json;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Writing;

namespace PdfNotes.Editor.Services;

/// <summary>
/// Annotations on the system clipboard (spec 17, D14d): the selection as a schema v6 packet under
/// <see cref="Format"/>, read back only when every annotation restores and is supported.
/// </summary>
public static class AnnotationTransfer
{
    public const string Format = "PdfNotes.Annotations.v1";

    /// <summary>Clipboard packets larger than this are refused before parsing.</summary>
    public const int MaxPacketChars = 384 * 1024 * 1024;

    private sealed record Packet(byte[] Header, byte[][] Envelopes, Dictionary<Guid, byte[]> Resources);

    /// <summary>The packet text of annotations <paramref name="ids"/> of page <paramref name="pageId"/>, or null when nothing can be copied.</summary>
    public static string? Encode(DocumentModel model, Guid pageId, IEnumerable<Guid> ids)
    {
        var data = AnnotationClipboard.Capture(model, pageId, ids);
        if (data.Annotations.Length == 0) return null;
        var page = model.GetPage(pageId) with { Annotations = data.Annotations.ToImmutableList() };
        var encoded = SchemaV6.Serialize(model with { Pages = [page], Resources = data.Resources.ToImmutableDictionary(r => r.Id) });
        return JsonSerializer.Serialize(new Packet(encoded.Header, encoded.Annotations.Select(a => a.Envelope).ToArray(),
            encoded.Resources.ToDictionary(r => r.Id, r => r.Payload.ToArray())));
    }

    /// <summary>
    /// The annotations of a packet. Throws <see cref="InvalidDataException"/> (or <see cref="JsonException"/>) for anything
    /// incomplete; <paramref name="checkImage"/> lets the platform check that image resources decode.
    /// </summary>
    public static AnnotationClipboard Decode(string? text, Action<byte[]> checkImage)
    {
        if (text is null || text.Length > MaxPacketChars) throw new InvalidDataException();
        var packet = JsonSerializer.Deserialize<Packet>(text) ?? throw new InvalidDataException();
        if (packet.Header is null || packet.Envelopes is null || packet.Resources is null || packet.Envelopes.Any(e => e is null) || packet.Resources.Values.Any(v => v is null))
            throw new InvalidDataException();
        var read = SchemaV6.Read(packet.Header, packet.Envelopes, id => packet.Resources.GetValueOrDefault(id));
        if (read.Model is not { } model || read.HeaderStatus != HeaderStatus.Valid || read.Annotations.Any(a => a.Status != AnnotationReadStatus.Restored) ||
            model.AllAnnotations.Any(a => !AnnotationAppearance.IsSupported(a.Kind))) throw new InvalidDataException();
        foreach (var r in model.Resources.Values) checkImage(r.Payload.ToArray());
        return AnnotationClipboard.Capture(model, model.Pages[0].Id, model.AllAnnotations.Select(a => a.Id));
    }
}

/// <summary>Where inserted images go (spec 10, D14d).</summary>
public static class ImagePlacement
{
    /// <summary>
    /// An image annotation of natural size (<paramref name="pixelWidth"/> at <paramref name="dpi"/>, 96 when unknown) fitted
    /// into 80 % of the page, centered on <paramref name="center"/> (null: the page center).
    /// </summary>
    public static ImageAnnotation Place(Resource resource, int pixelWidth, int pixelHeight, double dpi, Page page, Point2? center = null)
    {
        double width = pixelWidth * 72 / (dpi > 0 ? dpi : 96);
        double height = width * pixelHeight / pixelWidth;
        double fit = Math.Min(1, Math.Min(page.Setup.WidthPt * .8 / width, page.Setup.HeightPt * .8 / height));
        width *= fit; height *= fit;
        var offset = center is { } c ? Affine.Translation(c.X - width / 2, c.Y - height / 2)
            : Affine.Translation((page.Setup.WidthPt - width) / 2, (page.Setup.HeightPt - height) / 2);
        return new ImageAnnotation(Guid.NewGuid(), false, offset, resource.Id, new(0, 0, width, height));
    }
}

/// <summary>
/// Pages on the system clipboard for other windows (spec 11.4, D14g): the copied pages as one editable PDF of their own
/// (what page export writes), with the copying document's session. A window pastes them like an import (11.9); the
/// copying window itself pastes its in-memory page clipboard instead.
/// </summary>
public static class PageTransfer
{
    public const string Format = "PdfNotes.Pages.v1";

    /// <summary>Packets larger than this are refused (a base64 PDF).</summary>
    public const int MaxPacketChars = 512 * 1024 * 1024;

    private sealed record Packet(Guid Session, byte[] Pdf);

    public static string Encode(Guid session, byte[] pdf) => JsonSerializer.Serialize(new Packet(session, pdf));

    /// <summary>
    /// The session and PDF of a packet, or null for anything that is not one. An empty PDF is a copy still being written:
    /// the copying window claims the clipboard at once and puts the PDF there when it is ready.
    /// </summary>
    public static (Guid Session, byte[] Pdf)? Decode(string? text)
    {
        if (text is null || text.Length > MaxPacketChars) return null;
        try { return JsonSerializer.Deserialize<Packet>(text) is { Pdf: not null } p ? (p.Session, p.Pdf) : null; }
        catch (JsonException) { return null; }
    }
}
