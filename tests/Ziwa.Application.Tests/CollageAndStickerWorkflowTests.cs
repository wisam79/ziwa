using System.Collections.Immutable;
using Xunit;
using Ziwa.Application.Collage;
using Ziwa.Application.Session;
using Ziwa.Application.Stickers;
using Ziwa.Application.Workspace;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Stickers;
using Ziwa.Domain.Validation;

namespace Ziwa.Application.Tests;

public sealed class CollageAndStickerWorkflowTests
{
    [Fact]
    public void CollageOperations_AutoRepeatSwapTransformAndBatchFilters_AreSingleUndoSteps()
    {
        ZiwaDocument doc = QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.IdPhotoSheet4x6);
        var session = new DocumentSession(doc);

        var photoA = new AssetId("photo-a");
        var photoB = new AssetId("photo-b");
        CollageLayoutModel initialLayout = session.CommittedDocument.ActivePage.CollageLayout!;
        SlotId slot0 = initialLayout.Slots[0].Id;
        SlotId slot1 = initialLayout.Slots[1].Id;

        Assert.True(session.ApplyCommand("تعيين الخانة 0", d => CollageOperations.AssignAssetToSlot(d, slot0, photoA)));
        Assert.True(session.ApplyCommand("تعيين الخانة 1", d => CollageOperations.AssignAssetToSlot(d, slot1, photoB)));

        Assert.True(session.ApplyCommand("تبديل الخانتين", d => CollageOperations.SwapSlots(d, slot0, slot1)));
        CollageLayoutModel afterSwap = session.CommittedDocument.ActivePage.CollageLayout!;
        Assert.Equal(photoB, afterSwap.Slots[0].AssignedAssetId);
        Assert.Equal(photoA, afterSwap.Slots[1].AssignedAssetId);

        Assert.True(session.Undo());
        Assert.Equal(photoA, session.CommittedDocument.ActivePage.CollageLayout!.Slots[0].AssignedAssetId);

        // AutoRepeat fills all 4 slots
        Assert.True(session.ApplyCommand("تكرار تلقائي", d => CollageOperations.SetAutoRepeatMode(d, true, photoA)));
        Assert.All(session.CommittedDocument.ActivePage.CollageLayout!.Slots, s => Assert.Equal(photoA, s.AssignedAssetId));

        // Transform slot content (rotate 90 + flip + crop)
        Assert.True(session.ApplyCommand("تدوير وقلب", d => CollageOperations.TransformSlotContent(
            d,
            slot0,
            crop: new CropSpec(1.5, 0.2, -0.1),
            quarterTurnDelta: 1,
            toggleFlipH: true)));
        CollageSlot transformed = session.CommittedDocument.ActivePage.CollageLayout!.Slots[0];
        Assert.Equal(1, transformed.QuarterTurns);
        Assert.True(transformed.FlipHorizontal);
        Assert.Equal(1.5, transformed.EffectiveCrop.Zoom);

        // Apply filter to all slots + reset single slot
        var bw = new ImageFilterSpec(Contrast: 0.2, Preset: PhotoFilterPreset.ClassicBw);
        Assert.True(session.ApplyCommand("فلتر للكل", d => CollageOperations.ApplyFiltersToAllSlots(d, bw)));
        Assert.All(session.CommittedDocument.ActivePage.CollageLayout!.Slots, s => Assert.Equal(PhotoFilterPreset.ClassicBw, s.EffectiveFilters.Preset));

        Assert.True(session.ApplyCommand("إعادة تعيين", d => CollageOperations.ResetSlotFilters(d, slot0)));
        Assert.Equal(PhotoFilterPreset.None, session.CommittedDocument.ActivePage.CollageLayout!.Slots[0].EffectiveFilters.Preset);
        Assert.Equal(PhotoFilterPreset.ClassicBw, session.CommittedDocument.ActivePage.CollageLayout!.Slots[1].EffectiveFilters.Preset);
    }

    [Fact]
    public void StickerOperations_DoubleClickNonDestructiveReEdit_PreservesGeometryWithSingleUndo()
    {
        ZiwaDocument doc = QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.StickerSheetA5);
        var session = new DocumentSession(doc);

        StickerNode original = Assert.IsType<StickerNode>(session.CommittedDocument.ActivePage.Nodes[0]);
        RectMm originalBounds = original.BoundsMm;

        ImmutableDictionary<string, string> editedFields = original.Fields.SetItem("company", "مختبر التصوير الحديث");
        StickerPalette nextPalette = OklchPaletteGenerator.GenerateSmartRandomPalette(99);

        Assert.True(session.ApplyCommand("تعديل الملصق", d =>
            StickerOperations.UpdateStickerInPlace(d, original.Id, original.TemplateId, editedFields, nextPalette)));

        StickerNode updated = Assert.IsType<StickerNode>(session.CommittedDocument.ActivePage.Nodes[0]);
        Assert.Equal(originalBounds, updated.BoundsMm);
        Assert.Equal("مختبر التصوير الحديث", updated.Fields["company"]);
        Assert.Equal(nextPalette, updated.Palette);

        Assert.True(session.Undo());
        StickerNode reverted = Assert.IsType<StickerNode>(session.CommittedDocument.ActivePage.Nodes[0]);
        Assert.Equal(original.Fields["company"], reverted.Fields["company"]);
    }

    [Fact]
    public void QuickStartAndBrandService_AllPresetsAndBrandActionsPassSchemaValidation()
    {
        StudioBrandKit kit = QuickStartAndBrandService.CreateDefaultBrandKit(new AssetId("svg-brand-logo"));

        foreach (QuickStartPresetKind kind in Enum.GetValues<QuickStartPresetKind>())
        {
            ZiwaDocument doc = QuickStartAndBrandService.CreateQuickStartDocument(kind, new AssetId("sample-img"));
            Assert.True(SchemaValidator.ValidateDocument(doc).IsValid);

            ZiwaDocument withLogo = QuickStartAndBrandService.InsertBrandLogo(doc, kit);
            ZiwaDocument withPhrase = QuickStartAndBrandService.InsertBrandPhrase(withLogo, kit, 1);
            Assert.True(SchemaValidator.ValidateDocument(withPhrase).IsValid);
        }
    }
}
