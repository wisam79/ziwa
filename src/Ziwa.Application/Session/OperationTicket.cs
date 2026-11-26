using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Session;

public sealed record OperationTicket(
    Guid OperationId,
    DocumentId DocumentId,
    NodeId? TargetNodeId,
    CancellationToken Token,
    SlotId? TargetSlotId = null,
    PageId? TargetPageId = null,
    long? ExpectedGeneration = null,
    AssetId? ExpectedSourceAssetId = null);
