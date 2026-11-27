using Ziwa.Domain.Bridge;

namespace Ziwa.Infrastructure.Bridge;

public sealed class PhoneBridgeOptions
{
    public int PreferredPort { get; init; } = PhoneBridgeUrlPolicy.DefaultPort;
    public long MaxUploadBytes { get; init; } = PhoneBridgeUrlPolicy.MaxUploadBytes;
    public string CloudCameraBaseUrl { get; init; } = PhoneBridgeUrlPolicy.DefaultCloudCameraBaseUrl;
    public bool BindLoopbackOnly { get; init; }
    public bool EnableCloudSync { get; init; } = true;
    public TimeSpan CloudPollInterval { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan RealtimeHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(25);
}
