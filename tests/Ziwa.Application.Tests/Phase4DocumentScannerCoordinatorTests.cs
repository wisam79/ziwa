using System.Collections.Immutable;
using Xunit;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Scanner;
using Ziwa.Application.Services;
using Ziwa.Application.Session;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Scanner;

namespace Ziwa.Application.Tests;

public sealed class Phase4DocumentScannerCoordinatorTests
{
    [Fact]
    public async Task InsertExtractedIntoFreeformAndCollage_AreSingleUndoSteps_AndPreserveUnfilteredOriginals()
    {
        ZiwaDocument doc = SampleVerticalSliceFactory.CreateShowcaseDocument(new AssetId("initial-asset"));
        var session = new DocumentSession(doc);
        int initialCount = session.CommittedDocument.ActivePage.Nodes.Length;

        var scanner = new StubScannerService();
        using var coordinator = new DocumentScannerCoordinator(session, scanner);

        ScanExtractionRequestItem[] requests =
        [
            new(Guid.NewGuid(), "بطاقة الهوية (أ)", DocumentQuadPx.CreateInsetDefault(400, 250), DocumentScanFilterKind.MagicColor),
            new(Guid.NewGuid(), "بطاقة الهوية (ب)", DocumentQuadPx.CreateInsetDefault(400, 250), DocumentScanFilterKind.MagicColor),
        ];

        ScannerInsertionOutcome freeformOutcome = await coordinator.ExtractAndInsertToFreeformAsync(new AssetId("raw-scan"), requests);
        Assert.True(freeformOutcome.Succeeded);
        Assert.Equal(2, freeformOutcome.InsertedCount);
        Assert.Equal(1, session.UndoCount);
        Assert.Equal(initialCount + 2, session.CommittedDocument.ActivePage.Nodes.Length);

        ImageNode insertedCard = Assert.IsType<ImageNode>(session.CommittedDocument.ActivePage.Nodes.Last());
        Assert.StartsWith("filtered-", insertedCard.CurrentAssetId.Value, StringComparison.Ordinal);
        Assert.StartsWith("unfiltered-", insertedCard.OriginalAssetId.Value, StringComparison.Ordinal);

        // Single Undo removes both inserted cards cleanly
        Assert.True(session.Undo());
        Assert.Equal(initialCount, session.CommittedDocument.ActivePage.Nodes.Length);

        // Insert into Collage creates/populates slots in one single Undo step
        ScannerInsertionOutcome collageOutcome = await coordinator.ExtractAndInsertToCollageAsync(new AssetId("raw-scan"), requests);
        Assert.True(collageOutcome.Succeeded);
        Assert.Equal(1, session.UndoCount);
        Assert.NotNull(session.CommittedDocument.ActivePage.CollageLayout);
        Assert.Equal(2, session.CommittedDocument.ActivePage.CollageLayout!.Slots.Count(s => s.AssignedAssetId is not null));

        Assert.True(session.Undo());
        Assert.Null(session.CommittedDocument.ActivePage.CollageLayout);
    }

    [Fact]
    public async Task StaleDocumentSwitchDuringExtraction_RejectsLateInsertion()
    {
        ZiwaDocument doc = SampleVerticalSliceFactory.CreateShowcaseDocument(new AssetId("initial-asset"));
        var session = new DocumentSession(doc);
        int initialCount = session.CommittedDocument.ActivePage.Nodes.Length;

        var tcs = new TaskCompletionSource<ImmutableArray<ExtractedScanDocument>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedScanner = new DeferredScannerService(tcs.Task);
        using var coordinator = new DocumentScannerCoordinator(session, delayedScanner);

        Task<ScannerInsertionOutcome> pending = coordinator.ExtractAndInsertToFreeformAsync(
            new AssetId("raw-scan"),
            [new(Guid.NewGuid(), "مستند", DocumentQuadPx.CreateInsetDefault(300, 400), DocumentScanFilterKind.MagicColor)]);

        // Switch document while extraction runs (same DocumentId, new Generation)
        session.ReplaceDocument(doc);
        tcs.SetResult([new ExtractedScanDocument(Guid.NewGuid(), "مستند", new AssetId("f1"), new AssetId("u1"), 300, 400, DocumentScanFilterKind.MagicColor)]);

        ScannerInsertionOutcome outcome = await pending;
        Assert.False(outcome.Succeeded);
        Assert.Equal(initialCount, session.CommittedDocument.ActivePage.Nodes.Length);
    }

    private sealed class StubScannerService : IDocumentScannerService
    {
        public Task<ScanDetectionResult> DetectDocumentsAsync(AssetId sourceAssetId, ScanDetectionMode mode = ScanDetectionMode.AutoSingleOrMulti, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScanDetectionResult(400, 250, [new DetectedDocumentRegion(Guid.NewGuid(), "مستند", DocumentQuadPx.CreateInsetDefault(400, 250))]));

        public Task<ImmutableArray<ExtractedScanDocument>> ExtractDocumentsAsync(AssetId sourceAssetId, IReadOnlyList<ScanExtractionRequestItem> items, CancellationToken cancellationToken = default)
        {
            ImmutableArray<ExtractedScanDocument> results = items.Select((it, idx) => new ExtractedScanDocument(
                it.RegionId,
                it.LabelAr,
                new AssetId($"filtered-{idx}"),
                new AssetId($"unfiltered-{idx}"),
                340,
                214,
                it.Filter)).ToImmutableArray();
            return Task.FromResult(results);
        }
    }

    private sealed class DeferredScannerService(Task<ImmutableArray<ExtractedScanDocument>> task) : IDocumentScannerService
    {
        public Task<ScanDetectionResult> DetectDocumentsAsync(AssetId sourceAssetId, ScanDetectionMode mode = ScanDetectionMode.AutoSingleOrMulti, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ImmutableArray<ExtractedScanDocument>> ExtractDocumentsAsync(AssetId sourceAssetId, IReadOnlyList<ScanExtractionRequestItem> items, CancellationToken cancellationToken = default) =>
            task;
    }
}
