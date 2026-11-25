using Xunit;
using Ziwa.Infrastructure.IO;

namespace Ziwa.Infrastructure.Tests;

public sealed class AtomicFileWriterTests
{
    [Fact]
    public async Task WriteBytesAtomicallyAsync_CreatesAndOverwritesWithoutLeavingTempFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"ziwa-atomic-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string target = Path.Combine(tempDir, "project.bin");
            var writer = new AtomicFileWriter();

            byte[] v1 = [1, 2, 3, 4];
            await writer.WriteBytesAtomicallyAsync(target, v1);
            Assert.Equal(v1, await File.ReadAllBytesAsync(target));

            byte[] v2 = [10, 20, 30, 40, 50];
            await writer.WriteBytesAtomicallyAsync(target, v2);
            Assert.Equal(v2, await File.ReadAllBytesAsync(target));

            string[] leftoverTemps = Directory.GetFiles(tempDir, "*.tmp");
            Assert.Empty(leftoverTemps);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WriteBytesAtomicallyAsync_WhenCancelled_PreservesOriginalAndCleansTempFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"ziwa-atomic-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string target = Path.Combine(tempDir, "keep.bin");
            byte[] original = [9, 8, 7];
            await File.WriteAllBytesAsync(target, original);

            var writer = new AtomicFileWriter();
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => writer.WriteBytesAtomicallyAsync(target, new byte[] { 1, 2, 3 }, cts.Token));

            Assert.Equal(original, await File.ReadAllBytesAsync(target));
            Assert.Empty(Directory.GetFiles(tempDir, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
