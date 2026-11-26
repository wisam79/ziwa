using System.Collections.Immutable;
using Ziwa.Domain.Math;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Validation;

namespace Ziwa.Domain.Ai;

public enum IdPhotoFramingPreset
{
    AutoByAspect = 0,
    PassportIcao35x45 = 1,
    OfficialId40x60 = 2,
    VisaSquare50x50 = 3,
}

public enum MaskBrushMode
{
    Erase = 0,
    Restore = 1,
}

public enum MaskPreviewMode
{
    AfterCheckerboard = 0,
    SplitBeforeAfter = 1,
    RubyOverlay = 2,
    BeforeOriginal = 3,
}

public readonly record struct FaceRectPx(
    double X,
    double Y,
    double Width,
    double Height,
    double Confidence = 1.0);

public sealed record IdPhotoFramingRule(
    IdPhotoFramingPreset Preset,
    string TitleAr,
    double TargetHeadHeightRatio,
    double CrownTopMarginRatio);

public static class IdPhotoFramingCatalog
{
    public static readonly IdPhotoFramingRule PassportIcao35x45 = new(
        IdPhotoFramingPreset.PassportIcao35x45,
        "جواز سفر دولي 35×45 مم (ICAO)",
        TargetHeadHeightRatio: 0.72,
        CrownTopMarginRatio: 0.10);

    public static readonly IdPhotoFramingRule OfficialId40x60 = new(
        IdPhotoFramingPreset.OfficialId40x60,
        "معاملات وهوية رسمية 4×6 سم",
        TargetHeadHeightRatio: 0.60,
        CrownTopMarginRatio: 0.10);

    public static readonly IdPhotoFramingRule VisaSquare50x50 = new(
        IdPhotoFramingPreset.VisaSquare50x50,
        "تأشيرة مربعة 5×5 سم",
        TargetHeadHeightRatio: 0.62,
        CrownTopMarginRatio: 0.12);

    public static ImmutableArray<IdPhotoFramingPreset> All { get; } =
    [
        IdPhotoFramingPreset.AutoByAspect,
        IdPhotoFramingPreset.PassportIcao35x45,
        IdPhotoFramingPreset.OfficialId40x60,
        IdPhotoFramingPreset.VisaSquare50x50,
    ];

    public static string GetDisplayTitleAr(IdPhotoFramingPreset preset) => preset switch
    {
        IdPhotoFramingPreset.AutoByAspect => "تلقائي حسب مقاس الهوية",
        IdPhotoFramingPreset.PassportIcao35x45 => PassportIcao35x45.TitleAr,
        IdPhotoFramingPreset.OfficialId40x60 => OfficialId40x60.TitleAr,
        IdPhotoFramingPreset.VisaSquare50x50 => VisaSquare50x50.TitleAr,
        _ => "تلقائي حسب مقاس الهوية",
    };

    public static IdPhotoFramingRule ResolveEffectiveRule(
        IdPhotoFramingPreset preset,
        double frameWidthMm,
        double frameHeightMm)
    {
        SchemaValidator.AssertFinitePositive(frameWidthMm, nameof(frameWidthMm));
        SchemaValidator.AssertFinitePositive(frameHeightMm, nameof(frameHeightMm));

        if (preset != IdPhotoFramingPreset.AutoByAspect)
        {
            return preset switch
            {
                IdPhotoFramingPreset.PassportIcao35x45 => PassportIcao35x45,
                IdPhotoFramingPreset.OfficialId40x60 => OfficialId40x60,
                IdPhotoFramingPreset.VisaSquare50x50 => VisaSquare50x50,
                _ => PassportIcao35x45,
            };
        }

        double aspect = frameWidthMm / frameHeightMm;
        if (aspect < 0.72)
        {
            return OfficialId40x60;
        }

        if (aspect > 0.90)
        {
            return VisaSquare50x50;
        }

        return PassportIcao35x45;
    }

    public static CropSpec ComputeFramingCrop(
        int imageWidthPx,
        int imageHeightPx,
        double frameWidthMm,
        double frameHeightMm,
        FaceRectPx face,
        IdPhotoFramingPreset preset = IdPhotoFramingPreset.AutoByAspect)
    {
        if (imageWidthPx <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidthPx), imageWidthPx, "Image width must be positive.");
        }

        if (imageHeightPx <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageHeightPx), imageHeightPx, "Image height must be positive.");
        }

        SchemaValidator.AssertFinitePositive(frameWidthMm, nameof(frameWidthMm));
        SchemaValidator.AssertFinitePositive(frameHeightMm, nameof(frameHeightMm));
        SchemaValidator.AssertFinitePositive(face.Width, nameof(face.Width));
        SchemaValidator.AssertFinitePositive(face.Height, nameof(face.Height));

        if (!double.IsFinite(face.X) || !double.IsFinite(face.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(face), "Face coordinates must be finite.");
        }

        // Convert eyebrow-to-chin detector box into crown-to-chin full head span.
        double headTopPx = System.Math.Max(0.0, face.Y - (0.22 * face.Height));
        double chinBottomPx = System.Math.Min(imageHeightPx, face.Y + (1.08 * face.Height));
        double headHeightPx = System.Math.Max(1.0, chinBottomPx - headTopPx);
        double headCenterXPx = DomainMath.Clamp(face.X + (face.Width * 0.5), 0.0, imageWidthPx);
        double headCenterYPx = (headTopPx + chinBottomPx) * 0.5;

        double imageAspect = (double)imageWidthPx / imageHeightPx;
        double slotAspect = frameWidthMm / frameHeightMm;

        double baseCropW;
        double baseCropH;
        if (imageAspect > slotAspect)
        {
            baseCropH = imageHeightPx;
            baseCropW = imageHeightPx * slotAspect;
        }
        else
        {
            baseCropW = imageWidthPx;
            baseCropH = imageWidthPx / slotAspect;
        }

        IdPhotoFramingRule rule = ResolveEffectiveRule(preset, frameWidthMm, frameHeightMm);
        double desiredCropH = System.Math.Max(
            headHeightPx / rule.TargetHeadHeightRatio,
            (face.Width / 0.58) / slotAspect);

        double zoom = DomainMath.Clamp(baseCropH / desiredCropH, 1.0, 20.0);
        double actualCropW = baseCropW / zoom;
        double actualCropH = baseCropH / zoom;

        double targetHeadCenterYRatio = rule.CrownTopMarginRatio + (rule.TargetHeadHeightRatio * 0.5);
        double desiredCenterX = headCenterXPx;
        double desiredCenterY = headCenterYPx + ((0.5 - targetHeadCenterYRatio) * actualCropH);

        double maxPanX = System.Math.Max(0.0, (imageWidthPx - actualCropW) * 0.5);
        double maxPanY = System.Math.Max(0.0, (imageHeightPx - actualCropH) * 0.5);

        double panX = maxPanX > 1e-6
            ? DomainMath.Clamp((desiredCenterX - (imageWidthPx * 0.5)) / maxPanX, -1.0, 1.0)
            : 0.0;
        double panY = maxPanY > 1e-6
            ? DomainMath.Clamp((desiredCenterY - (imageHeightPx * 0.5)) / maxPanY, -1.0, 1.0)
            : 0.0;

        return new CropSpec(zoom, panX, panY);
    }
}
