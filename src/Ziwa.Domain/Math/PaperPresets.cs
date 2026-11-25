using System.Collections.Immutable;

namespace Ziwa.Domain.Math;

public sealed record PaperPreset(
    string Id,
    string TitleAr,
    double WidthMm,
    double HeightMm,
    double DefaultBleedMm = 0.0,
    double DefaultSafeMarginMm = 5.0);

public static class PaperPresets
{
    public static readonly PaperPreset A4 = new("a4", "A4 (210 × 297 مم)", 210.0, 297.0);
    public static readonly PaperPreset A5 = new("a5", "A5 (148 × 210 مم)", 148.0, 210.0);
    public static readonly PaperPreset Photo4x6In = new("photo-4x6", "صورة 4×6 بوصة (101.6 × 152.4 مم)", 101.6, 152.4, 0.0, 3.0);
    public static readonly PaperPreset Photo5x7In = new("photo-5x7", "صورة 5×7 بوصة (127 × 177.8 مم)", 127.0, 177.8, 0.0, 4.0);
    public static readonly PaperPreset IdCardCr80 = new("id-cr80", "بطاقة هوية CR80 (85.6 × 53.98 مم)", 85.60, 53.98, 0.0, 3.0);
    public static readonly PaperPreset BusinessCard = new("business-card", "بطاقة عمل (90 × 55 مم)", 90.0, 55.0, 3.0, 4.0);

    public static ImmutableArray<PaperPreset> All { get; } =
    [
        A4,
        A5,
        Photo4x6In,
        Photo5x7In,
        IdCardCr80,
        BusinessCard,
    ];
}
