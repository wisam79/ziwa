using Ziwa.Domain.Bridge;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Abstractions;

public sealed record PhoneBridgeReceivedPhoto(
    AssetId AssetId,
    int WidthPx,
    int HeightPx,
    PhoneBridgePhotoSource SourceKind,
    DateTimeOffset ReceivedAtUtc,
    string? DisplayName = null);

public interface IPhoneBridgeService
{
    event Action<PhoneBridgeStatus>? StatusChanged;
    event Action<string>? DevicePaired;
    event Action<PhoneBridgeReceivedPhoto>? PhotoReceived;
    event Action<string>? CloudErrorOccurred;

    PhoneBridgeStatus CurrentStatus { get; }

    Task<PhoneBridgeStartInfo> StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
