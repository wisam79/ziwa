using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Persistence;
using Ziwa.Application.Services;
using Ziwa.Application.Stickers;
using Ziwa.Application.Workspace;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Infrastructure.IO;
using Ziwa.Infrastructure.Persistence;
using Ziwa.Rendering.Assets;

namespace Ziwa.Infrastructure.Tests;

public sealed class Phase4PersistenceTests
{
    [Fact]
    public async Task Project_RoundTripsAllBackgroundsNodesPagesCollageAndOriginalAssets()
    {
        using var fixture = new Fixture();
        ProjectSnapshot expected = fixture.Assets.Capture(fixture.Document);
        string path = fixture.Path("project.ziwa");
        await fixture.Store.SaveAsync(path, expected);
        ProjectSnapshot actual = await fixture.Store.LoadAsync(path);
        Assert.Equal(ProjectDocumentSerializer.CanonicalFingerprint(expected.Document), ProjectDocumentSerializer.CanonicalFingerprint(actual.Document));
        Assert.Equal(expected.Assets.Length, actual.Assets.Length);
        foreach (ProjectAsset asset in expected.Assets)
        {
            Assert.Equal(asset.Data, actual.Assets.Single(a => a.Id == asset.Id).Data);
        }

        using var restoredStore = new AssetStore();
        var restored = new RenderingProjectAssetStore(restoredStore);
        restored.Restore(actual);
        Assert.Equal(ProjectDocumentSerializer.CanonicalFingerprint(expected.Document), ProjectDocumentSerializer.CanonicalFingerprint(restored.Capture(actual.Document).Document));
        using var archive = ZipFile.OpenRead(path);
        Assert.Equal(expected.Assets.Length + 1, archive.Entries.Count);
        Assert.Equal(2, actual.Assets.Count(a => a.Kind == ProjectAssetKind.Raster));
        Assert.Single(actual.Assets.Where(a => a.Kind == ProjectAssetKind.Svg));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("checksum")]
    [InlineData("path")]
    [InlineData("future")]
    [InlineData("duplicate")]
    [InlineData("oversize")]
    [InlineData("opacity")]
    public async Task Project_RejectsCorruptionUnsafeEntriesAndUnsupportedSchemas(string corruption)
    {
        using var fixture = new Fixture();
        string path = fixture.Path("bad.ziwa");
        await fixture.Store.SaveAsync(path, fixture.Assets.Capture(fixture.Document));
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            if (corruption == "missing") { archive.Entries.First(e => e.FullName.StartsWith("assets/", StringComparison.Ordinal)).Delete(); }
            else if (corruption is "path" or "duplicate")
            {
                using Stream output = archive.CreateEntry(corruption == "path" ? "../outside.bin" : "manifest.json").Open();
                output.WriteByte(1);
            }
            else
            {
                ZipArchiveEntry entry = archive.GetEntry("manifest.json")!;
                JsonObject manifest;
                using (var reader = new StreamReader(entry.Open())) { manifest = JsonNode.Parse(reader.ReadToEnd())!.AsObject(); }
                if (corruption == "future") { manifest["document"]!["schemaVersion"] = 2; }
                if (corruption == "opacity") { manifest["document"]!["pages"]![0]!["nodes"]![0]!["opacity"] = 5; }
                if (corruption == "oversize") { manifest["assets"]![0]!["size"] = ZiwaProjectStore.MaxAssetBytes + 1L; }
                if (corruption == "checksum") { manifest["assets"]![0]!["sha256"] = new string('0', 64); }
                entry.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
                writer.Write(manifest.ToJsonString());
            }
        }

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => fixture.Store.LoadAsync(path));
    }

    [Fact]
    public async Task AtomicStreamingWrite_FailureHalfwayPreservesOriginalAndCleansTemporaryFile()
    {
        using var fixture = new Fixture();
        string path = fixture.Path("original.ziwa");
        byte[] original = [1, 2, 3, 4];
        await fixture.Writer.WriteBytesAtomicallyAsync(path, original);
        await Assert.ThrowsAsync<IOException>(() => fixture.Writer.WriteAtomicallyAsync(path, async (stream, ct) =>
        {
            await stream.WriteAsync(new byte[] { 9, 8 }, ct);
            throw new IOException("Injected write failure.");
        }));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public async Task Library_WalVersionedMigrationAndBackupRestorePreserveProjectsWithoutOverwrite()
    {
        using var fixture = new Fixture();
        using var library = new SqliteProjectRepo(new ProjectStorageOptions(fixture.Path("library")), fixture.Store, fixture.Writer);
        string projectPath = fixture.Path("project.ziwa");
        await fixture.Store.SaveAsync(projectPath, fixture.Assets.Capture(fixture.Document));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await library.RecordAsync(new ProjectLibraryEntry(projectPath, fixture.Document.Id, "عنوان ' آمن", now, now));
        await library.RecordAsync(new ProjectLibraryEntry(projectPath, fixture.Document.Id, "عنوان ' آمن", now, now));
        Assert.Single(await library.ListAsync());
        using (var connection = new SqliteConnection($"Data Source={fixture.Path("library/projects.db")};Foreign Keys=True;Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", await command.ExecuteScalarAsync());
            command.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Version = 1;";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
            command.CommandText = "PRAGMA foreign_keys;";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }

        string backup = fixture.Path("library.ziwabackup");
        await library.ExportBackupAsync(backup);
        byte[] before = await File.ReadAllBytesAsync(projectPath);
        Assert.Equal(1, await library.ImportBackupAsync(backup));
        Assert.Equal(before, await File.ReadAllBytesAsync(projectPath));
        IReadOnlyList<ProjectLibraryEntry> entries = await library.ListAsync();
        Assert.Equal(2, entries.Count);
        string importedPath = entries.Single(e => e.Path != projectPath).Path;
        ProjectSnapshot imported = await fixture.Store.LoadAsync(importedPath);
        Assert.Equal(ProjectDocumentSerializer.CanonicalFingerprint(fixture.Document), ProjectDocumentSerializer.CanonicalFingerprint(imported.Document));
    }

    [Fact]
    public async Task Library_RejectsTraversalBackupWithoutChangingExistingLibrary()
    {
        using var fixture = new Fixture();
        using var library = new SqliteProjectRepo(new ProjectStorageOptions(fixture.Path("library")), fixture.Store, fixture.Writer);
        string path = fixture.Path("unsafe.ziwabackup");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using Stream stream = archive.CreateEntry("../outside.ziwa").Open();
            stream.WriteByte(1);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => library.ImportBackupAsync(path));
        Assert.Empty(await library.ListAsync());
        Assert.False(File.Exists(fixture.Path("outside.ziwa")));
    }

    [Fact]
    public async Task CrashGuard_LiveSessionIsExcludedAndAbandonedSessionRestoresAllAssets()
    {
        using var fixture = new Fixture();
        var options = new ProjectStorageOptions(fixture.Path("recovery-root"));
        using var first = new CrashGuard(options, fixture.Store, fixture.Writer);
        Assert.Empty(await first.StartAsync());
        await first.CaptureAsync(fixture.Assets.Capture(fixture.Document), null);
        using (var second = new CrashGuard(options, fixture.Store, fixture.Writer))
        {
            Assert.Empty(await second.StartAsync());
            await second.CompleteAsync();
        }

        first.Dispose();
        using var next = new CrashGuard(options, fixture.Store, fixture.Writer);
        RecoveryEntry recovery = Assert.Single(await next.StartAsync());
        ProjectSnapshot restored = await next.LoadRecoveryAsync(recovery.Id);
        Assert.Equal(ProjectDocumentSerializer.CanonicalFingerprint(fixture.Document), ProjectDocumentSerializer.CanonicalFingerprint(restored.Document));
        Assert.Equal(3, restored.Assets.Length);
        await next.CompleteAsync();
        Assert.True(Directory.Exists(fixture.Path("recovery-root/recovery/" + recovery.Id)));
    }

    [Fact]
    public async Task CrashGuard_MetadataFailureKeepsPreviousCompleteSnapshotAndCleanExitOnlyDeletesOwnSession()
    {
        using var fixture = new Fixture();
        var options = new ProjectStorageOptions(fixture.Path("crash"));
        var fault = new FailingManifestWriter(fixture.Writer);
        using (var first = new CrashGuard(options, fixture.Store, fault))
        {
            await first.StartAsync();
            await first.CaptureAsync(fixture.Assets.Capture(fixture.Document), null);
            fault.Fail = true;
            await Assert.ThrowsAsync<IOException>(() => first.CaptureAsync(fixture.Assets.Capture(fixture.Document with { Title = "new" }), null));
        }

        using var next = new CrashGuard(options, fixture.Store, fixture.Writer);
        RecoveryEntry recovery = Assert.Single(await next.StartAsync());
        Assert.Equal(fixture.Document.Title, (await next.LoadRecoveryAsync(recovery.Id)).Document.Title);
        await next.DiscardRecoveryAsync(recovery.Id);
        await next.CompleteAsync();
        Assert.Empty(Directory.GetDirectories(fixture.Path("crash/recovery")));
    }

    [Fact]
    public async Task CrashGuard_CorruptSnapshotIsRetainedAndCancelledCaptureDoesNotReplaceIt()
    {
        using var fixture = new Fixture();
        var options = new ProjectStorageOptions(fixture.Path("crash"));
        using (var first = new CrashGuard(options, fixture.Store, fixture.Writer))
        {
            await first.StartAsync();
            await first.CaptureAsync(fixture.Assets.Capture(fixture.Document), null);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.CaptureAsync(fixture.Assets.Capture(fixture.Document), null, cancelled.Token));
        }

        string snapshot = Directory.GetFiles(fixture.Path("crash/recovery"), "snapshot-*.ziwa", SearchOption.AllDirectories).Single();
        await File.WriteAllBytesAsync(snapshot, new byte[] { 1, 2 });
        using var next = new CrashGuard(options, fixture.Store, fixture.Writer);
        Assert.Empty(await next.StartAsync());
        Assert.True(File.Exists(snapshot));
        await next.CompleteAsync();
    }

    private sealed class FailingManifestWriter(AtomicFileWriter writer) : IAtomicFileWriter
    {
        public bool Fail { get; set; }
        public Task WriteBytesAtomicallyAsync(string destinationPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            if (Fail && destinationPath.EndsWith("manifest.json", StringComparison.Ordinal)) { throw new IOException("Injected metadata failure."); }
            return writer.WriteBytesAtomicallyAsync(destinationPath, content, cancellationToken);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ziwa-p4-" + Guid.NewGuid().ToString("N"));
        public AssetStore AssetStore { get; } = new();
        public AtomicFileWriter Writer { get; } = new();
        public ZiwaProjectStore Store { get; }
        public RenderingProjectAssetStore Assets { get; }
        public ZiwaDocument Document { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Store = new ZiwaProjectStore(Writer);
            Assets = new RenderingProjectAssetStore(AssetStore);
            byte[] image = ProceduralEmblemPngBuilder.BuildEmblemPngBytes();
            AssetId original = AssetStore.RegisterAsset(image);
            using var bitmap = new SkiaSharp.SKBitmap(8, 8);
            bitmap.Erase(SkiaSharp.SKColors.Green);
            using var encoded = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var bytes = encoded.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            AssetId current = AssetStore.RegisterAsset(bytes.ToArray());
            AssetId svg = AssetStore.RegisterSvgAsset(QuickStartAndBrandService.DefaultStudioLogoSvg);
            ZiwaDocument sample = SampleVerticalSliceFactory.CreateShowcaseDocument(original);
            ImageNode photo = sample.ActivePage.Nodes.OfType<ImageNode>().First() with { CurrentAssetId = current };
            var group = new GroupNode(NodeId.New(), "مجموعة", new RectMm(10, 10, 20, 20),
                [new ShapeNode(NodeId.New(), "تعبئة صورية", new RectMm(10, 10, 20, 20), ShapeKind.Rectangle, new ImageShapeFill(current, original, CropSpec.Identity))]);
            PageModel first = sample.ActivePage with
            {
                Nodes = sample.ActivePage.Nodes.Where(n => n is not ImageNode).ToImmutableArray().Add(photo).Add(group)
                    .Add(new SvgNode(NodeId.New(), "SVG", new RectMm(20, 20, 15, 15), svg))
                    .Add(StickerOperations.CreateStickerFromTemplate("commercial-seal", 40, 40)),
            };
            PageModel collage = QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.IdPhotoSheet4x6, original).ActivePage;
            collage = collage with { CollageLayout = collage.CollageLayout! with { PendingAssets = [current] }, Background = new LinearGradientBackground(new ColorRgba(1, 2, 3, 255), new ColorRgba(20, 30, 40, 255)) };
            PageModel radial = first with { Id = PageId.New(), Name = "تدرج دائري", Nodes = [], Background = new RadialGradientBackground(new ColorRgba(1, 2, 3, 255), new ColorRgba(20, 30, 40, 255)) };
            PageModel transparent = radial with { Id = PageId.New(), Name = "شفافة", Background = new TransparentBackground() };
            Document = sample with { Pages = [first, collage, radial, transparent] };
        }

        public string Path(string name) => System.IO.Path.Combine(Root, name);
        public void Dispose()
        {
            AssetStore.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
