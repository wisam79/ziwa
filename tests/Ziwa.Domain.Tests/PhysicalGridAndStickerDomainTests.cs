using System.Collections.Immutable;
using System.Xml.Linq;
using Xunit;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Stickers;
using Ziwa.Domain.Validation;

namespace Ziwa.Domain.Tests;

public sealed class PhysicalGridAndStickerDomainTests
{
    [Fact]
    public void PhysicalGridBuilder_PropertyFuzz280Cases_NeverExitsPaperOrProducesBelow1Mm()
    {
        var rng = new Random(20261125);
        GridAlignment9[] alignments = Enum.GetValues<GridAlignment9>();

        for (int i = 0; i < 280; i++)
        {
            double pageW = i switch
            {
                0 => 40.0,
                1 => 10.0,
                2 => 2.0,
                _ => 20.0 + (rng.NextDouble() * 400.0),
            };
            double pageH = i switch
            {
                0 => 40.0,
                1 => 10.0,
                2 => 2.0,
                _ => 20.0 + (rng.NextDouble() * 400.0),
            };

            int rows = i == 0 ? 200 : rng.Next(1, 35);
            int cols = i == 0 ? 200 : rng.Next(1, 35);
            double cellW = i == 3 ? double.NaN : (rng.NextDouble() * 120.0) - 10.0;
            double cellH = i == 4 ? double.PositiveInfinity : (rng.NextDouble() * 120.0) - 10.0;
            double gapX = i == 5 ? -5.0 : rng.NextDouble() * 30.0;
            double gapY = i == 6 ? double.NaN : rng.NextDouble() * 30.0;
            double margin = i == 7 ? 500.0 : (rng.NextDouble() * 80.0) - 5.0;
            CollageSizingMode sizing = (i % 2 == 0) ? CollageSizingMode.FixedPhysicalCellMm : CollageSizingMode.FitGridToPaper;
            GridAlignment9 align = alignments[i % alignments.Length];

            ImmutableArray<MixedRowBandSpec> mixed = (i % 10 == 0)
                ? [new MixedRowBandSpec(rng.Next(1, 8), 40.0, 60.0, rng.Next(1, 3)), new MixedRowBandSpec(rng.Next(1, 10), 30.0, 40.0, rng.Next(1, 4))]
                : default;

            var spec = new CollageGridSpec($"fuzz-{i}", sizing, rows, cols, cellW, cellH, gapX, gapY, margin, align, mixed);
            ImmutableArray<RectMm> rects = PhysicalGridBuilder.BuildSlotRects(pageW, pageH, spec);

            Assert.NotEmpty(rects);
            foreach (RectMm r in rects)
            {
                Assert.True(double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height));
                Assert.True(r.Width >= PhysicalGridBuilder.MinCellSizeMm - 1e-6, $"Case {i}: Width {r.Width} < 1mm on page {pageW}x{pageH}");
                Assert.True(r.Height >= PhysicalGridBuilder.MinCellSizeMm - 1e-6, $"Case {i}: Height {r.Height} < 1mm on page {pageW}x{pageH}");
                Assert.True(r.X >= -1e-6 && r.Y >= -1e-6, $"Case {i}: Negative origin ({r.X}, {r.Y})");
                Assert.True(r.Right <= pageW + 1e-6, $"Case {i}: Right {r.Right} > PageW {pageW}");
                Assert.True(r.Bottom <= pageH + 1e-6, $"Case {i}: Bottom {r.Bottom} > PageH {pageH}");
            }
        }
    }

    [Fact]
    public void CollagePresets_BuildExactPhysicalSizesOnStandardSheets()
    {
        ImmutableArray<RectMm> official = PhysicalGridBuilder.BuildSlotRects(101.6, 152.4, CollagePresets.IdOfficial40x60);
        Assert.Equal(4, official.Length);
        Assert.All(official, r =>
        {
            Assert.Equal(40.0, r.Width, precision: 6);
            Assert.Equal(60.0, r.Height, precision: 6);
        });

        ImmutableArray<RectMm> mixed = PhysicalGridBuilder.BuildSlotRects(148.0, 210.0, CollagePresets.MixedIdentitySheet);
        Assert.Equal(11, mixed.Length); // 3 top (40x60) + 8 bottom (30x40)

        CollageLayoutModel reflowed = PhysicalGridBuilder.BuildOrReflowLayout(101.6, 152.4, CollagePresets.IdOfficial40x60);
        var page = new PageModel(PageId.New(), "شيت", 101.6, 152.4, 0, 3, new SolidBackground(ColorRgba.White), [], reflowed);
        var doc = new ZiwaDocument(DocumentId.New(), 1, "اختبار", EditorWorkflow.Collage, [page], 0, new PrintTicketSpec(), DateTimeOffset.UtcNow);
        Assert.True(SchemaValidator.ValidateDocument(doc).IsValid);
    }

    [Fact]
    public void OklchPaletteGenerator_AlwaysAchievesWcagContrast()
    {
        for (int seed = 1; seed <= 100; seed++)
        {
            StickerPalette palette = OklchPaletteGenerator.GenerateSmartRandomPalette(seed);
            double primaryContrast = OklchPaletteGenerator.ComputeContrastRatio(palette.TextOnPrimary, palette.Primary);
            double surfaceContrast = OklchPaletteGenerator.ComputeContrastRatio(palette.TextOnSurface, palette.SurfaceBackground);

            Assert.True(primaryContrast >= 4.5, $"Seed {seed} primary contrast {primaryContrast:0.00} < 4.5");
            Assert.True(surfaceContrast >= 4.5, $"Seed {seed} surface contrast {surfaceContrast:0.00} < 4.5");
        }
    }

    [Fact]
    public void ParametricStickerCatalog_GeneratesValidXmlAcrossAllTemplatesEvenWithSpecialCharacters()
    {
        foreach (StickerTemplateDefinition def in ParametricStickerCatalog.All)
        {
            ImmutableDictionary<string, string> trickyFields = def.Fields
                .ToImmutableDictionary(f => f.Key, f => $"{f.DefaultValue} <tag>&\"'");

            var node = new StickerNode(
                NodeId.New(),
                def.TitleAr,
                new RectMm(10, 10, def.DefaultWidthMm, def.DefaultHeightMm),
                def.Id,
                trickyFields,
                def.DefaultPalette);

            string svgXml = ParametricStickerCatalog.BuildSvgXml(node);
            XDocument parsed = XDocument.Parse(svgXml);
            Assert.Equal("svg", parsed.Root?.Name.LocalName);
            Assert.Contains("&lt;tag&gt;&amp;", svgXml, StringComparison.Ordinal);
            Assert.Contains(parsed.Descendants(), el => el.Value.Contains("<tag>&\"'", StringComparison.Ordinal));
        }
    }
}
