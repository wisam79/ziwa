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

public sealed class Phase4CloudAiCoordinatorTests
{
    private static readonly AssetId SourceAsset = new("source-portrait-sha");
    private static readonly AssetId CutoutAsset = new("cutout-portrait-sha");
    private static readonly AssetId Upscaled2xAsset = new("upscaled-2x-portrait-sha");

    [Fact]
    public async Task FreeformImageNode_CloudEnhance2x_PreservesOriginalAndUndoesInSingleStep()
    {
        ZiwaDocument doc = SampleVerticalSliceFactory.CreateShowcaseDocument(SourceAsset);
        var session = new DocumentSession(doc);
        ImageNode target = Assert.IsType<ImageNode>(session.CommittedDocument.ActivePage.Nodes.First(n => n is ImageNode));
        // Simulate offline background cutout prior to cloud enhancement
        session.ApplyCommand("تطبيق عزل محلي", d => Ziwa.Application.Interaction.FreeformSceneOperations.ApplyProcessedImageAsset(d, target.Id, CutoutAsset));
        session.SetSelection([target.Id]);

        var auth = new StubAuthProvider(new CloudAuthSessionSnapshot("jwt-freeform", "user-1", "a@ziwa.app", "pro"));
        var utcNow = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var upscaler = new StubModalUpscaler(new CloudUpscaleResult(
            Upscaled2xAsset,
            500,
            700,
            1000,
            1400,
            2.1,
            "req-1",
            new CloudAiQuotaSnapshot(4, 15, utcNow, "user-1")));

        using var coordinator = new CloudAiCoordinator(session, upscaler, auth, clock: () => utcNow);
        AiOperationOutcome outcome = await coordinator.EnhanceSelectedPhotoAsync();

        Assert.True(outcome.Succeeded);
        Assert.Equal(CutoutAsset, upscaler.LastEnhancedAssetId);
        Assert.Equal(2, session.UndoCount);

        ImageNode updated = Assert.IsType<ImageNode>(session.CommittedDocument.ActivePage.Nodes.First(n => n.Id == target.Id));
        Assert.Equal(Upscaled2xAsset, updated.CurrentAssetId);
        Assert.Equal(SourceAsset, updated.OriginalAssetId);

        Assert.Equal(4, coordinator.CurrentQuota.Used);
        Assert.Equal(11, coordinator.CurrentQuota.Remaining);
        Assert.True(coordinator.CurrentQuota.IsServerVerified);

        Assert.True(session.Undo());
        ImageNode undone = Assert.IsType<ImageNode>(session.CommittedDocument.ActivePage.Nodes.First(n => n.Id == target.Id));
        Assert.Equal(CutoutAsset, undone.CurrentAssetId);
    }

    [Fact]
    public async Task CollageSlot_CloudEnhance2x_RecalibratesCropSyncsAutoRepeatAndClearsQuotaOnLogout()
    {
        ZiwaDocument doc = QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.IdPhotoSheet4x6, SourceAsset);
        var session = new DocumentSession(doc);
        CollageLayoutModel layout = session.CommittedDocument.ActivePage.CollageLayout!;
        SlotId slot0 = layout.Slots[0].Id;

        session.ApplyCommand("ضبط تأطير أولي", d => CollageOperations.TransformSlotContent(
            d,
            slot0,
            crop: new CropSpec(1.65, -0.25, 0.30)));
        session.SelectCollageSlot(slot0);

        var utcNow = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var auth = new StubAuthProvider(new CloudAuthSessionSnapshot("jwt-collage", "user-2", "b@ziwa.app", "pro"));
        var upscaler = new StubModalUpscaler(new CloudUpscaleResult(
            Upscaled2xAsset,
            600,
            800,
            1200,
            1600,
            1.8,
            "req-collage",
            new CloudAiQuotaSnapshot(5, 15, utcNow, "user-2")));

        using var coordinator = new CloudAiCoordinator(session, upscaler, auth, clock: () => utcNow);
        AiOperationOutcome outcome = await coordinator.EnhanceSelectedPhotoAsync();

        Assert.True(outcome.Succeeded);
        Assert.All(session.CommittedDocument.ActivePage.CollageLayout!.Slots, s =>
        {
            Assert.Equal(Upscaled2xAsset, s.AssignedAssetId);
            Assert.Equal(SourceAsset, s.OriginalAssetId);
        });
        CollageSlot updatedSlot0 = session.CommittedDocument.ActivePage.CollageLayout!.Slots[0];
        Assert.Equal(1.65, updatedSlot0.EffectiveCrop.Zoom, precision: 5);
        Assert.Equal(-0.25, updatedSlot0.EffectiveCrop.PanX, precision: 5);
        Assert.Equal(0.30, updatedSlot0.EffectiveCrop.PanY, precision: 5);
        Assert.True(coordinator.CurrentQuota.IsServerVerified);
        Assert.Equal(10, coordinator.CurrentQuota.Remaining);

        // Logout clears verified server quota snapshot immediately
        auth.SetSession(null);
        Assert.False(coordinator.IsAuthenticated);
        Assert.False(coordinator.CurrentQuota.IsServerVerified);
        Assert.Equal(0, coordinator.CurrentQuota.Used);
        Assert.Equal(5, coordinator.CurrentQuota.Limit);
    }

    [Fact]
    public async Task DocumentSwitchDuringCloudInference_RejectsLateInjectionCleanly()
    {
        ZiwaDocument firstDoc = SampleVerticalSliceFactory.CreateShowcaseDocument(SourceAsset);
        ZiwaDocument secondDoc = QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.BlankFreeformA4);
        var session = new DocumentSession(firstDoc);
        ImageNode target = Assert.IsType<ImageNode>(session.CommittedDocument.ActivePage.Nodes.First(n => n is ImageNode));
        session.SetSelection([target.Id]);

        var tcs = new TaskCompletionSource<CloudUpscaleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = new StubAuthProvider(new CloudAuthSessionSnapshot("jwt-switch", "user-3", Plan: "pro"));
        var upscaler = new DeferredModalUpscaler(tcs.Task);
        using var coordinator = new CloudAiCoordinator(session, upscaler, auth);

        Task<AiOperationOutcome> pending = coordinator.EnhanceSelectedPhotoAsync();
        await upscaler.Started.Task;

        // Switch project while cloud request is still in-flight
        session.ReplaceDocument(secondDoc);
        tcs.SetResult(new CloudUpscaleResult(Upscaled2xAsset, 400, 400, 800, 800, 1.5, "req-late", null));

        AiOperationOutcome outcome = await pending;
        Assert.False(outcome.Succeeded);
        Assert.Equal(secondDoc.Id, session.CommittedDocument.Id);
        Assert.Equal(0, session.UndoCount);
        Assert.DoesNotContain(session.CommittedDocument.ActivePage.Nodes.OfType<ImageNode>(), n => n.CurrentAssetId == Upscaled2xAsset);
    }

    private sealed class StubAuthProvider : ICloudAuthSessionStore
    {
        private CloudAuthSessionSnapshot? _session;

        public StubAuthProvider(CloudAuthSessionSnapshot? session)
        {
            _session = session;
        }

        public event Action? SessionChanged;

        public CloudAuthSessionSnapshot? GetCurrentSession() => _session;

        public void SetSession(CloudAuthSessionSnapshot? session)
        {
            _session = session;
            SessionChanged?.Invoke();
        }

        public void ClearSession() => SetSession(null);
    }

    private sealed class StubModalUpscaler : IModalAiUpscaler
    {
        private readonly CloudUpscaleResult _result;

        public StubModalUpscaler(CloudUpscaleResult result)
        {
            _result = result;
        }

        public AssetId LastEnhancedAssetId { get; private set; }

        public Task<CloudUpscaleResult> EnhanceImageAsync(
            AssetId sourceAssetId,
            CloudAuthSessionSnapshot session,
            double fidelity = 0.7,
            CancellationToken cancellationToken = default)
        {
            LastEnhancedAssetId = sourceAssetId;
            return Task.FromResult(_result);
        }
    }

    private sealed class DeferredModalUpscaler : IModalAiUpscaler
    {
        private readonly Task<CloudUpscaleResult> _task;

        public DeferredModalUpscaler(Task<CloudUpscaleResult> task)
        {
            _task = task;
        }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CloudUpscaleResult> EnhanceImageAsync(
            AssetId sourceAssetId,
            CloudAuthSessionSnapshot session,
            double fidelity = 0.7,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return _task;
        }
    }
}
