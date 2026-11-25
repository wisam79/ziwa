using Ziwa.Application.Abstractions;

namespace Ziwa.Infrastructure.Platform;

public sealed class FallbackPlatformCapabilities : IPlatformCapabilities
{
    public string PlatformName => $"Cross-Platform ({Environment.OSVersion.Platform})";
    public bool SupportsDirectGdiPrint => false;
}
