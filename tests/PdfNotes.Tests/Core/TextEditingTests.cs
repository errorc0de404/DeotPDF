using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;

namespace PdfNotes.Tests.Core;

public sealed class TextEditingTests
{
    public static TextBoxAnnotation Box(string text = "한글 English 😀\n둘째 줄", double width = 180) => new(Guid.NewGuid(), false, Affine.Rigid(.2, 60, 70), text,
        [TextEditing.Default with { Length = text.Length }], TextAlignment.Left, width, TextBoxStyle.Default, []);
    [Theory] [InlineData(.5, 1)] [InlineData(400.1, 400)]
    public void FontSizeInputIsClampedWithoutChangingTheV1ScaledGeometryContract(double size, double expected) => Assert.Equal(expected, TextEditing.InputSize(size));

    [Fact] public void FormattingSnapsToGraphemesAndUndoRestoresAllRuns()
    {
        var original = Box("A😀e\u0301한글");
        var formatted = TextEditing.Format(original, 2, 2, r => r with { Bold = true, Underline = true, SizePt = 24 });
        Assert.Null(ModelValidator.ValidateAnnotation(formatted, _ => false));
        Assert.Equal(1, formatted.Runs[1].Start); Assert.Equal(4, formatted.Runs[1].Length);
        Assert.Equal(original.Text, formatted.Text);
        var s = new DocumentSession(DocumentModel.Create([new(400, 500, 0, PageSource.Generated, PageBackground.Blank)]));
        s.Execute(DocumentEdits.AddAnnotation(s.Current, s.Current.Pages[0].Id, original));
        s.Execute(DocumentEdits.UpdateAnnotations(s.Current, [formatted])); s.Undo(); Assert.Equal(original, s.Current.AllAnnotations.Single());
    }
    [Fact] public void LayoutUsesWidthAndMixedFormattingAndOnlyHeightIsDerived()
    {
        var box = Box(string.Join(" ", Enumerable.Repeat("한글 English", 10)), 180);
        var narrow = box with { WidthPt = 80 };
        Assert.True(TextLayout.Measure(box).Height > 25, $"Wide={TextLayout.Measure(box).Height}, narrow={TextLayout.Measure(narrow).Height}");
        Assert.True(TextLayout.Measure(narrow).Height > TextLayout.Measure(box).Height, $"Wide={TextLayout.Measure(box).Height}, narrow={TextLayout.Measure(narrow).Height}");
        var styled = TextEditing.Format(box, 0, 10, r => r with { SizePt = 32, Bold = true });
        Assert.True(TextLayout.Measure(styled).Height >= TextLayout.Measure(box).Height);
#if !DEOT_SKIA
        if (!TextEngineSetup.Skia) Assert.True(WpfText.Measure(box).Drawing.IsFrozen); // the WPF screen drawing is shared across threads
#endif
    }
    [Fact] public void LayoutIsReusedForAMovedCopyButNotForChangedContent()
    {
        var box = Box(); var layout = TextLayout.Measure(box);
        // A drag frame or re-read box is a new record with the same content: no second layout (it took 100-700 ms).
        Assert.Same(layout, TextLayout.Measure(box with { Id = Guid.NewGuid(), Locked = true, Transform = Affine.Translation(5, 9).Then(box.Transform) }));
        Assert.Same(layout, TextLayout.Measure(box with { Runs = [.. box.Runs] }));
        Assert.NotSame(layout, TextLayout.Measure(box with { Text = box.Text.Replace('둘', '셋') }));
        Assert.NotSame(layout, TextLayout.Measure(box with { WidthPt = box.WidthPt + 1 }));
        Assert.NotSame(layout, TextLayout.Measure(box with { LayoutScale = 2 }));
        Assert.NotSame(layout, TextLayout.Measure(box with { Runs = [box.Runs[0] with { Bold = true }] }));
        Assert.NotSame(layout, TextLayout.Measure(box with { BoxStyle = box.BoxStyle with { BorderEnabled = true } }));
        Assert.NotSame(layout, TextLayout.Measure(box with { Highlights = [new(0, 2, HighlightHeightMode.Auto, null, RgbColor.Black, .3)] }));
        Assert.NotSame(layout, TextLayout.Measure(box with { Alignment = TextAlignment.Center }));
    }
    [Fact] public async Task BackgroundLayoutIsSharedWithWaitingCallersAndMatchesTheSynchronousOne()
    {
        var box = Box("배경에서 " + Guid.NewGuid());
        Assert.False(TextLayout.TryMeasure(box, out _));
        var queued = TextLayout.MeasureAsync(box, urgent: false);
        var again = TextLayout.MeasureAsync(box with { Id = Guid.NewGuid() }); // same content: one build, moved forward
        Assert.True(ReferenceEquals(queued, again) || queued.IsCompleted);
        var waited = TextLayout.Measure(box with { Transform = Affine.Translation(3, 4) }); // a caller that needs it now waits for it
        Assert.Same(waited, await queued);
        Assert.True(TextLayout.TryMeasure(box, out var known)); Assert.Same(waited, known);
        var direct = TextLayout.Engine.Build(box);
        Assert.Equal(direct.Height, waited.Height);
        Assert.Equal(direct.Characters.ToArray(), waited.Characters.ToArray());
    }
    [Fact] public void MixedGroupScaleBakesFontSizeOnceAndRotationHitsLocalBox()
    {
        var box = Box(); var changed = (TextBoxAnnotation)AnnotationGeometry.Similarity(box, new(20, 30), 2, .3);
        Assert.Equal(24, changed.Runs[0].SizePt); Assert.Equal(360, changed.WidthPt); Assert.True(changed.Transform.IsRigid);
        Assert.True(AnnotationGeometry.Hit(changed, changed.Transform.Transform(new(10, 10)), 0, TextLayout.Bounds));
        Assert.False(AnnotationGeometry.Hit(changed, changed.Transform.Transform(new(-10, -10)), 0, TextLayout.Bounds));
    }
    [Fact] public void ClipboardGetsIndependentTextIdsAndRetainsStyles()
    {
        var box = Box(); var s = new DocumentSession(DocumentModel.Create([new(400, 500, 0, PageSource.Generated, PageBackground.Blank)]));
        var page = s.Current.Pages[0].Id; s.Execute(DocumentEdits.AddAnnotation(s.Current, page, box));
        var paste = AnnotationClipboard.Capture(s.Current, page, [box.Id]).Paste(s.Current, page, null, TextLayout.Bounds);
        s.Execute(paste.Command); var copy = (TextBoxAnnotation)s.Current.FindAnnotation(paste.Ids[0])!.Annotation;
        Assert.NotEqual(box.Id, copy.Id); Assert.Equal(box.Runs.ToArray(), copy.Runs.ToArray()); Assert.Equal(box.Text, copy.Text);
        Assert.Equal(box.Transform.OffsetX + 10, copy.Transform.OffsetX); s.Undo(); Assert.Single(s.Current.AllAnnotations);
    }
    [Fact] public void BlankCleanupIncludesLockedBoxesAndUndoRestoresTheirPositionAndStyle()
    {
        var blank = Box(" \t\n") with { Locked = true, BoxStyle = new(RgbColor.Black, 1, true, RgbColor.Black, 1) };
        var s = new DocumentSession(DocumentModel.Create([new(400, 500, 0, PageSource.Generated, PageBackground.Blank)]));
        s.Execute(DocumentEdits.AddAnnotation(s.Current, s.Current.Pages[0].Id, blank));
        s.Execute(DocumentEdits.RemoveBlankTextBoxes(s.Current)); Assert.Empty(s.Current.AllAnnotations);
        s.Undo(); Assert.Equal(blank, s.Current.AllAnnotations.Single());
    }
}
