using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using Serilog.Core;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Platform;

namespace Ziwa.Infrastructure.Diagnostics;

public sealed class SerilogAppLogger : IStructuredAppLogger, IDisposable
{
    private readonly Logger _logger;
    private readonly IAtomicFileWriter _atomicWriter;
    private readonly string _logDirectory;
    private bool _disposed;

    public SerilogAppLogger(ProjectStorageOptions storageOptions, IAtomicFileWriter atomicWriter)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(atomicWriter);

        _atomicWriter = atomicWriter;
        _logDirectory = Path.Combine(storageOptions.RootDirectory, "Logs");
        Directory.CreateDirectory(_logDirectory);

        string logTemplate = Path.Combine(_logDirectory, "ziwa-.log");
        _logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: logTemplate,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10L * 1024L * 1024L,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{Category}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    public string LogDirectory => _logDirectory;

    public void LogInfo(string category, string message)
    {
        if (_disposed)
        {
            return;
        }

        _logger
            .ForContext("Category", SanitizeCategory(category))
            .Information("{SanitizedMessage}", SensitiveLogRedactor.Redact(message));
    }

    public void LogWarning(string category, string message)
    {
        if (_disposed)
        {
            return;
        }

        _logger
            .ForContext("Category", SanitizeCategory(category))
            .Warning("{SanitizedMessage}", SensitiveLogRedactor.Redact(message));
    }

    public void LogError(string category, string message, Exception? exception = null)
    {
        if (_disposed)
        {
            return;
        }

        string safeMessage = SensitiveLogRedactor.Redact(message);
        if (exception is null)
        {
            _logger.ForContext("Category", SanitizeCategory(category)).Error("{SanitizedMessage}", safeMessage);
        }
        else
        {
            string safeExText = SensitiveLogRedactor.Redact(exception.ToString());
            _logger.ForContext("Category", SanitizeCategory(category)).Error("{SanitizedMessage} | {SanitizedException}", safeMessage, safeExText);
        }
    }

    public async Task<string> ExportSanitizedSupportLogsAsync(
        string destinationZipPath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(destinationZipPath))
        {
            throw new ArgumentException("Destination path cannot be empty.", nameof(destinationZipPath));
        }

        string fullDest = Path.GetFullPath(destinationZipPath);
        string? destDir = Path.GetDirectoryName(fullDest);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        await _atomicWriter.WriteAtomicallyAsync(fullDest, async (outputStream, ct) =>
        {
            using var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true);

            ZipArchiveEntry summaryEntry = archive.CreateEntry("diagnostics-summary.txt", CompressionLevel.Optimal);
            await using (Stream summaryStream = summaryEntry.Open())
            await using (var writer = new StreamWriter(summaryStream, new UTF8Encoding(false), leaveOpen: true))
            {
                string summary = $"""
                    Ziwa Studio Support Diagnostics
                    ExportedAtUtc: {DateTimeOffset.UtcNow:O}
                    OSDescription: {RuntimeInformation.OSDescription}
                    OSArchitecture: {RuntimeInformation.OSArchitecture}
                    Framework: {RuntimeInformation.FrameworkDescription}
                    LogDirectoryExists: {Directory.Exists(_logDirectory)}
                    """;
                await writer.WriteAsync(SensitiveLogRedactor.Redact(summary)).ConfigureAwait(false);
            }

            if (Directory.Exists(_logDirectory))
            {
                FileInfo[] logFiles = new DirectoryInfo(_logDirectory)
                    .GetFiles("*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(14)
                    .ToArray();

                foreach (FileInfo logFile in logFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    ZipArchiveEntry entry = archive.CreateEntry($"logs/{logFile.Name}", CompressionLevel.Optimal);
                    await using Stream entryStream = entry.Open();
                    await using var writer = new StreamWriter(entryStream, new UTF8Encoding(false), leaveOpen: true);
                    await using var fs = new FileStream(logFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                    {
                        await writer.WriteLineAsync(SensitiveLogRedactor.Redact(line)).ConfigureAwait(false);
                    }
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        LogInfo("Diagnostics", $"Exported sanitized support logs package ({Path.GetFileName(fullDest)})");
        return fullDest;
    }

    private static string SanitizeCategory(string? category) =>
        string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logger.Dispose();
    }
}
