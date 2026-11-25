using System.Runtime.Versioning;
using Ziwa.Application.Abstractions;

namespace Ziwa.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformCapabilities : IPlatformCapabilities
{
    public string PlatformName => "Windows Desktop (Win32 GDI Ready)";
    public bool SupportsDirectGdiPrint => true;
}
