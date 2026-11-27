using System.Text.Json.Nodes;
using Xunit;
using Ziwa.Application.Persistence;
using Ziwa.Application.Services;
using Ziwa.Application.Session;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Tests;

public sealed class Phase4ProjectWorkspaceTests
{
    [Fact]
    public async Task DirtyState_IgnoresSelectionAndTransientPreviewAndTracksSaveUndoRedo()
    {
        using var fixture = new Fixture();
        SceneNode first = fixture.Session.CommittedDocument.ActivePage.Nodes[0];
        fixture.Session.SetSelection([first.Id]);
        Assert.False(fixture.Workspace.IsDirty);
        fixture.Session.BeginInteraction("preview");
        fixture.Session.UpdateTransientPreview(doc => doc with { Title = "changed" });
        Assert.False(fixture.Workspace.IsDirty);
        fixture.Session.CommitInteraction();
        Assert.True(fixture.Workspace.IsDirty);
        Assert.True(await fixture.Workspace.SaveAsync());
        Assert.False(fixture.Workspace.IsDirty);
        fixture.Session.Undo();
        Assert.True(fixture.Workspace.IsDirty);
        fixture.Session.Redo();
        Assert.False(fixture.Workspace.IsDirty);
        Assert.Equal("memory.ziwa", fixture.Workspace.CurrentPath);
    }

    [Fact]
    public async Task SaveWhileEditing_DoesNotMarkNewerChangesAsSaved()
    {
        using var fixture = new Fixture();
        fixture.Session.ApplyCommand("first", doc => doc with { Title = "first" });
        fixture.Store.SaveBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> save = fixture.Workspace.SaveAsync();
        Assert.True(fixture.Workspace.IsBusy);
        fixture.Session.ApplyCommand("second", doc => doc with { Title = "second" });
        fixture.Store.SaveBlock.SetResult();
        Assert.True(await save);
        Assert.True(fixture.Workspace.IsDirty);
        Assert.Equal("first", fixture.Store.Saved!.Document.Title);
        Assert.Equal("second", fixture.Session.CommittedDocument.Title);
    }

    [Fact]
    public async Task OpenReplacedSessionWithSameId_RejectsLateResult()
    {
        using var fixture = new Fixture();
        fixture.Store.Loaded = new ProjectSnapshot(fixture.Session.CommittedDocument with { Title = "loaded" }, []);
        fixture.Store.LoadBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> opening = fixture.Workspace.OpenAsync("source.ziwa");
        fixture.Session.ReplaceDocument(fixture.Session.CommittedDocument with { Title = "replacement with same id" });
        fixture.Store.LoadBlock.SetResult();
        Assert.False(await opening);
        Assert.Equal("replacement with same id", fixture.Session.CommittedDocument.Title);
        Assert.Null(fixture.Workspace.CurrentPath);
    }

    [Fact]
    public async Task CancelledPickerAndFailedSavePreventReplacingDirtyProjectOrClosing()
    {
        using var fixture = new Fixture();
        fixture.Session.ApplyCommand("change", doc => doc with { Title = "keep" });
        fixture.Dialogs.Choice = UnsavedChangesChoice.Save;
        fixture.Dialogs.SavePath = null;
        Assert.False(await fixture.Workspace.ReplaceAsync(fixture.Session.CommittedDocument with { Title = "replace" }));
        Assert.Equal("keep", fixture.Session.CommittedDocument.Title);
        fixture.Dialogs.SavePath = "memory.ziwa";
        fixture.Store.FailSave = true;
        Assert.False(await fixture.Workspace.RequestCloseAsync());
        Assert.True(fixture.Workspace.IsDirty);
        Assert.False(fixture.Crash.Completed);
        fixture.Dialogs.Choice = UnsavedChangesChoice.Cancel;
        Assert.False(await fixture.Workspace.OpenAsync("open.ziwa"));
        Assert.Equal("keep", fixture.Session.CommittedDocument.Title);
    }

    [Fact]
    public async Task IndexFailureAfterSave_ReportsSuccessfulFileAndLeavesProjectClean()
    {
        using var fixture = new Fixture();
        fixture.Session.ApplyCommand("change", doc => doc with { Title = "changed" });
        fixture.Library.Fail = true;
        Assert.True(await fixture.Workspace.SaveAsync());
        Assert.NotNull(fixture.Store.Saved);
        Assert.False(fixture.Workspace.IsDirty);
        Assert.Contains("الملف صالح", fixture.Workspace.StatusMessage);
    }

    [Fact]
    public async Task RestoreRecovery_StaysDirtyAndOriginalSnapshotIsKeptUntilSuccessfulSave()
    {
        using var fixture = new Fixture();
        var entry = new RecoveryEntry(Guid.NewGuid().ToString("N"), "recovered", DateTimeOffset.UtcNow, "original.ziwa");
        fixture.Crash.Entries = [entry];
        fixture.Crash.Snapshot = new ProjectSnapshot(fixture.Session.CommittedDocument with { Title = "recovered" }, []);
        Assert.True(await fixture.Workspace.InitializeAsync());
        Assert.True(await fixture.Workspace.RestoreAsync(entry));
        Assert.True(fixture.Workspace.IsDirty);
        Assert.Null(fixture.Workspace.CurrentPath);
        Assert.Null(fixture.Crash.Discarded);
        Assert.True(await fixture.Workspace.SaveAsync());
        Assert.False(fixture.Workspace.IsDirty);
        Assert.Equal(entry.Id, fixture.Crash.Discarded);
    }

    [Fact]
    public async Task RecoveryCapture_UsesCommittedStateAndUndoToSavedStateClearsDirtyRecovery()
    {
        using var fixture = new Fixture();
        await fixture.Workspace.InitializeAsync();
        fixture.Session.ApplyCommand("change", doc => doc with { Title = "committed" });
        fixture.Session.BeginInteraction("preview");
        fixture.Session.UpdateTransientPreview(doc => doc with { Title = "transient" });
        Assert.True(await fixture.Workspace.CaptureRecoveryAsync());
        Assert.Equal("committed", fixture.Crash.Snapshot!.Document.Title);
        fixture.Session.CancelInteraction();
        fixture.Session.Undo();
        Assert.False(fixture.Workspace.IsDirty);
        await fixture.Workspace.CaptureRecoveryAsync();
        Assert.True(fixture.Crash.Cleared);
    }

    [Fact]
    public void ProjectJson_RejectsDuplicatePropertiesAndUnsupportedEnumValues()
    {
        Assert.Throws<InvalidDataException>(() => ProjectDocumentSerializer.ValidateUniqueProperties("{\"title\":\"first\",\"Title\":\"second\"}"u8));
        ZiwaDocument document = SampleVerticalSliceFactory.CreateShowcaseDocument(new AssetId("sample"));
        byte[] json = ProjectDocumentSerializer.Serialize(document);
        JsonObject node = JsonNode.Parse(json)!.AsObject();
        node["mode"] = 12345;
        Assert.Throws<InvalidDataException>(() => ProjectDocumentSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Fact]
    public void MigrationPipeline_IsSequentialAndDoesNotModifyInput()
    {
        var original = new JsonObject { ["schemaVersion"] = 1 };
        JsonObject migrated = ProjectDocumentSerializer.ApplyMigrations(original, 3, [new Migration(1), new Migration(2)]);
        Assert.Equal(1, original["schemaVersion"]!.GetValue<int>());
        Assert.Equal(3, migrated["schemaVersion"]!.GetValue<int>());
        Assert.Throws<InvalidDataException>(() => ProjectDocumentSerializer.ApplyMigrations(original, 3, [new Migration(1)]));
        Assert.Throws<InvalidDataException>(() => ProjectDocumentSerializer.ApplyMigrations(new JsonObject { ["schemaVersion"] = 4 }, 3, []));
    }

    private sealed class Migration(int version) : IDocumentMigrator
    {
        public int FromVersion => version;
        public int ToVersion => version + 1;
        public JsonObject Migrate(JsonObject document) { document["schemaVersion"] = ToVersion; return document; }
    }

    private sealed class Fixture : IDisposable
    {
        public DocumentSession Session { get; } = new(SampleVerticalSliceFactory.CreateShowcaseDocument(new AssetId("sample")));
        public Store Store { get; } = new();
        public Dialogs Dialogs { get; } = new();
        public Library Library { get; } = new();
        public Crash Crash { get; } = new();
        public ProjectWorkspace Workspace { get; }
        public Fixture() => Workspace = new ProjectWorkspace(Session, Store, new Assets(), Library, Crash, Dialogs, TimeSpan.FromHours(1));
        public void Dispose() => Workspace.Dispose();
    }

    private sealed class Assets : IProjectAssetStore
    {
        public ProjectSnapshot Capture(ZiwaDocument document) => new(document, []);
        public void Restore(ProjectSnapshot snapshot) { }
    }

    private sealed class Store : IProjectStore
    {
        public ProjectSnapshot? Saved { get; private set; }
        public ProjectSnapshot? Loaded { get; set; }
        public TaskCompletionSource? SaveBlock { get; set; }
        public TaskCompletionSource? LoadBlock { get; set; }
        public bool FailSave { get; set; }
        public async Task SaveAsync(string path, ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (SaveBlock is not null) { await SaveBlock.Task.WaitAsync(cancellationToken); }
            if (FailSave) { throw new IOException("Injected failure."); }
            Saved = snapshot;
        }
        public async Task<ProjectSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (LoadBlock is not null) { await LoadBlock.Task.WaitAsync(cancellationToken); }
            return Loaded ?? throw new InvalidDataException("No snapshot.");
        }
    }

    private sealed class Dialogs : IProjectDialogs
    {
        public string? SavePath { get; set; } = "memory.ziwa";
        public UnsavedChangesChoice Choice { get; set; } = UnsavedChangesChoice.Cancel;
        public Task<string?> PickOpenProjectAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveProjectAsync(string title, string? currentPath, CancellationToken cancellationToken = default) => Task.FromResult(SavePath);
        public Task<string?> PickOpenBackupAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveBackupAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Choice);
    }

    private sealed class Library : IProjectLibrary
    {
        public bool Fail { get; set; }
        public Task<IReadOnlyList<ProjectLibraryEntry>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProjectLibraryEntry>>([]);
        public Task RecordAsync(ProjectLibraryEntry entry, CancellationToken cancellationToken = default) => Fail ? Task.FromException(new IOException("Index failure.")) : Task.CompletedTask;
        public Task RemoveAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ExportBackupAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> ImportBackupAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class Crash : ICrashGuard
    {
        public IReadOnlyList<RecoveryEntry> Entries { get; set; } = [];
        public ProjectSnapshot? Snapshot { get; set; }
        public string? Discarded { get; private set; }
        public bool Completed { get; private set; }
        public bool Cleared { get; private set; }
        public Task<IReadOnlyList<RecoveryEntry>> StartAsync(CancellationToken cancellationToken = default) => Task.FromResult(Entries);
        public Task CaptureAsync(ProjectSnapshot snapshot, string? projectPath, CancellationToken cancellationToken = default) { Snapshot = snapshot; return Task.CompletedTask; }
        public Task<ProjectSnapshot> LoadRecoveryAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot!);
        public Task DiscardRecoveryAsync(string id, CancellationToken cancellationToken = default) { Discarded = id; return Task.CompletedTask; }
        public Task ClearCurrentAsync(CancellationToken cancellationToken = default) { Cleared = true; return Task.CompletedTask; }
        public Task CompleteAsync(CancellationToken cancellationToken = default) { Completed = true; return Task.CompletedTask; }
    }
}
