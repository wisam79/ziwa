using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using Xunit;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Platform;
using Ziwa.Infrastructure.Diagnostics;
using Ziwa.Infrastructure.IO;
using Ziwa.Infrastructure.Platform;
using Ziwa.Infrastructure.Updates;

namespace Ziwa.Infrastructure.Tests;

public sealed class Phase5UpdaterAndDiagnosticsTests
{
    [Fact]
    public async Task Updater_FailsClosed_WhenChecksumMissingInvalidMismatchOrInsecureUrl()
    {
        string tempRoot = CreateTempDir("ziwa-p503-upd-");
        try
        {
            byte[] installerBytes = Encoding.UTF8.GetBytes("MZ-FAKE-ZIWA-INSTALLER-BINARY-PAYLOAD");
            string validSha = Convert.ToHexStringLower(SHA256.HashData(installerBytes));

            using var handler = new DelegateHttpHandler((req, _) =>
            {
                if (req.RequestUri!.AbsoluteUri == "https://updates.ziwa.test/releases/latest")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($$"""
                            {
                              "tag_name": "v1.2.0",
                              "body": "Release 1.2.0",
                              "sha256": "{{validSha}}",
                              "download_url": "https://updates.ziwa.test/ZiwaStudio-Setup-1.2.0.exe"
                            }
                            """, Encoding.UTF8, "application/json"),
                    });
                }

                if (req.RequestUri!.AbsoluteUri == "https://updates.ziwa.test/redirect-downgrade.exe")
                {
                    var redir = new HttpResponseMessage(HttpStatusCode.Found);
                    redir.Headers.Location = new Uri("http://insecure.ziwa.test/ZiwaStudio-Setup.exe");
                    return Task.FromResult(redir);
                }

                if (req.RequestUri!.AbsoluteUri == "https://updates.ziwa.test/ZiwaStudio-Setup-1.2.0.exe")
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(installerBytes),
                    };
                    resp.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
                    return Task.FromResult(resp);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

            var options = new AppUpdaterOptions
            {
                CurrentVersion = "1.0.0",
                ReleaseFeedUrl = "https://updates.ziwa.test/releases/latest",
                UpdatesTempDirectory = Path.Combine(tempRoot, "updates"),
                CustomHttpHandler = handler,
            };

            using var updater = new SecureAppUpdaterService(options);
            AppUpdateInfo info = await updater.CheckForUpdateAsync();
            Assert.True(info.HasUpdate);
            Assert.Equal("v1.2.0", info.LatestVersion);
            Assert.Equal(validSha, info.ExpectedSha256);

            // 1. Missing SHA-256 -> Fail-Closed
            AppUpdateVerificationException missingEx = await Assert.ThrowsAsync<AppUpdateVerificationException>(() =>
                updater.DownloadAndVerifyUpdateAsync(info with { ExpectedSha256 = string.Empty }));
            Assert.Equal(AppUpdateErrorCode.MissingChecksum, missingEx.ErrorCode);

            // 2. Invalid SHA-256 format -> Fail-Closed
            AppUpdateVerificationException formatEx = await Assert.ThrowsAsync<AppUpdateVerificationException>(() =>
                updater.DownloadAndVerifyUpdateAsync(info with { ExpectedSha256 = "not-a-sha" }));
            Assert.Equal(AppUpdateErrorCode.InvalidChecksumFormat, formatEx.ErrorCode);

            // 3. Insecure HTTP download URL -> Fail-Closed
            AppUpdateVerificationException httpEx = await Assert.ThrowsAsync<AppUpdateVerificationException>(() =>
                updater.DownloadAndVerifyUpdateAsync(info with { DownloadUrl = "http://updates.ziwa.test/setup.exe" }));
            Assert.Equal(AppUpdateErrorCode.InsecureUrl, httpEx.ErrorCode);

            // 4. HTTPS -> HTTP downgrade redirect -> Fail-Closed
            AppUpdateVerificationException redirEx = await Assert.ThrowsAsync<AppUpdateVerificationException>(() =>
                updater.DownloadAndVerifyUpdateAsync(info with { DownloadUrl = "https://updates.ziwa.test/redirect-downgrade.exe" }));
            Assert.Equal(AppUpdateErrorCode.InsecureRedirect, redirEx.ErrorCode);

            // 5. Checksum mismatch -> Fail-Closed and temp file deleted
            AppUpdateVerificationException mismatchEx = await Assert.ThrowsAsync<AppUpdateVerificationException>(() =>
                updater.DownloadAndVerifyUpdateAsync(info with { ExpectedSha256 = new string('0', 64) }));
            Assert.Equal(AppUpdateErrorCode.ChecksumMismatch, mismatchEx.ErrorCode);
            Assert.Empty(Directory.GetFiles(options.UpdatesTempDirectory));

            // 6. Valid HTTPS + matching SHA-256 -> succeeds & reports 100%
            int lastProgress = 0;
            var progress = new Progress<int>(p => lastProgress = p);
            AppUpdateDownloadResult verified = await updater.DownloadAndVerifyUpdateAsync(info, progress);
            Assert.True(File.Exists(verified.VerifiedInstallerPath));
            Assert.Equal(validSha, verified.ActualSha256);

            // 7. TOCTOU tamper before launch -> rejected & deleted
            await File.WriteAllBytesAsync(verified.VerifiedInstallerPath, Encoding.UTF8.GetBytes("TAMPERED"));
            AppUpdateVerificationException tamperEx = await Assert.ThrowsAsync<AppUpdateVerificationException>(() =>
                updater.LaunchVerifiedInstallerAsync(verified));
            Assert.Equal(AppUpdateErrorCode.ChecksumMismatch, tamperEx.ErrorCode);
            Assert.False(File.Exists(verified.VerifiedInstallerPath));
        }
        finally
        {
            CleanupTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task SerilogAppLogger_RedactsSecretsAndExportsSanitizedSupportZip()
    {
        string tempRoot = CreateTempDir("ziwa-p503-logs-");
        try
        {
            var storage = new ProjectStorageOptions(tempRoot);
            var atomicWriter = new AtomicFileWriter();
            string zipPath = Path.Combine(tempRoot, "support-export.zip");

            using (var logger = new SerilogAppLogger(storage, atomicWriter))
            {
                logger.LogInfo("Auth", "Connected with Bearer super-secret-jwt-123 and ?token=secret-bridge-456");
                logger.LogError("Cloud", "Failed call", new InvalidOperationException("Rejected #session=uid-1/tok-99"));
            }

            using (var logger2 = new SerilogAppLogger(storage, atomicWriter))
            {
                string exported = await logger2.ExportSanitizedSupportLogsAsync(zipPath);
                Assert.Equal(Path.GetFullPath(zipPath), exported);
            }

            Assert.True(File.Exists(zipPath));
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            Assert.Contains(archive.Entries, e => e.FullName == "diagnostics-summary.txt");
            ZipArchiveEntry logEntry = Assert.Single(archive.Entries, e => e.FullName.StartsWith("logs/", StringComparison.Ordinal));
            using var reader = new StreamReader(logEntry.Open(), Encoding.UTF8);
            string logContent = await reader.ReadToEndAsync();
            Assert.DoesNotContain("super-secret-jwt-123", logContent, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-bridge-456", logContent, StringComparison.Ordinal);
            Assert.DoesNotContain("uid-1/tok-99", logContent, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", logContent, StringComparison.Ordinal);
        }
        finally
        {
            CleanupTempDir(tempRoot);
        }
    }

    [Fact]
    public void DropFileFilter_AcceptsValidImagesAndRejectsNonImages()
    {
        string tempRoot = CreateTempDir("ziwa-p503-drop-");
        try
        {
            string validPng = Path.Combine(tempRoot, "photo.png");
            using (var bmp = new SKBitmap(8, 8))
            {
                bmp.Erase(SKColors.Teal);
                using SKData encoded = bmp.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(validPng, encoded.ToArray());
            }

            string fakePng = Path.Combine(tempRoot, "fake.png");
            File.WriteAllText(fakePng, "not actually an image");

            string textFile = Path.Combine(tempRoot, "notes.txt");
            File.WriteAllText(textFile, "plain text");

            var filter = new FallbackDropFileFilter();
            IReadOnlyList<string> filtered = filter.FilterSupportedImageFiles([validPng, fakePng, textFile, Path.Combine(tempRoot, "missing.jpg")]);

            string single = Assert.Single(filtered);
            Assert.Equal(Path.GetFullPath(validPng), single);
        }
        finally
        {
            CleanupTempDir(tempRoot);
        }
    }

    private static string CreateTempDir(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanupTempDir(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
