using Xunit;
using Ziwa.Domain.Ai;
using Ziwa.Domain.Primitives;

namespace Ziwa.Domain.Tests;

public sealed class IdPhotoFramingSpecsTests
{
    [Fact]
    public void ResolveEffectiveRule_AutoByAspect_SelectsExpectedOfficialStandards()
    {
        Assert.Equal(
            IdPhotoFramingPreset.OfficialId40x60,
            IdPhotoFramingCatalog.ResolveEffectiveRule(IdPhotoFramingPreset.AutoByAspect, 40.0, 60.0).Preset);

        Assert.Equal(
            IdPhotoFramingPreset.PassportIcao35x45,
            IdPhotoFramingCatalog.ResolveEffectiveRule(IdPhotoFramingPreset.AutoByAspect, 35.0, 45.0).Preset);

        Assert.Equal(
            IdPhotoFramingPreset.VisaSquare50x50,
            IdPhotoFramingCatalog.ResolveEffectiveRule(IdPhotoFramingPreset.AutoByAspect, 50.0, 50.0).Preset);
    }

    [Fact]
    public void ComputeFramingCrop_CentersOffCenterFace_WithinValidClampedCropSpec()
    {
        var offCenterFace = new FaceRectPx(X: 460.0, Y: 120.0, Width: 140.0, Height: 175.0, Confidence: 0.94);

        CropSpec crop = IdPhotoFramingCatalog.ComputeFramingCrop(
            imageWidthPx: 800,
            imageHeightPx: 600,
            frameWidthMm: 35.0,
            frameHeightMm: 45.0,
            face: offCenterFace,
            preset: IdPhotoFramingPreset.PassportIcao35x45);

        Assert.InRange(crop.Zoom, 1.0, 20.0);
        Assert.True(crop.Zoom > 1.2, $"Expected zoom > 1.2 for off-center portrait, got {crop.Zoom}.");
        Assert.InRange(crop.PanX, -1.0, 1.0);
        Assert.InRange(crop.PanY, -1.0, 1.0);
        Assert.True(crop.PanX > 0.1, $"Expected positive PanX to track right-shifted face, got {crop.PanX}.");
        Assert.True(crop.PanY < 0.0, $"Expected negative PanY to track upper face, got {crop.PanY}.");
    }

    [Fact]
    public void ComputeFramingCrop_RejectsNonPositiveOrNonFiniteInputs()
    {
        var validFace = new FaceRectPx(100, 100, 80, 100);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IdPhotoFramingCatalog.ComputeFramingCrop(0, 400, 35, 45, validFace));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IdPhotoFramingCatalog.ComputeFramingCrop(400, 400, double.NaN, 45, validFace));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IdPhotoFramingCatalog.ComputeFramingCrop(400, 400, 35, 45, new FaceRectPx(100, 100, 0, 100)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IdPhotoFramingCatalog.ComputeFramingCrop(400, 400, 35, 45, new FaceRectPx(double.PositiveInfinity, 100, 80, 100)));
    }
}
