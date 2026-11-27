using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ziwa.Application.Commands;
using Ziwa.Application.Persistence;

namespace Ziwa.App.ViewModels;

public sealed partial class MainWindowViewModel : IDisposable
{
    private readonly ProjectWorkspace _projects;
    private RecoveryEntry? _recoveryToDiscard;

    [ObservableProperty]
    private bool _isProjectLibraryOpen;

    [ObservableProperty]
    private ProjectLibraryEntry? _selectedProject;

    [ObservableProperty]
    private RecoveryEntry? _selectedRecovery;

    [ObservableProperty]
    private bool _isRecoveryDiscardConfirmationOpen;

    [ObservableProperty]
    private string _projectLibrarySearchQuery = string.Empty;

    [ObservableProperty]
    private string _editableDocumentTitle = string.Empty;

    public ProjectWorkspace Projects => _projects;
    public bool IsProjectBusy => _projects.IsBusy;
    public bool IsProjectEditable => !_projects.IsBusy;
    public bool HasUnsavedChanges => _projects.IsDirty;
    public bool HasSelectedProject => SelectedProject is not null;
    public bool HasNoFilteredProjects => FilteredProjectEntries.Count == 0;
    public string ProjectLibraryCountText => $"المشاريع الحديثة ({FilteredProjectEntries.Count})";
    public string ProjectWindowTitle => $"{DocumentTitle}{(HasUnsavedChanges ? " • غير محفوظ" : string.Empty)} — Ziwa Studio";
    public string ProjectSaveStateText => HasUnsavedChanges ? "● غير محفوظ" : "محفوظ";
    public ObservableCollection<ProjectLibraryEntry> ProjectEntries { get; } = [];
    public ObservableCollection<ProjectLibraryEntry> FilteredProjectEntries { get; } = [];
    public ObservableCollection<RecoveryEntry> RecoveryEntries { get; } = [];
    public bool HasRecoveries => RecoveryEntries.Count > 0;
    public string RecoveryDiscardTitle => _recoveryToDiscard?.Title ?? string.Empty;

    public async Task InitializeProjectsAsync()
    {
        await _projects.InitializeAsync();
        if (HasRecoveries)
        {
            IsProjectLibraryOpen = true;
            SelectedRecovery = RecoveryEntries[0];
        }
    }

    public async Task<bool> RequestCloseAsync()
    {
        bool approved = await _projects.RequestCloseAsync();
        if (approved)
        {
            await StopPhoneBridgeOnExitAsync();
        }

        return approved;
    }

    private void RegisterProjectCommands()
    {
        RegisterProjectCommand("file.openProject", "فتح مشروع .ziwa", "Ctrl+O", ct => _projects.OpenAsync(ct: ct));
        RegisterProjectCommand("file.saveProject", "حفظ المشروع", "Ctrl+S", ct => _projects.SaveAsync(ct: ct));
        RegisterProjectCommand("file.saveProjectAs", "حفظ المشروع باسم", "Ctrl+Shift+S", ct => _projects.SaveAsync(saveAs: true, ct));
        RegisterProjectCommand("file.projectLibrary", "مكتبة المشاريع والتعافي", string.Empty, async ct =>
        {
            await _projects.RefreshLibraryAsync(ct);
            IsProjectLibraryOpen = true;
        });
        RegisterProjectCommand("file.exportLibraryBackup", "تصدير نسخة احتياطية للمكتبة", string.Empty, ct => _projects.ExportBackupAsync(ct));
        RegisterProjectCommand("file.importLibraryBackup", "استيراد نسخة احتياطية للمكتبة", string.Empty, ct => _projects.ImportBackupAsync(ct));
    }

    private void RegisterProjectCommand(string id, string title, string shortcut, Func<CancellationToken, Task> execute) =>
        _commandRegistry.Register(new CommandDefinition(new CommandId(id), title, shortcut, "مشاريع", () => !IsProjectBusy, execute));

    private void OnProjectsChanged()
    {
        if (!string.IsNullOrWhiteSpace(_projects.StatusMessage))
        {
            StatusMessage = _projects.StatusMessage;
        }

        string? selectedPath = SelectedProject?.Path;
        ProjectEntries.Clear();
        foreach (ProjectLibraryEntry entry in _projects.Projects) { ProjectEntries.Add(entry); }
        RefreshFilteredProjects();
        SelectedProject = selectedPath is not null
            ? FilteredProjectEntries.FirstOrDefault(p => string.Equals(p.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
            : null;

        string? selectedRecoveryId = SelectedRecovery?.Id;
        RecoveryEntries.Clear();
        foreach (RecoveryEntry entry in _projects.Recoveries) { RecoveryEntries.Add(entry); }
        SelectedRecovery = RecoveryEntries.FirstOrDefault(r => r.Id == selectedRecoveryId) ?? RecoveryEntries.FirstOrDefault();
        OnPropertyChanged(nameof(IsProjectBusy));
        OnPropertyChanged(nameof(IsProjectEditable));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(HasSelectedProject));
        OnPropertyChanged(nameof(ProjectWindowTitle));
        OnPropertyChanged(nameof(ProjectSaveStateText));
        OnPropertyChanged(nameof(HasRecoveries));
    }

    partial void OnSelectedProjectChanged(ProjectLibraryEntry? value) =>
        OnPropertyChanged(nameof(HasSelectedProject));

    partial void OnProjectLibrarySearchQueryChanged(string value) =>
        RefreshFilteredProjects();

    private void RefreshFilteredProjects()
    {
        string q = ProjectLibrarySearchQuery.Trim();
        FilteredProjectEntries.Clear();
        foreach (ProjectLibraryEntry entry in ProjectEntries)
        {
            if (string.IsNullOrEmpty(q) ||
                entry.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                entry.Path.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredProjectEntries.Add(entry);
            }
        }

        OnPropertyChanged(nameof(HasNoFilteredProjects));
        OnPropertyChanged(nameof(ProjectLibraryCountText));
    }

    [RelayCommand]
    private void RenameDocument()
    {
        string trimmed = EditableDocumentTitle.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            EditableDocumentTitle = Document.Title;
            StatusMessage = "لا يمكن ترك اسم المشروع فارغاً";
            return;
        }

        if (trimmed.Length > 200)
        {
            trimmed = trimmed[..200];
        }

        if (string.Equals(trimmed, Document.Title, StringComparison.Ordinal))
        {
            return;
        }

        _session.ApplyCommand("إعادة تسمية المشروع", doc => doc with { Title = trimmed });
        StatusMessage = $"تمت إعادة تسمية المشروع إلى: {trimmed}";
    }

    [RelayCommand]
    private Task OpenProjectAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("file.openProject"), ct);

    [RelayCommand]
    private Task SaveProjectAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("file.saveProject"), ct);

    [RelayCommand]
    private Task SaveProjectAsAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("file.saveProjectAs"), ct);

    [RelayCommand]
    private Task OpenProjectLibraryAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("file.projectLibrary"), ct);

    [RelayCommand]
    private void CloseProjectLibrary()
    {
        IsProjectLibraryOpen = false;
        IsRecoveryDiscardConfirmationOpen = false;
    }

    [RelayCommand]
    private async Task OpenSelectedProjectAsync(CancellationToken ct)
    {
        if (SelectedProject is { } entry && await _projects.OpenAsync(entry.Path, ct)) { IsProjectLibraryOpen = false; }
    }

    [RelayCommand]
    private async Task RemoveSelectedProjectFromLibraryAsync(CancellationToken ct)
    {
        if (SelectedProject is { } entry)
        {
            await _projects.RemoveFromLibraryAsync(entry.Path, ct);
        }
    }

    [RelayCommand]
    private async Task StartQuickStartFromLibraryAsync(string? commandIdString, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(commandIdString))
        {
            return;
        }

        long genBefore = _session.Generation;
        if (await _commandRegistry.TryExecuteAsync(new CommandId(commandIdString), ct) &&
            _session.Generation != genBefore)
        {
            IsProjectLibraryOpen = false;
        }
    }

    [RelayCommand]
    private async Task RestoreSelectedRecoveryAsync(CancellationToken ct)
    {
        if (SelectedRecovery is { } entry && await _projects.RestoreAsync(entry, ct)) { IsProjectLibraryOpen = false; }
    }

    [RelayCommand]
    private void AskDiscardRecovery()
    {
        _recoveryToDiscard = SelectedRecovery;
        OnPropertyChanged(nameof(RecoveryDiscardTitle));
        IsRecoveryDiscardConfirmationOpen = _recoveryToDiscard is not null;
    }

    [RelayCommand]
    private void CancelDiscardRecovery() => IsRecoveryDiscardConfirmationOpen = false;

    [RelayCommand]
    private async Task ConfirmDiscardRecoveryAsync(CancellationToken ct)
    {
        if (_recoveryToDiscard is { } entry && await _projects.DiscardRecoveryAsync(entry, ct))
        {
            IsRecoveryDiscardConfirmationOpen = false;
            _recoveryToDiscard = null;
        }
    }

    [RelayCommand]
    private Task ExportLibraryBackupAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("file.exportLibraryBackup"), ct);

    [RelayCommand]
    private Task ImportLibraryBackupAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("file.importLibraryBackup"), ct);

    [RelayCommand]
    private void CancelProjectOperation() => _projects.CancelOperation();

    public void Dispose()
    {
        _session.SessionChanged -= OnSessionChanged;
        _projects.Changed -= OnProjectsChanged;
        _stickerPreviewBitmap?.Dispose();
        _stickerPreviewBitmap = null;
        DisposeAiResources();
        DisposeScannerResources();
        DisposeAccountResources();
        DisposePhoneBridgeResources();
    }
}
