using Xunit;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Services;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Validation;

namespace Ziwa.Application.Tests;

public sealed class WorkspaceAndExportUseCaseTests
{
    [Fact]
    public void SampleVerticalSliceFactory_ProducesSchemaValidDocument()
    {
        byte[] emblem = ProceduralEmblemPngBuilder.BuildEmblemPngBytes(64);
        Assert.True(emblem.Length > 100);

        var assetId = new AssetId("sample-emblem-sha256");
        var doc = SampleVerticalSliceFactory.CreateShowcaseDocument(assetId);
        var service = new WorkspaceDocumentService(doc);

        ValidationResult validation = SchemaValidator.ValidateDocument(service.CurrentDocument);
        Assert.True(validation.IsValid);
        Assert.Equal(148.0, service.CurrentDocument.ActivePage.WidthMm);
        Assert.Equal(210.0, service.CurrentDocument.ActivePage.HeightMm);
        Assert.Equal(300.0, service.CurrentDocument.PrintSettings.ExportDpi);
    }

    [Fact]
    public void WorkspaceDocumentService_RejectsInvalidDocument()
    {
        var doc = SampleVerticalSliceFactory.CreateShowcaseDocument(new AssetId("valid-id"));
        var service = new WorkspaceDocumentService(doc);
        var invalid = doc with { Pages = [] };

        Assert.Throws<InvalidOperationException>(() => service.SetDocument(invalid));
    }

    [Fact]
    public void ExportArtifactResult_RecordsExpectedMetadata()
    {
        var result = new ExportArtifactResult("out.png", 1748, 2480, 300.0, 42000);
        Assert.Equal(1748, result.PixelWidth);
        Assert.Equal(2480, result.PixelHeight);
        Assert.Equal(300.0, result.Dpi);
    }
}
