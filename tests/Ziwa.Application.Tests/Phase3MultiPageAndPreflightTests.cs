using System.Collections.Immutable;
using Xunit;
using Ziwa.Application.Pages;
using Ziwa.Application.PrePress;
using Ziwa.Application.Printing;
using Ziwa.Application.Workspace;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.PrePress;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Tests;

public sealed class Phase3MultiPageAndPreflightTests
{
    [Fact]
    public void MultiPageOperations_AddDuplicateFrontBackMoveDeleteAndSyncWorkCorrectly()
    {
        ZiwaDocument doc = QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.BusinessCardFront);
        Assert.Single(doc.Pages);

        // Protected delete on single-page document
        ZiwaDocument protectedDelete = MultiPageOperations.DeletePage(doc, 0);
        Assert.Single(protectedDelete.Pages);

        // EnsureFrontAndBackPages creates Front + Back and selects Back
        ZiwaDocument frontBack = MultiPageOperations.EnsureFrontAndBackPages(doc);
        Assert.Equal(2, frontBack.Pages.Length);
        Assert.Equal(1, frontBack.ActivePageIndex);
        Assert.Equal("الوجه الأمامي", frontBack.Pages[0].Name);
        Assert.Equal("الوجه الخلفي", frontBack.Pages[1].Name);
        Assert.Equal(frontBack.Pages[0].WidthMm, frontBack.Pages[1].WidthMm);
        Assert.Equal(frontBack.Pages[0].BleedMm, frontBack.Pages[1].BleedMm);

        // ToggleFrontBackPage flips 1 <-> 0
        ZiwaDocument toggled = MultiPageOperations.ToggleFrontBackPage(frontBack);
        Assert.Equal(0, toggled.ActivePageIndex);
        Assert.Equal(1, MultiPageOperations.ToggleFrontBackPage(toggled).ActivePageIndex);

        // AddBlankPage inherits paper dimensions & bleed
        ZiwaDocument added = MultiPageOperations.AddBlankPage(frontBack, "صفحة إضافية");
        Assert.Equal(3, added.Pages.Length);
        Assert.Equal(2, added.ActivePageIndex);
        Assert.Equal(frontBack.Pages[0].WidthMm, added.ActivePage.WidthMm);
        Assert.Equal(frontBack.Pages[0].BleedMm, added.ActivePage.BleedMm);

        // DuplicatePage deep-clones node IDs
        ZiwaDocument duplicated = MultiPageOperations.DuplicatePage(added, 0);
        Assert.Equal(4, duplicated.Pages.Length);
        Assert.Equal(1, duplicated.ActivePageIndex);
        Assert.NotEqual(duplicated.Pages[0].Id, duplicated.Pages[1].Id);
        if (!duplicated.Pages[0].Nodes.IsDefaultOrEmpty && !duplicated.Pages[1].Nodes.IsDefaultOrEmpty)
        {
            Assert.NotEqual(duplicated.Pages[0].Nodes[0].Id, duplicated.Pages[1].Nodes[0].Id);
        }

        // MovePage keeps active PageId selected
        PageId activeId = duplicated.ActivePage.Id;
        ZiwaDocument moved = MultiPageOperations.MovePage(duplicated, 1, 3);
        Assert.Equal(3, moved.ActivePageIndex);
        Assert.Equal(activeId, moved.ActivePage.Id);

        // DeletePage removes target when > 1 pages
        ZiwaDocument deleted = MultiPageOperations.DeletePage(moved, 3);
        Assert.Equal(3, deleted.Pages.Length);

        // ResolvePageIndices
        Assert.Equal([0, 1, 2], MultiPageOperations.ResolvePageIndices(deleted, ExportPageRangeKind.AllPages).ToArray());
        Assert.Equal([0, 2], MultiPageOperations.ResolvePageIndices(deleted, ExportPageRangeKind.SpecificPages, [2, 0, 99, 0]).ToArray());
        Assert.Equal([deleted.ActivePageIndex], MultiPageOperations.ResolvePageIndices(deleted, ExportPageRangeKind.ActivePageOnly).ToArray());
    }

    [Fact]
    public void SyncPaperAndBleedAcrossAllPages_UpdatesDimensionsBleedAndReflowsCollage()
    {
        ZiwaDocument collageDoc = QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.IdPhotoSheet4x6);
        ZiwaDocument twoPages = MultiPageOperations.AddBlankPage(collageDoc, "صفحة ثانية");

        ZiwaDocument synced = MultiPageOperations.SyncPaperAndBleedAcrossAllPages(
            twoPages,
            widthMm: 148.0,
            heightMm: 210.0,
            bleedMm: 3.0,
            safeMarginMm: 5.0);

        foreach (PageModel page in synced.Pages)
        {
            Assert.Equal(148.0, page.WidthMm);
            Assert.Equal(210.0, page.HeightMm);
            Assert.Equal(3.0, page.BleedMm);
            Assert.Equal(5.0, page.SafeMarginMm);
        }

        CollageLayoutModel? reflowed = synced.Pages[0].CollageLayout;
        Assert.NotNull(reflowed);
        Assert.All(reflowed!.Slots, s =>
        {
            Assert.True(s.BoundsMm.Right <= 148.0 + 1e-4);
            Assert.True(s.BoundsMm.Bottom <= 210.0 + 1e-4);
        });
    }

    [Fact]
    public void PreflightChecker_CatchesLowEffectiveDpiWithCropZoomAndTextNearTrim_AndHasZeroFalsePositivesOnFullBleedBackgrounds()
    {
        var lowResAssetId = new AssetId("low-res-asset");
        var highResAssetId = new AssetId("high-res-asset");

        // 1) Low DPI via Crop.Zoom: 600px over 60mm (~254 DPI uncropped), with Zoom=2.5 -> ~101.6 DPI (< 150)
        var zoomedLowImage = new ImageNode(
            Id: NodeId.New(),
            Name: "صورة مقصوصة",
            BoundsMm: new RectMm(10.0, 10.0, 60.0, 60.0),
            CurrentAssetId: lowResAssetId,
            OriginalAssetId: lowResAssetId,
            Crop: new CropSpec(Zoom: 2.5, PanX: 0.0, PanY: 0.0));

        // 2) Text near trim line (1.0 mm from left edge)
        var nearTrimText = new TextNode(
            Id: NodeId.New(),
            Name: "نص قريب من القص",
            BoundsMm: new RectMm(1.0, 20.0, 40.0, 12.0),
            Text: "تنبيه قص",
            FontFamily: "Cairo",
            FontSizePt: 12.0,
            FontWeight: 600,
            Color: new ColorRgba(20, 24, 31, 255));

        var riskyPage = new PageModel(
            Id: PageId.New(),
            Name: "صفحة بها تحذيرات",
            WidthMm: 90.0,
            HeightMm: 55.0,
            BleedMm: 3.0,
            SafeMarginMm: 3.0,
            Background: new SolidBackground(ColorRgba.White),
            Nodes: [zoomedLowImage, nearTrimText]);

        static AssetPixelDimensions? Resolve(AssetId id) => id.Value switch
        {
            "low-res-asset" => new AssetPixelDimensions(600, 600),
            "high-res-asset" => new AssetPixelDimensions(4000, 3000),
            _ => null,
        };

        ImmutableArray<PreflightIssue> issues = PreflightChecker.CheckPage(riskyPage, 0, 300.0, Resolve);
        Assert.Equal(2, issues.Length);

        PreflightIssue dpiIssue = Assert.Single(issues, i => i.Kind == PreflightIssueKind.LowEffectiveImageDpi);
        Assert.Equal(zoomedLowImage.Id, dpiIssue.NodeId);
        Assert.True(dpiIssue.EffectiveDpi < 150.0);

        PreflightIssue trimIssue = Assert.Single(issues, i => i.Kind == PreflightIssueKind.TextNearTrimLine);
        Assert.Equal(nearTrimText.Id, trimIssue.NodeId);
        Assert.True(trimIssue.DistanceToTrimMm < 2.5);

        // 3) Full-bleed background ImageNode and ShapeNode spanning negative bleed must produce ZERO false positives
        var fullBleedImage = new ImageNode(
            Id: NodeId.New(),
            Name: "خلفية ممتدة للنزيف",
            BoundsMm: new RectMm(-3.0, -3.0, 96.0, 61.0),
            CurrentAssetId: highResAssetId,
            OriginalAssetId: highResAssetId,
            Crop: CropSpec.Identity);

        var fullBleedShape = new ShapeNode(
            Id: NodeId.New(),
            Name: "إطار خلفية ممتد",
            BoundsMm: new RectMm(-3.0, -3.0, 96.0, 61.0),
            Kind: ShapeKind.Rectangle,
            Fill: new SolidShapeFill(new ColorRgba(11, 92, 171, 255)));

        var cleanPage = riskyPage with { Nodes = [fullBleedShape, fullBleedImage] };
        ImmutableArray<PreflightIssue> cleanIssues = PreflightChecker.CheckPage(cleanPage, 0, 300.0, Resolve);
        Assert.Empty(cleanIssues);
    }

    [Fact]
    public void PrintCalibrationPlanner_ComputesExactMillimeterCenteringOffsetCompensationAndMarginClip()
    {
        var caps = new HardwarePrintCapabilities(
            PrinterName: "Test Printer",
            DpiX: 300,
            DpiY: 300,
            PhysicalWidthPx: 2480,
            PhysicalHeightPx: 3508,
            PhysicalOffsetXPx: 60,
            PhysicalOffsetYPx: 60,
            PrintableWidthPx: 2360,
            PrintableHeightPx: 3388);

        var cardPage = new PageModel(
            Id: PageId.New(),
            Name: "بطاقة عمل",
            WidthMm: 90.0,
            HeightMm: 50.0,
            BleedMm: 3.0,
            SafeMarginMm: 3.0,
            Background: new SolidBackground(ColorRgba.White),
            Nodes: []);

        var ticket = new DirectPrintJobTicket(
            PrinterName: "Test Printer",
            DocumentTitle: "Card",
            Borderless: false,
            IncludeBleed: true,
            IncludeCropMarks: true);

        PhysicalPrintCalibrationPlan plan = PrintCalibrationPlanner.ComputeCalibrationPlan(cardPage, caps, ticket);

        double expectedContentW = 90.0 + (2.0 * (3.0 + PrintCalibrationPlanner.CropMarksMarginMm));
        double expectedContentH = 50.0 + (2.0 * (3.0 + PrintCalibrationPlanner.CropMarksMarginMm));
        Assert.Equal(expectedContentW, plan.ContentWidthMm, 6);
        Assert.Equal(expectedContentH, plan.ContentHeightMm, 6);

        double expectedLeftMm = (caps.PhysicalWidthMm - expectedContentW) * 0.5;
        double expectedTopMm = (caps.PhysicalHeightMm - expectedContentH) * 0.5;
        Assert.Equal(expectedLeftMm, plan.ContentPhysicalLeftMm, 6);
        Assert.Equal(expectedTopMm, plan.ContentPhysicalTopMm, 6);

        int expectedDcLeft = (int)Math.Round(DomainMath.MmToPx(expectedLeftMm, 300), MidpointRounding.AwayFromZero) - 60;
        int expectedDcTop = (int)Math.Round(DomainMath.MmToPx(expectedTopMm, 300), MidpointRounding.AwayFromZero) - 60;
        Assert.Equal(expectedDcLeft, plan.DestDcLeftPx);
        Assert.Equal(expectedDcTop, plan.DestDcTopPx);
        Assert.False(plan.ClipsAtHardwareMargins);

        // Borderless removes offset compensation
        PhysicalPrintCalibrationPlan borderlessPlan = PrintCalibrationPlanner.ComputeCalibrationPlan(
            cardPage,
            caps,
            ticket with { Borderless = true });
        Assert.Equal(expectedDcLeft + 60, borderlessPlan.DestDcLeftPx);
        Assert.Equal(0.0, borderlessPlan.PrintableLeftMm);

        // Oversized design triggers hardware margin clip warning
        PageModel oversized = cardPage with { WidthMm = 208.0, HeightMm = 295.0 };
        PhysicalPrintCalibrationPlan clippedPlan = PrintCalibrationPlanner.ComputeCalibrationPlan(oversized, caps, ticket);
        Assert.True(clippedPlan.ClipsAtHardwareMargins);
    }
}
