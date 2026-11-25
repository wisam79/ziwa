using Xunit;
using Ziwa.Domain.Math;

namespace Ziwa.Domain.Tests;

public sealed class DomainMathTests
{
    [Fact]
    public void MmPerInch_IsExactStandardConstant()
    {
        Assert.Equal(25.4, DomainMath.MmPerInch, precision: 10);
    }

    [Theory]
    [InlineData(210.0, 300.0, 2480)]
    [InlineData(297.0, 300.0, 3508)]
    [InlineData(85.60, 300.0, 1011)]
    [InlineData(53.98, 300.0, 638)]
    [InlineData(148.0, 300.0, 1748)]
    [InlineData(210.0, 600.0, 4961)]
    public void MmToPixelsRounded_MatchesPrintStandard(double mm, double dpi, int expectedPx)
    {
        Assert.Equal(expectedPx, DomainMath.MmToPixelsRounded(mm, dpi));
    }

    [Fact]
    public void MmAndPxAndPtConversions_RoundTripAccurately()
    {
        double px = DomainMath.MmToPx(101.6, 300.0);
        Assert.Equal(1200.0, px, precision: 6);
        Assert.Equal(101.6, DomainMath.PxToMm(px, 300.0), precision: 6);

        double pt = DomainMath.MmToPt(25.4);
        Assert.Equal(72.0, pt, precision: 6);
        Assert.Equal(25.4, DomainMath.PtToMm(pt), precision: 6);
    }

    [Theory]
    [InlineData(300.0, 11811u)]
    [InlineData(600.0, 23622u)]
    public void DpiToPixelsPerMeter_ReturnsExactPhysValue(double dpi, uint expectedPpm)
    {
        Assert.Equal(expectedPpm, DomainMath.DpiToPixelsPerMeter(dpi));
    }

    [Fact]
    public void Clamp_RejectsNonFiniteInputs()
    {
        Assert.Equal(5.0, DomainMath.Clamp(12.0, 1.0, 5.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DomainMath.Clamp(double.NaN, 0.0, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DomainMath.Clamp(double.PositiveInfinity, 0.0, 1.0));
    }

    [Fact]
    public void PaperPresets_ContainCoreStudioSizes()
    {
        Assert.Contains(PaperPresets.All, p => p.Id == "a4" && p.WidthMm == 210.0 && p.HeightMm == 297.0);
        Assert.Contains(PaperPresets.All, p => p.Id == "id-cr80" && p.WidthMm == 85.60);
    }
}
