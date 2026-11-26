using Xunit;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Ai;
using Ziwa.Application.Collage;
using Ziwa.Application.Services;
using Ziwa.Application.Session;
using Ziwa.Application.Workspace;
using Ziwa.Domain.Ai;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Tests;

public sealed class Phase4OfflineAiCoordinatorTests
{
    private static readonly AssetId OriginalPortrait = new("orig-portrait-sha");
    private static readonly AssetId CutoutPortrait = new("cutout-portrait-sha");
    private static readonly AssetId EditedMaskCutout = new("edited-mask-sha");

    [Fact]
    public async Task FreeformImageNode_BackgroundRemovalMaskEditAndAutoFraming_PreserveOriginalAndUndoInSingleStep()
    {
        ZiwaDocument doc = SampleVerticalSliceFactory.CreateShowcaseDocument(OriginalPortrait);
        var session = new DocumentSession(doc);
        ImageNode imageNode = Assert.IsType<ImageNode>(session.CommittedDocument.ActivePage.Nodes.First(n => n is ImageNode));
        session.SetSelection([imageNode.Id]);

        var bgService = new StubBackgroundRemovalService();
        var faceService = new StubFaceFramingService(new FaceRectPx(260, 90, 120, 150, 0.92), 600, 450);
        using var coordinator = new OfflineAiCoordinator(session, bgService, faceService);

        AiOperationOutcome bgOutcome = await coordinator.RemoveBackgroundForSelectionAsync();
        Assert.True(bgOutcome.Succeeded);
        Assert.Equal(1, session.UndoCount);

        ImageNode afterBg = Assert.IsType<ImageNode>(session.CommittedDocument.ActivePage.Nodes.First(n => n.Id == imageNode.Id));
        Assert.Equal(CutoutPortrait, afterBg.CurrentAssetId);
        Assert.Equal(OriginalPortrait, afterBg.OriginalAssetId);

        // Prepare & commit manual mask edit -> single Undo step
        PreparedMaskEditorContext? prepared = await coordinator.PrepareMaskEditorForSelectionAsync();
        Assert.NotNull(prepared);
        byte[] modifiedAlpha = new byte[4 * 4];
        Array.Fill(modifiedAlpha, (byte)200);
        AiOperationOutcome maskOutcome = await coordinator.CommitEditedMaskAsync(
            prepared!.Ticket,
            prepared.Prepared.OriginalAssetId,
            4,
            4,
            modifiedAlpha);
        Assert.True(maskOutcome.Succeeded);
        Assert.Equal(2, session.UndoCount);

        ImageNode afterMask = Assert.IsType<ImageNode>(session.CommittedDocument.ActivePage.Nodes.First(n => n.Id == imageNode.Id));
        Assert.Equal(EditedMaskCutout, afterMask.CurrentAssetId);
        Assert.Equal(OriginalPortrait, afterMask.OriginalAssetId);

        // Auto face framing -> single Undo step
        AiOperationOutcome frameOutcome = await coordinator.AutoFrameFaceForSelectionAsync(IdPhotoFramingPreset.PassportIcao35x45);
        Assert.True(frameOutcome.Succeeded);
        Assert.Equal(3, session.UndoCount);

        ImageNode afterFrame = Assert.IsType<ImageNode>(session.CommittedDocument.ActivePage.Nodes.First(n => n.Id == imageNode.Id));
        Assert.True(afterFrame.Crop.Zoom > 1.0);
        Assert.Equal(OriginalPortrait, afterFrame.OriginalAssetId);

        // Undo reverts framing cleanly
        Assert.True(session.Undo());
        ImageNode revertedFrame = Assert.IsType<ImageNode>(session.CommittedDocument.ActivePage.Nodes.First(n => n.Id == imageNode.Id));
        Assert.Equal(CropSpec.Identity, revertedFrame.Crop);
        Assert.Equal(EditedMaskCutout, revertedFrame.CurrentAssetId);
    }

    [Fact]
    public async Task CollageSlot_AutoRepeatSyncAndRestoreOriginal_WorkCleanlyWithSingleUndo()
    {
        ZiwaDocument doc = QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.IdPhotoSheet4x6, OriginalPortrait);
        var session = new DocumentSession(doc);
        CollageLayoutModel layout = session.CommittedDocument.ActivePage.CollageLayout!;
        SlotId slot0 = layout.Slots[0].Id;
        session.SelectCollageSlot(slot0);

        var bgService = new StubBackgroundRemovalService();
        var faceService = new StubFaceFramingService(new FaceRectPx(180, 70, 110, 140, 0.90), 500, 400);
        using var coordinator = new OfflineAiCoordinator(session, bgService, faceService);

        AiOperationOutcome bgOutcome = await coordinator.RemoveBackgroundForSelectionAsync();
        Assert.True(bgOutcome.Succeeded);
        Assert.All(session.CommittedDocument.ActivePage.CollageLayout!.Slots, s =>
        {
            Assert.Equal(CutoutPortrait, s.AssignedAssetId);
            Assert.Equal(OriginalPortrait, s.OriginalAssetId);
        });

        AiOperationOutcome frameOutcome = await coordinator.AutoFrameFaceForSelectionAsync(IdPhotoFramingPreset.AutoByAspect);
        Assert.True(frameOutcome.Succeeded);
        Assert.All(session.CommittedDocument.ActivePage.CollageLayout!.Slots, s =>
            Assert.True(s.EffectiveCrop.Zoom > 1.0));

        Assert.True(session.ApplyCommand("استعادة الأصل", d => CollageOperations.RestoreOriginalSlotImage(d, slot0)));
        CollageSlot restored = session.CommittedDocument.ActivePage.CollageLayout!.Slots[0];
        Assert.Equal(OriginalPortrait, restored.AssignedAssetId);
        Assert.Equal(CropSpec.Identity, restored.EffectiveCrop);
    }

    [Fact]
    public async Task StaleOrCancelledOperationTicket_RejectsLateMutationSafely()
    {
        ZiwaDocument doc = SampleVerticalSliceFactory.CreateShowcaseDocument(OriginalPortrait);
        var session = new DocumentSession(doc);
        ImageNode imageNode = Assert.IsType<ImageNode>(session.CommittedDocument.ActivePage.Nodes.First(n => n is ImageNode));
        session.SetSelection([imageNode.Id]);

        var tcs = new TaskCompletionSource<MaskEstimationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedBg = new DeferredBackgroundRemovalService(tcs.Task);
        var faceService = new StubFaceFramingService(null, 400, 400);
        using var coordinator = new OfflineAiCoordinator(session, delayedBg, faceService);

        Task<AiOperationOutcome> running = coordinator.RemoveBackgroundForSelectionAsync();
        // Switch document while background inference is still running (even reusing same DocumentId bumps Generation)
        session.ReplaceDocument(doc);
        tcs.SetResult(new MaskEstimationResult(CutoutPortrait, 4, 4, new byte[16], false));

        AiOperationOutcome outcome = await running;
        Assert.False(outcome.Succeeded);
        ImageNode after = Assert.IsType<ImageNode>(session.CommittedDocument.ActivePage.Nodes.First(n => n.Id == imageNode.Id));
        Assert.Equal(OriginalPortrait, after.CurrentAssetId);
    }

    private sealed class StubBackgroundRemovalService : IBackgroundRemovalService
    {
        public Task<MaskEstimationResult> RemoveBackgroundAsync(AssetId originalAssetId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] alpha = new byte[16];
            Array.Fill(alpha, (byte)255);
            return Task.FromResult(new MaskEstimationResult(CutoutPortrait, 4, 4, alpha, false));
        }

        public Task<MaskEditorPreparedSession> PrepareMaskEditorAsync(AssetId originalAssetId, AssetId currentAssetId, CancellationToken cancellationToken = default)
        {
            byte[] alpha = new byte[16];
            Array.Fill(alpha, (byte)255);
            return Task.FromResult(new MaskEditorPreparedSession(originalAssetId, currentAssetId, 4, 4, alpha, (byte[])alpha.Clone(), false));
        }

        public Task<AssetId> CommitEditedMaskAsync(AssetId originalAssetId, int widthPx, int heightPx, ReadOnlyMemory<byte> editedAlphaMask, CancellationToken cancellationToken = default) =>
            Task.FromResult(EditedMaskCutout);
    }

    private sealed class DeferredBackgroundRemovalService(Task<MaskEstimationResult> task) : IBackgroundRemovalService
    {
        public Task<MaskEstimationResult> RemoveBackgroundAsync(AssetId originalAssetId, CancellationToken cancellationToken = default) => task;

        public Task<MaskEditorPreparedSession> PrepareMaskEditorAsync(AssetId originalAssetId, AssetId currentAssetId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssetId> CommitEditedMaskAsync(AssetId originalAssetId, int widthPx, int heightPx, ReadOnlyMemory<byte> editedAlphaMask, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubFaceFramingService(FaceRectPx? faceBox, int width, int height) : IFaceFramingService
    {
        public Task<FaceDetectionOutcome> DetectPrimaryFaceAsync(
            AssetId assetId,
            int quarterTurns = 0,
            bool flipHorizontal = false,
            bool flipVertical = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new FaceDetectionOutcome(faceBox is not null, faceBox, width, height, false));
    }
}
