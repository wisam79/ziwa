using System.Reflection;
using Ziwa.Domain.Platform;
using Ziwa.Infrastructure.IO;

namespace Ziwa.Infrastructure.Updates;

public sealed class AppUpdaterOptions
{
    public const string DefaultFeedUrl = "https://api.github.com/repos/wisam79/ziwa/releases/latest";
    public const string DefaultChecksumsUrl = "https://grido.cloud-ip.cc/api/ziwa-checksums";

    public string CurrentVersion { get; init; } = ResolveAssemblyVersion();

    private static string ResolveAssemblyVersion()
    {
        string? infoVer = typeof(AppUpdaterOptions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            int plusIdx = infoVer.IndexOf('+');
            return plusIdx >= 0 ? infoVer[..plusIdx] : infoVer;
        }

        Version? asmVer = typeof(AppUpdaterOptions).Assembly.GetName().Version;
        return asmVer is not null ? $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}" : "1.0.0";
    }
    public string? ReleaseFeedUrl { get; init; }
    public string? ChecksumsFallbackUrl { get; init; }
    public long MaxInstallerBytes { get; init; } = AppUpdatePolicy.MaxInstallerBytes;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public bool AllowInsecureHttpForTesting { get; init; }
    public HttpMessageHandler? CustomHttpHandler { get; init; }
    public string? UpdatesTempDirectory { get; init; }

    public Uri ResolveValidatedFeedUri() =>
        AppEnvConfigReader.ResolveSecureUriOrNull(ReleaseFeedUrl, AllowInsecureHttpForTesting)
        ?? new Uri(DefaultFeedUrl, UriKind.Absolute);

    public Uri? ResolveValidatedChecksumsFallbackUri() =>
        AppEnvConfigReader.ResolveSecureUriOrNull(
            string.IsNullOrWhiteSpace(ChecksumsFallbackUrl) ? DefaultChecksumsUrl : ChecksumsFallbackUrl,
            AllowInsecureHttpForTesting);

    public static AppUpdaterOptions LoadFromAppDataAndEnvironment(string appDataRoot)
    {
        Func<string, string?> read = AppEnvConfigReader.CreateReader(appDataRoot);
        return new AppUpdaterOptions
        {
            ReleaseFeedUrl = read("ZIWA_UPDATE_FEED_URL"),
            ChecksumsFallbackUrl = read("ZIWA_UPDATE_CHECKSUMS_URL"),
        };
    }
}
