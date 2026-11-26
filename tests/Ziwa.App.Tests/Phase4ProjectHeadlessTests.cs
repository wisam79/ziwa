using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.Services;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Application.Persistence;

namespace Ziwa.App.Tests;

public sealed class Phase4ProjectHeadlessTests
{
    [AvaloniaFact]
    public async Task ProjectSaveOpenAndLibrary_AreWiredAccessibleAndKeepDirtyStateCorrect()
    {
        string root = Path.Combine(Path.GetTempPath(), "ziwa-p4-ui-" + Guid.NewGuid().ToString("N"));
        var dialogs = new Dialogs(Path.Combine(root, "project.ziwa"));
        try
        {
            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(new ProjectStorageOptions(root), dialogs);
            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            window.Show();
            await vm.InitializeProjectsAsync();
            Assert.Equal(Avalonia.Media.FlowDirection.RightToLeft, window.FlowDirection);
            Assert.NotNull(window.FindControl<Button>("ProjectFileMenuButton"));
            vm.Session.ApplyCommand("تغيير العنوان", doc => doc with { Title = "مشروع محفوظ بالعربية" });
            Assert.True(vm.HasUnsavedChanges);
            Assert.Contains("غير محفوظ", window.Title);
            await vm.SaveProjectCommand.ExecuteAsync(null);
            Assert.False(vm.HasUnsavedChanges);
            Assert.True(File.Exists(dialogs.Path));
            Assert.Single(vm.ProjectEntries);
            vm.Session.ApplyCommand("تعديل إضافي", doc => doc with { Title = "تعديل لاحق" });
            dialogs.Choice = UnsavedChangesChoice.Discard;
            await vm.OpenProjectCommand.ExecuteAsync(null);
            Assert.Equal("مشروع محفوظ بالعربية", vm.DocumentTitle);
            Assert.False(vm.HasUnsavedChanges);
            Assert.False(vm.CanUndo);
            await vm.OpenProjectLibraryCommand.ExecuteAsync(null);
            Assert.True(vm.IsProjectLibraryOpen);
            ProjectLibraryView library = Assert.Single(window.GetVisualDescendants().OfType<ProjectLibraryView>());
            Assert.Equal(Avalonia.Media.FlowDirection.RightToLeft, library.FlowDirection);
            foreach (Button button in library.GetVisualDescendants().OfType<Button>())
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                Assert.NotNull(ToolTip.GetTip(button));
            }

            Assert.True(await vm.RequestCloseAsync());
            window.Close();
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [AvaloniaFact]
    public async Task QuickStartAndClose_DoNotDiscardDirtyWorkWhenConfirmationIsCancelled()
    {
        string root = Path.Combine(Path.GetTempPath(), "ziwa-p4-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(new ProjectStorageOptions(root), new Dialogs(Path.Combine(root, "project.ziwa")));
            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            vm.Session.ApplyCommand("تعديل", doc => doc with { Title = "احتفظ بهذا العمل" });
            await vm.StartBusinessCardCommand.ExecuteAsync(null);
            Assert.Equal("احتفظ بهذا العمل", vm.DocumentTitle);
            Assert.False(await vm.RequestCloseAsync());
            Assert.True(vm.HasUnsavedChanges);
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [AvaloniaFact]
    public async Task AbandonedRecovery_IsOfferedRestoredAndRetainedUntilSaved()
    {
        string root = Path.Combine(Path.GetTempPath(), "ziwa-p4-recovery-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (ServiceProvider first = Bootstrapper.BuildServiceProvider(new ProjectStorageOptions(root)))
            {
                var session = first.GetRequiredService<Ziwa.Application.Session.DocumentSession>();
                session.ApplyCommand("عمل قبل الانهيار", doc => doc with { Title = "عمل مستعاد بأصوله" });
                ICrashGuard crash = first.GetRequiredService<ICrashGuard>();
                await crash.StartAsync();
                await crash.CaptureAsync(first.GetRequiredService<IProjectAssetStore>().Capture(session.CommittedDocument), null);
            }

            using ServiceProvider next = Bootstrapper.BuildServiceProvider(new ProjectStorageOptions(root), new Dialogs(Path.Combine(root, "restored.ziwa")));
            MainWindowViewModel vm = next.GetRequiredService<MainWindowViewModel>();
            await vm.InitializeProjectsAsync();
            Assert.True(vm.IsProjectLibraryOpen);
            Assert.Single(vm.RecoveryEntries);
            await vm.RestoreSelectedRecoveryCommand.ExecuteAsync(null);
            Assert.Equal("عمل مستعاد بأصوله", vm.DocumentTitle);
            Assert.True(vm.HasUnsavedChanges);
            Assert.Single(vm.RecoveryEntries);
            Assert.NotEmpty(next.GetRequiredService<IProjectAssetStore>().Capture(vm.Document).Assets);
            await vm.SaveProjectCommand.ExecuteAsync(null);
            Assert.False(vm.HasUnsavedChanges);
            Assert.Empty(vm.RecoveryEntries);
            Assert.True(await vm.RequestCloseAsync());
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Fact]
    public void ProjectShortcutRouting_IsUnifiedAndRecognizesSaveAs()
    {
        Assert.Equal("Ctrl+O", KeyboardCommandRouter.GetGesture(Key.O, KeyModifiers.Control));
        Assert.Equal("Ctrl+S", KeyboardCommandRouter.GetGesture(Key.S, KeyModifiers.Control));
        Assert.Equal("Ctrl+Shift+S", KeyboardCommandRouter.GetGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Null(KeyboardCommandRouter.GetGesture(Key.S, KeyModifiers.Control | KeyModifiers.Alt));
    }

    private sealed class Dialogs(string path) : IProjectDialogs
    {
        public string Path => path;
        public UnsavedChangesChoice Choice { get; set; } = UnsavedChangesChoice.Cancel;
        public Task<string?> PickOpenProjectAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(path);
        public Task<string?> PickSaveProjectAsync(string title, string? currentPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>(path);
        public Task<string?> PickOpenBackupAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveBackupAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Choice);
    }
}
