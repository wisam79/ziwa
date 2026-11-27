using System.Text.RegularExpressions;

namespace Ziwa.Domain.Platform;

public enum AppUpdateErrorCode
{
    MissingChecksum,
    InvalidChecksumFormat,
    InsecureUrl,
    InsecureRedirect,
    ChecksumMismatch,
    UnexpectedContentType,
    SizeLimitExceeded,
    NetworkError,
    InvalidResponse,
}

public sealed class AppUpdateVerificationException : Exception
{
    public AppUpdateVerificationException(AppUpdateErrorCode errorCode, string messageAr, Exception? innerException = null)
        : base(messageAr, innerException)
    {
        ErrorCode = errorCode;
    }

    public AppUpdateErrorCode ErrorCode { get; }
}

public sealed record AppUpdateInfo(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseNotes,
    string DownloadUrl,
    string ExpectedSha256,
    string SignatureBase64 = "");

public sealed record AppUpdateDownloadResult(
    string VerifiedInstallerPath,
    string ActualSha256,
    long FileSizeBytes);

public static class AppUpdatePolicy
{
    public const long MaxInstallerBytes = 250L * 1024L * 1024L;
    public const int Sha256HexLength = 64;

    public static bool TryNormalizeSha256Hex(string? candidate, out string normalizedHex)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            normalizedHex = string.Empty;
            return false;
        }

        string trimmed = candidate.Trim().ToLowerInvariant();
        if (trimmed.Length != Sha256HexLength)
        {
            normalizedHex = string.Empty;
            return false;
        }

        foreach (char c in trimmed)
        {
            bool isHex = (c is >= '0' and <= '9') || (c is >= 'a' and <= 'f');
            if (!isHex)
            {
                normalizedHex = string.Empty;
                return false;
            }
        }

        normalizedHex = trimmed;
        return true;
    }

    public static bool IsSecureHttpsUri(Uri? uri, bool allowInsecureHttpForTesting = false)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return allowInsecureHttpForTesting &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVersionGreater(string? latestVersion, string? currentVersion)
    {
        Version latest = ParseComparableVersion(latestVersion);
        Version current = ParseComparableVersion(currentVersion);
        return latest > current;
    }

    public static string ParseChecksumForInstaller(string checksumsText, string preferredArtifactToken = "ziwa")
    {
        if (string.IsNullOrWhiteSpace(checksumsText))
        {
            return string.Empty;
        }

        string fallback = string.Empty;
        string[] lines = checksumsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !TryNormalizeSha256Hex(parts[0], out string sha))
            {
                continue;
            }

            string fileName = parts[^1].TrimStart('*').ToLowerInvariant();
            if (fileName.Contains(preferredArtifactToken, StringComparison.Ordinal) &&
                (fileName.EndsWith(".exe", StringComparison.Ordinal) ||
                 fileName.EndsWith(".msi", StringComparison.Ordinal) ||
                 fileName.EndsWith(".zip", StringComparison.Ordinal)))
            {
                return sha;
            }

            if (fallback.Length == 0 && fileName.EndsWith(".exe", StringComparison.Ordinal))
            {
                fallback = sha;
            }
        }

        return fallback;
    }

    private static Version ParseComparableVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Version(0, 0, 0);
        }

        string cleaned = raw.Trim();
        if (cleaned.StartsWith('v') || cleaned.StartsWith('V'))
        {
            cleaned = cleaned[1..];
        }

        int dashIdx = cleaned.IndexOf('-');
        if (dashIdx >= 0)
        {
            cleaned = cleaned[..dashIdx];
        }

        string[] parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int major = parts.Length > 0 && int.TryParse(parts[0], out int p0) && p0 >= 0 ? p0 : 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out int p1) && p1 >= 0 ? p1 : 0;
        int patch = parts.Length > 2 && int.TryParse(parts[2], out int p2) && p2 >= 0 ? p2 : 0;
        return new Version(major, minor, patch);
    }
}

public static partial class SensitiveLogRedactor
{
    [GeneratedRegex(@"(?i)(Bearer\s+)[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?i)([?&](?:token|apikey|access_token|refresh_token|code|state)=)[^&\s""']+")]
    private static partial Regex QuerySecretRegex();

    [GeneratedRegex(@"(?i)(#session=)[^\s""']+")]
    private static partial Regex SessionHashRegex();

    [GeneratedRegex(@"(?i)(""(?:access_token|refresh_token|password|apikey|license_key|X-Bridge-Token)""\s*:\s*"")[^""]*(""?)")]
    private static partial Regex JsonSecretRegex();

    [GeneratedRegex(@"(?i)(X-Bridge-Token\s*[:=]\s*)[^\s,;]+")]
    private static partial Regex BridgeHeaderRegex();

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        string result = BearerTokenRegex().Replace(input, "$1[REDACTED]");
        result = QuerySecretRegex().Replace(result, "$1[REDACTED]");
        result = SessionHashRegex().Replace(result, "$1[REDACTED]");
        result = JsonSecretRegex().Replace(result, "$1[REDACTED]$2");
        result = BridgeHeaderRegex().Replace(result, "$1[REDACTED]");
        return result;
    }
}

public static class StartupArgumentParser
{
    public static string? TryExtractZiwaProjectPath(string[]? args)
    {
        if (args is null || args.Length == 0)
        {
            return null;
        }

        foreach (string rawArg in args)
        {
            if (string.IsNullOrWhiteSpace(rawArg))
            {
                continue;
            }

            string candidate = rawArg.Trim().Trim('"');
            if (candidate.StartsWith('-'))
            {
                continue;
            }

            if (candidate.EndsWith(".ziwa", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}
