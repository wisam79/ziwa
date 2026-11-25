using Xunit;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Validation;

namespace Ziwa.Domain.Tests;

public sealed class SchemaValidatorAndImmutabilityTests
{
    private static ZiwaDocument CreateValidDocument()
    {
        var imageId = new AssetId("abc123");
        var page = new PageModel(
            Id: PageId.New(),
            Name: "صفحة 1",
            WidthMm: 148.0,
            HeightMm: 210.0,
            BleedMm: 3.0,
            SafeMarginMm: 5.0,
            Background: new SolidBackground(ColorRgba.White),
            Nodes:
            [
                new ShapeNode(
                    Id: NodeId.New(),
                    Name: "مستطيل",
                    BoundsMm: new RectMm(10, 10, 50, 30),
                    Kind: ShapeKind.Rectangle,
                    Fill: new SolidShapeFill(ColorRgba.Black)),
                new ImageNode(
                    Id: NodeId.New(),
                    Name: "صورة",
                    BoundsMm: new RectMm(20, 50, 40, 40),
                    CurrentAssetId: imageId,
                    OriginalAssetId: imageId,
                    Crop: CropSpec.Identity),
                new TextNode(
                    Id: NodeId.New(),
                    Name: "نص",
                    BoundsMm: new RectMm(10, 100, 80, 20),
                    Text: "زِيوا",
                    FontFamily: "Cairo",
                    FontSizePt: 14.0,
                    FontWeight: 700,
                    Color: ColorRgba.Black),
            ]);

        return new ZiwaDocument(
            Id: DocumentId.New(),
            SchemaVersion: 1,
            Title: "اختبار",
            Mode: EditorWorkflow.Freeform,
            Pages: [page],
            ActivePageIndex: 0,
            PrintSettings: new PrintTicketSpec(300.0),
            UpdatedAtUtc: DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ImmutableDocument_WithExpression_PreservesOriginalAndSharesUnchangedNodes()
    {
        ZiwaDocument original = CreateValidDocument();
        ZiwaDocument modified = original with { Title = "مستند معدّل" };

        Assert.Equal("اختبار", original.Title);
        Assert.Equal("مستند معدّل", modified.Title);
        Assert.Equal(original.ActivePage, modified.ActivePage);
        Assert.True(SchemaValidator.ValidateDocument(modified).IsValid);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-15.0)]
    public void ValidateDocument_RejectsInvalidPageDimensions(double badWidth)
    {
        ZiwaDocument doc = CreateValidDocument();
        PageModel badPage = doc.ActivePage with { WidthMm = badWidth };
        ZiwaDocument invalidDoc = doc with { Pages = [badPage] };

        ValidationResult result = SchemaValidator.ValidateDocument(invalidDoc);
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidateDocument_RejectsInvalidNodeParameters()
    {
        ZiwaDocument doc = CreateValidDocument();
        var badImage = (ImageNode)doc.ActivePage.Nodes[1] with { Crop = new CropSpec(Zoom: 0.5) };
        var badText = (TextNode)doc.ActivePage.Nodes[2] with { FontSizePt = double.NaN };
        PageModel badPage = doc.ActivePage with { Nodes = [doc.ActivePage.Nodes[0], badImage, badText] };
        ZiwaDocument invalidDoc = doc with { Pages = [badPage] };

        ValidationResult result = SchemaValidator.ValidateDocument(invalidDoc);
        Assert.False(result.IsValid);
        Assert.True(result.Errors.Length >= 2);
    }

    [Fact]
    public void ValidateDocument_SupportsAndValidatesGroupSvgAndCurvedText()
    {
        ZiwaDocument doc = CreateValidDocument();
        var svg = new SvgNode(NodeId.New(), "شعار SVG", new RectMm(5, 5, 20, 20), new AssetId("svg-1"));
        var curved = new TextNode(
            NodeId.New(),
            "ختم",
            new RectMm(10, 10, 60, 60),
            "زِيوا",
            "Cairo",
            14,
            700,
            ColorRgba.Black,
            Curve: new TextCurveSpec(TextCurveKind.FullCircle, 1.0),
            Effects: new TextEffectSpec(Outline: new StrokeSpec(ColorRgba.White, 0.3)));
        var group = new GroupNode(NodeId.New(), "مجموعة", new RectMm(5, 5, 60, 60), [svg, curved]);
        PageModel page = doc.ActivePage with { Nodes = [.. doc.ActivePage.Nodes, group] };

        Assert.True(SchemaValidator.ValidateDocument(doc with { Pages = [page] }).IsValid);
        Assert.True(SchemaValidator.ValidateNodes([group]).IsValid);

        var emptyGroup = group with { Children = [] };
        Assert.False(SchemaValidator.ValidateNodes([emptyGroup]).IsValid);

        var badSvg = svg with { SvgAssetId = AssetId.Empty };
        Assert.False(SchemaValidator.ValidateNodes([badSvg]).IsValid);
    }
}
