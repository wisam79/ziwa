using System.Security.Cryptography;
using System.Text;

namespace Ziwa.Domain.Bridge;

public enum PhoneBridgeMode
{
    Cloud,
    LocalLan,
}

public enum PhoneBridgePhotoSource
{
    LocalLan,
    CloudRelay,
}

public sealed record PhoneBridgeStartInfo(
    string IpAddress,
    int Port,
    string Token,
    string LocalUrl,
    string CloudUrl,
    bool IsRunning);

public sealed record PhoneBridgeStatus(
    bool IsRunning,
    string LocalUrl,
    string CloudUrl,
    string PairedDevice,
    int ReceivedCount,
    string LastReceivedAtText,
    string CloudErrorMessage)
{
    public static PhoneBridgeStatus Stopped { get; } = new(
        IsRunning: false,
        LocalUrl: string.Empty,
        CloudUrl: string.Empty,
        PairedDevice: string.Empty,
        ReceivedCount: 0,
        LastReceivedAtText: string.Empty,
        CloudErrorMessage: string.Empty);
}

public static class PhoneBridgeUrlPolicy
{
    public const int DefaultPort = 8741;
    public const long MaxUploadBytes = 50L * 1024L * 1024L;
    public const string DefaultCloudCameraBaseUrl = "https://grido.cloud-ip.cc/cam";

    public static string FormatLocalPairingUrl(string ipAddress, int port, string sessionToken)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || port <= 0 || string.IsNullOrWhiteSpace(sessionToken))
        {
            return string.Empty;
        }

        return $"http://{ipAddress.Trim()}:{port}/cam?token={Uri.EscapeDataString(sessionToken.Trim())}";
    }

    public static string FormatCloudPairingUrl(string? baseUrl, string? userId, string? sessionToken)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(sessionToken))
        {
            return string.Empty;
        }

        string effectiveBase = string.IsNullOrWhiteSpace(baseUrl)
            ? DefaultCloudCameraBaseUrl
            : baseUrl.Trim().TrimEnd('/');

        return $"{effectiveBase}#session={userId.Trim()}/{sessionToken.Trim()}";
    }

    public static bool TryBuildCloudObjectPrefix(string? userId, string? sessionToken, out string prefix)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(sessionToken))
        {
            prefix = string.Empty;
            return false;
        }

        prefix = $"{userId.Trim()}/{sessionToken.Trim()}";
        return true;
    }

    public static bool IsSafeCloudObjectPath(string? objectPath, string? expectedPrefix)
    {
        if (string.IsNullOrWhiteSpace(objectPath) || string.IsNullOrWhiteSpace(expectedPrefix))
        {
            return false;
        }

        string normalized = objectPath.Trim();
        if (normalized.Contains("..", StringComparison.Ordinal) ||
            normalized.Contains('\\', StringComparison.Ordinal) ||
            normalized.StartsWith('/'))
        {
            return false;
        }

        string prefixWithSlash = expectedPrefix.TrimEnd('/') + "/";
        return normalized.StartsWith(prefixWithSlash, StringComparison.Ordinal) &&
               normalized.Length > prefixWithSlash.Length;
    }
}

public static class PhoneBridgeTokenValidator
{
    public static bool FixedTimeEquals(string? candidateToken, string? expectedToken)
    {
        if (string.IsNullOrEmpty(candidateToken) || string.IsNullOrEmpty(expectedToken))
        {
            return false;
        }

        byte[] candidateBytes = Encoding.UTF8.GetBytes(candidateToken);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        return candidateBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(candidateBytes, expectedBytes);
    }
}
