using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Ziwa.Application.Abstractions;
using Ziwa.Domain.Platform;

namespace Ziwa.Infrastructure.Updates;

public sealed class SecureAppUpdaterService : IAppUpdaterService, IDisposable
{
    private readonly AppUpdaterOptions _options;
    private readonly IStructuredAppLogger? _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private bool _disposed;

    public SecureAppUpdaterService(AppUpdaterOptions options, IStructuredAppLogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;

        if (options.CustomHttpHandler is not null)
        {
            _httpClient = new HttpClient(options.CustomHttpHandler, disposeHandler: false)
            {
                Timeout = options.RequestTimeout,
            };
            _ownsClient = false;
        }
        else
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
            };
            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = options.RequestTimeout,
            };
            _ownsClient = true;
        }
    }

    public async Task<AppUpdateInfo> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Uri feedUri = _options.ResolveValidatedFeedUri();
        if (!AppUpdatePolicy.IsSecureHttpsUri(feedUri, _options.AllowInsecureHttpForTesting))
        {
            throw new AppUpdateVerificationException(
                AppUpdateErrorCode.InsecureUrl,
                "رفض التحقق من التحديث: رابط مصدر التحديثات يجب أن يستخدم HTTPS.");
        }

        string json;
        try
        {
            using HttpResponseMessage resp = await SendSecureGetWithRedirectsAsync(feedUri, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new AppUpdateVerificationException(
                    AppUpdateErrorCode.NetworkError,
                    $"تعذر التحقق من التحديثات (HTTP {(int)resp.StatusCode}).");
            }

            json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AppUpdateVerificationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AppUpdateVerificationException(
                AppUpdateErrorCode.NetworkError,
                $"تعذر الاتصال بخادم التحديثات: {ex.Message}",
                ex);
        }

        string tagName;
        string releaseNotes;
        string expectedSha256 = string.Empty;
        string signature = string.Empty;
        string downloadUrl = string.Empty;
        string checksumsAssetUrl = string.Empty;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            tagName = root.TryGetProperty("tag_name", out JsonElement tagEl) ? tagEl.GetString() ?? string.Empty : string.Empty;
            releaseNotes = root.TryGetProperty("body", out JsonElement bodyEl) ? bodyEl.GetString() ?? string.Empty : string.Empty;
            if (root.TryGetProperty("sha256", out JsonElement shaEl))
            {
                expectedSha256 = shaEl.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("signature", out JsonElement sigEl))
            {
                signature = sigEl.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("download_url", out JsonElement dlEl))
            {
                downloadUrl = dlEl.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("assets", out JsonElement assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assetsEl.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out JsonElement nEl) ? nEl.GetString() ?? string.Empty : string.Empty;
                    string assetUrl = asset.TryGetProperty("browser_download_url", out JsonElement uEl) ? uEl.GetString() ?? string.Empty : string.Empty;
                    string lower = name.ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(assetUrl))
                    {
                        continue;
                    }

                    if (lower.Contains("checksums", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(checksumsAssetUrl))
                    {
                        checksumsAssetUrl = assetUrl;
                    }
                    else if (string.IsNullOrWhiteSpace(downloadUrl) &&
                             (lower.EndsWith(".exe", StringComparison.Ordinal) || lower.EndsWith(".msi", StringComparison.Ordinal)))
                    {
                        downloadUrl = assetUrl;
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            throw new AppUpdateVerificationException(
                AppUpdateErrorCode.InvalidResponse,
                "استجابة خادم التحديثات غير صالحة.",
                ex);
        }

        if (string.IsNullOrWhiteSpace(expectedSha256) && !string.IsNullOrWhiteSpace(checksumsAssetUrl) &&
            Uri.TryCreate(checksumsAssetUrl, UriKind.Absolute, out Uri? checksumUri) &&
            AppUpdatePolicy.IsSecureHttpsUri(checksumUri, _options.AllowInsecureHttpForTesting))
        {
            expectedSha256 = await TryFetchChecksumFromUriAsync(checksumUri, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(expectedSha256) &&
            _options.ResolveValidatedChecksumsFallbackUri() is { } fallbackChecksumUri)
        {
            expectedSha256 = await TryFetchChecksumFromUriAsync(fallbackChecksumUri, cancellationToken).ConfigureAwait(false);
        }

        string normalizedSha = AppUpdatePolicy.TryNormalizeSha256Hex(expectedSha256, out string validSha)
            ? validSha
            : string.Empty;

        bool hasUpdate = AppUpdatePolicy.IsVersionGreater(tagName, _options.CurrentVersion);
        _logger?.LogInfo("Updater", $"Checked updates: current={_options.CurrentVersion}, latest={tagName}, hasUpdate={hasUpdate}");

        return new AppUpdateInfo(
            hasUpdate,
            _options.CurrentVersion,
            tagName,
            releaseNotes,
            downloadUrl,
            normalizedSha,
            signature);
    }

    public async Task<AppUpdateDownloadResult> DownloadAndVerifyUpdateAsync(
        AppUpdateInfo update,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(update);

        if (string.IsNullOrWhiteSpace(update.ExpectedSha256))
        {
            throw new AppUpdateVerificationException(
                AppUpdateErrorCode.MissingChecksum,
                "رفض تثبيت التحديث: لم يتم نشر بصمة SHA-256 لهذا الإصدار (Fail-Closed).");
        }

        if (!AppUpdatePolicy.TryNormalizeSha256Hex(update.ExpectedSha256, out string expectedSha))
        {
            throw new AppUpdateVerificationException(
                AppUpdateErrorCode.InvalidChecksumFormat,
                "رفض تثبيت التحديث: صيغة بصمة SHA-256 غير صالحة.");
        }

        if (!Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out Uri? downloadUri) ||
            !AppUpdatePolicy.IsSecureHttpsUri(downloadUri, _options.AllowInsecureHttpForTesting))
        {
            throw new AppUpdateVerificationException(
                AppUpdateErrorCode.InsecureUrl,
                "رفض تثبيت التحديث: رابط التنزيل غير مشفّر (يجب أن يبدأ بـ https://).");
        }

        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string tempDir = _options.UpdatesTempDirectory
            ?? Path.Combine(Path.GetTempPath(), "ziwa-updates");
        Directory.CreateDirectory(tempDir);

        string randomSuffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        string installerPath = Path.Combine(tempDir, $"ZiwaStudio-Setup-{randomSuffix}.exe");

        try
        {
            using HttpResponseMessage resp = await SendSecureGetWithRedirectsAsync(downloadUri, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new AppUpdateVerificationException(
                    AppUpdateErrorCode.NetworkError,
                    $"فشل تنزيل حزمة التحديث (HTTP {(int)resp.StatusCode}).");
            }

            string? contentType = resp.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(contentType) && !IsAllowedInstallerContentType(contentType))
            {
                throw new AppUpdateVerificationException(
                    AppUpdateErrorCode.UnexpectedContentType,
                    $"رفض حزمة التحديث: نوع المحتوى غير متوقع ({contentType}).");
            }

            if (resp.Content.Headers.ContentLength is long declaredLength && declaredLength > _options.MaxInstallerBytes)
            {
                throw new AppUpdateVerificationException(
                    AppUpdateErrorCode.SizeLimitExceeded,
                    "رفض حزمة التحديث: حجم الملف المعلن يتجاوز الحد المسموح.");
            }

            long totalExpected = resp.Content.Headers.ContentLength ?? 0;
            long totalWritten = 0;
            int lastPercent = -1;

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (Stream remoteStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var fileStream = new FileStream(
                installerPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    int read = await remoteStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    totalWritten += read;
                    if (totalWritten > _options.MaxInstallerBytes)
                    {
                        throw new AppUpdateVerificationException(
                            AppUpdateErrorCode.SizeLimitExceeded,
                            "رفض حزمة التحديث: تجاوز الملف الحد الأقصى للحجم أثناء التنزيل.");
                    }

                    hasher.AppendData(buffer.AsSpan(0, read));
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                    if (totalExpected > 0 && progress is not null)
                    {
                        int pct = (int)Math.Clamp((totalWritten * 100L) / totalExpected, 0, 99);
                        if (pct != lastPercent)
                        {
                            lastPercent = pct;
                            progress.Report(pct);
                        }
                    }
                }

                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                fileStream.Flush(flushToDisk: true);
            }

            string actualSha = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (!string.Equals(actualSha, expectedSha, StringComparison.Ordinal))
            {
                TryDeleteFile(installerPath);
                _logger?.LogError("Updater", $"Installer SHA-256 mismatch: expected={expectedSha}, actual={actualSha}");
                throw new AppUpdateVerificationException(
                    AppUpdateErrorCode.ChecksumMismatch,
                    "فشل التحقق من سلامة ملف التحديث (بصمة SHA-256 غير مطابقة). تم حذف الملف وحظر التثبيت.");
            }

            progress?.Report(100);
            _logger?.LogInfo("Updater", $"Verified update installer SHA-256={actualSha} ({totalWritten} bytes)");
            return new AppUpdateDownloadResult(installerPath, actualSha, totalWritten);
        }
        catch
        {
            TryDeleteFile(installerPath);
            throw;
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<bool> LaunchVerifiedInstallerAsync(
        AppUpdateDownloadResult verifiedUpdate,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(verifiedUpdate);

        if (!File.Exists(verifiedUpdate.VerifiedInstallerPath) ||
            !AppUpdatePolicy.TryNormalizeSha256Hex(verifiedUpdate.ActualSha256, out string expectedSha))
        {
            return false;
        }

        // إعادة التحقق من البصمة قبل الإطلاق مباشرة لمنع أي استبدال للملف (TOCTOU)
        string currentSha;
        await using (var fs = new FileStream(verifiedUpdate.VerifiedInstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            byte[] hash = await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false);
            currentSha = Convert.ToHexStringLower(hash);
        }

        if (!string.Equals(currentSha, expectedSha, StringComparison.Ordinal))
        {
            TryDeleteFile(verifiedUpdate.VerifiedInstallerPath);
            throw new AppUpdateVerificationException(
                AppUpdateErrorCode.ChecksumMismatch,
                "تم تعديل ملف المثبت بعد التحقق — تم حظر الإطلاق وحذف الملف.");
        }

        var psi = new ProcessStartInfo(verifiedUpdate.VerifiedInstallerPath)
        {
            UseShellExecute = true,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
        };
        Process? started = Process.Start(psi);
        return started is not null;
    }

    private async Task<HttpResponseMessage> SendSecureGetWithRedirectsAsync(Uri initialUri, CancellationToken ct)
    {
        Uri currentUri = initialUri;
        for (int redirectCount = 0; redirectCount < 5; redirectCount++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
            req.Headers.TryAddWithoutValidation("User-Agent", $"ZiwaStudio-Desktop/{_options.CurrentVersion}");

            HttpResponseMessage resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!IsRedirectStatusCode(resp.StatusCode))
            {
                return resp;
            }

            Uri? location = resp.Headers.Location;
            resp.Dispose();
            if (location is null)
            {
                throw new AppUpdateVerificationException(
                    AppUpdateErrorCode.NetworkError,
                    "استجابة إعادة التوجيه من خادم التحديثات تفتقر إلى الوجهة.");
            }

            Uri nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            if (!AppUpdatePolicy.IsSecureHttpsUri(nextUri, _options.AllowInsecureHttpForTesting))
            {
                throw new AppUpdateVerificationException(
                    AppUpdateErrorCode.InsecureRedirect,
                    "رفض التحديث: محاولة إعادة توجيه إلى رابط غير مشفّر (HTTP).");
            }

            currentUri = nextUri;
        }

        throw new AppUpdateVerificationException(
            AppUpdateErrorCode.NetworkError,
            "تجاوز عدد إعادات التوجيه المسموح به عند تنزيل التحديث.");
    }

    private async Task<string> TryFetchChecksumFromUriAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage resp = await SendSecureGetWithRedirectsAsync(uri, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return AppUpdatePolicy.ParseChecksumForInstaller(text);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsAllowedInstallerContentType(string mediaType)
    {
        string lower = mediaType.Trim().ToLowerInvariant();
        return lower is
            "application/octet-stream" or
            "application/x-msdownload" or
            "application/x-msdos-program" or
            "application/exe" or
            "application/vnd.microsoft.portable-executable";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }

        _updateGate.Dispose();
    }
}
