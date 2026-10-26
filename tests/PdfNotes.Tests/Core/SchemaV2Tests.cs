using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;

namespace PdfNotes.Tests.Core;

public sealed class SchemaV2Tests
{
    private static DocumentModel Model()
    {
        var session = new DocumentSession(DocumentModel.Create([new(400, 500, 0, PageSource.Generated, PageBackground.Blank)]));
        session.Execute(DocumentEdits.AddAnnotation(session.Current, session.Current.Pages[0].Id, TextEditingTests.Box("한글 😀")));
        return session.Current;
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void CurrentReaderMigratesLegacyAndNewMetadataWithoutGeometryChanges(bool legacy)
    {
        var model = Model(); var data = legacy ? SchemaV1.Serialize(model) : SchemaV2.Serialize(model);
        var read = SchemaV2.Read(data.Header, data.Annotations.Select(a => a.Envelope).ToArray(), _ => null);
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus); Assert.All(read.Annotations, a => Assert.Equal(AnnotationReadStatus.Restored, a.Status));
        Assert.Equal(SchemaV2.Serialize(model).Header, SchemaV2.Serialize(read.Model!).Header);
        Assert.Equal(SchemaV2.Serialize(model).Annotations[0].Envelope, SchemaV2.Serialize(read.Model!).Annotations[0].Envelope);
        if (!legacy) Assert.Equal(HeaderStatus.Unsupported, SchemaV1.Read(data.Header, data.Annotations.Select(a => a.Envelope).ToArray(), _ => null).HeaderStatus);
    }
    [Fact] public void MixedEnvelopeVersionIsRejectedInsteadOfSilentlyMigrated()
    {
        var model = Model(); var current = SchemaV2.Serialize(model); var legacy = SchemaV1.Serialize(model);
        var read = SchemaV2.Read(current.Header, legacy.Annotations.Select(a => a.Envelope).ToArray(), _ => null);
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus); Assert.Equal(AnnotationReadStatus.Rejected, read.Annotations.Single().Status);
        Assert.Empty(read.Model!.AllAnnotations);
    }
}
