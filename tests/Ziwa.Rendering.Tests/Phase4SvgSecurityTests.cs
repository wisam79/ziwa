using Ziwa.Rendering.Svg;

namespace Ziwa.Rendering.Tests;

public sealed class Phase4SvgSecurityTests
{
    [Theory]
    [InlineData("fill:url(file:///external.png)")]
    [InlineData("fill:url( https://invalid.example/image.png )")]
    [InlineData("fill:url(//invalid.example/image.png)")]
    public void Sanitizer_RemovesExternalCssReferencesBeforeProjectAssetsAreParsed(string style)
    {
        string svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"10\" height=\"10\" style=\"{style}\" /></svg>";
        string safe = SvgSanitizer.Sanitize(svg);
        Assert.DoesNotContain("style=", safe);
        Assert.DoesNotContain("url(", safe);
    }

    [Fact]
    public void Sanitizer_RemovesStylesheetImportsAndProcessingInstructionsButKeepsInternalPaint()
    {
        string svg = "<?xml-stylesheet href=\"file:///external.css\"?><svg xmlns=\"http://www.w3.org/2000/svg\"><style>@import 'https://invalid.example/style.css';</style><rect fill=\"url(#local)\" width=\"10\" height=\"10\" /></svg>";
        string safe = SvgSanitizer.Sanitize(svg);
        Assert.DoesNotContain("xml-stylesheet", safe);
        Assert.DoesNotContain("@import", safe);
        Assert.Contains("url(#local)", safe);
    }
}
