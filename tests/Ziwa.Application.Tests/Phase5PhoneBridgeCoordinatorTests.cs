using Xunit;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Bridge;
using Ziwa.Application.Session;
using Ziwa.Application.Workspace;
using Ziwa.Domain.Bridge;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Tests;

public sealed class Phase5PhoneBridgeCoordinatorTests
{
    [Fact]
    public void InsertReceivedPhoto_FreeformMode_AddsCenteredImageNodeWithUndo()
    {
        var session = new DocumentSession(QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.BlankFreeformA4));
        var assetId = new AssetId("phone-asset-1");
        var photo = new PhoneBridgeReceivedPhoto(assetId, 1600, 1200, PhoneBridgePhotoSource.LocalLan, DateTimeOffset.UtcNow);

        PhoneBridgeInsertOutcome outcome = PhoneBridgeCanvasInserter.InsertReceivedPhoto(session, photo);

        Assert.False(outcome.InsertedIntoCollageSlot);
        Assert.NotNull(outcome.InsertedNodeId);
        ImageNode inserted = Assert.Single(session.CommittedDocument.ActivePage.Nodes.OfType<ImageNode>());
        Assert.Equal(assetId, inserted.CurrentAssetId);
        Assert.Equal(assetId, inserted.OriginalAssetId);
        Assert.Equal(inserted.Id, Assert.Single(session.SelectedNodeIds));

        Assert.True(session.Undo());
        Assert.Empty(session.CommittedDocument.ActivePage.Nodes.OfType<ImageNode>());
    }

    [Fact]
    public void InsertReceivedPhoto_CollageMode_FillsSelectedEmptySlotAndAdvancesSelection()
    {
        var session = new DocumentSession(QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.IdPhotoSheet4x6));

        CollageSlot[] slots = [.. session.CommittedDocument.ActivePage.CollageLayout!.Slots];
        session.SelectCollageSlot(slots[0].Id);

        var asset1 = new AssetId("phone-slot-1");
        PhoneBridgeInsertOutcome first = PhoneBridgeCanvasInserter.InsertReceivedPhoto(
            session,
            new PhoneBridgeReceivedPhoto(asset1, 1000, 1000, PhoneBridgePhotoSource.CloudRelay, DateTimeOffset.UtcNow));

        Assert.True(first.InsertedIntoCollageSlot);
        Assert.Equal(slots[0].Id, first.TargetSlotId);
        Assert.Equal(slots[1].Id, session.SelectedCollageSlotId);

        var asset2 = new AssetId("phone-slot-2");
        PhoneBridgeInsertOutcome second = PhoneBridgeCanvasInserter.InsertReceivedPhoto(
            session,
            new PhoneBridgeReceivedPhoto(asset2, 1000, 1000, PhoneBridgePhotoSource.LocalLan, DateTimeOffset.UtcNow));

        Assert.Equal(slots[1].Id, second.TargetSlotId);
        Assert.Equal(slots[2].Id, session.SelectedCollageSlotId);

        Assert.True(session.Undo());
        Assert.Null(session.CommittedDocument.ActivePage.CollageLayout!.Slots[1].AssignedAssetId);
    }

    [Fact]
    public async Task PhoneBridgeCoordinator_ForwardsLifecycleAndEvents()
    {
        var fake = new FakePhoneBridgeService();
        using var coordinator = new PhoneBridgeCoordinator(fake);

        string? pairedDevice = null;
        PhoneBridgeReceivedPhoto? receivedPhoto = null;
        string? cloudErr = null;
        coordinator.DevicePaired += d => pairedDevice = d;
        coordinator.PhotoReceived += p => receivedPhoto = p;
        coordinator.CloudErrorOccurred += e => cloudErr = e;

        PhoneBridgeStartInfo? startInfo = await coordinator.StartAsync();
        Assert.NotNull(startInfo);
        Assert.True(startInfo!.IsRunning);
        Assert.True(coordinator.Status.IsRunning);

        fake.RaisePaired("iPhone Pro");
        fake.RaisePhoto(new PhoneBridgeReceivedPhoto(new AssetId("a-1"), 800, 600, PhoneBridgePhotoSource.LocalLan, DateTimeOffset.UtcNow));
        fake.RaiseCloudError("تنبيه سحابي");

        Assert.Equal("iPhone Pro", pairedDevice);
        Assert.Equal(new AssetId("a-1"), receivedPhoto?.AssetId);
        Assert.Equal("تنبيه سحابي", cloudErr);

        await coordinator.RestartAsync();
        Assert.Equal(1, fake.StopCalls);
        Assert.Equal(2, fake.StartCalls);

        await coordinator.StopAsync();
        Assert.False(coordinator.Status.IsRunning);
    }

    private sealed class FakePhoneBridgeService : IPhoneBridgeService
    {
        public event Action<PhoneBridgeStatus>? StatusChanged;
        public event Action<string>? DevicePaired;
        public event Action<PhoneBridgeReceivedPhoto>? PhotoReceived;
        public event Action<string>? CloudErrorOccurred;

        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public PhoneBridgeStatus CurrentStatus { get; private set; } = PhoneBridgeStatus.Stopped;

        public Task<PhoneBridgeStartInfo> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            var info = new PhoneBridgeStartInfo(
                "127.0.0.1",
                8741,
                "tok-1",
                "http://127.0.0.1:8741/cam?token=tok-1",
                "https://grido.cloud-ip.cc/cam#session=uid/tok-1",
                IsRunning: true);
            CurrentStatus = new PhoneBridgeStatus(true, info.LocalUrl, info.CloudUrl, string.Empty, 0, string.Empty, string.Empty);
            StatusChanged?.Invoke(CurrentStatus);
            return Task.FromResult(info);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            CurrentStatus = PhoneBridgeStatus.Stopped;
            StatusChanged?.Invoke(CurrentStatus);
            return Task.CompletedTask;
        }

        public void RaisePaired(string device) => DevicePaired?.Invoke(device);
        public void RaisePhoto(PhoneBridgeReceivedPhoto photo) => PhotoReceived?.Invoke(photo);
        public void RaiseCloudError(string message) => CloudErrorOccurred?.Invoke(message);
    }
}
