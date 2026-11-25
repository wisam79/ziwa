namespace Ziwa.Application.Abstractions;

public interface IPlatformCapabilities
{
    string PlatformName { get; }
    bool SupportsDirectGdiPrint { get; }
}
