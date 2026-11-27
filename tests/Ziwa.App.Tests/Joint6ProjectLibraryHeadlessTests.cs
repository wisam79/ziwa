using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Application.Persistence;

namespace Ziwa.App.Tests;

public sealed class Joint6ProjectLibraryHeadlessTests
{
    [AvaloniaFact]
    public async Task ProjectLibrarySearchFilterRemoveIndexAndQuickStartHub_WorkEndToEnd()
    {
        string root = Path.Combine(Path.GetTempPath(), "ziwa-joint6-lib-" + Guid.NewGuid().ToString("N"));
        string savedPath = Path.Combine(root, "customer-passport.ziwa");
        var dialogs = new TestDialogs(savedPath);
        try
        {
            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(new ProjectStorageOptions(root), dialogs);
            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                await vm.InitializeProjectsAsync();

                // Rename project & save
                vm.EditableDocumentTitle = "ملف جواز السفر - خالد";
                vm.RenameDocumentCommand.Execute(null);
                Assert.Equal("ملف جواز السفر - خالد", vm.DocumentTitle);
                await vm.SaveProjectCommand.ExecuteAsync(null);
                Assert.True(File.Exists(savedPath));

                await vm.OpenProjectLibraryCommand.ExecuteAsync(null);
                Assert.True(vm.IsProjectLibraryOpen);
                Assert.Single(vm.FilteredProjectEntries);
                Assert.False(vm.HasNoFilteredProjects);

                // Search filter
                vm.ProjectLibrarySearchQuery = "غير-موجود";
                Assert.Empty(vm.FilteredProjectEntries);
                Assert.True(vm.HasNoFilteredProjects);

                vm.ProjectLibrarySearchQuery = "خالد";
                Assert.Single(vm.FilteredProjectEntries);

                // Select & remove from library index (disk file remains intact)
                vm.SelectedProject = vm.FilteredProjectEntries[0];
                Assert.True(vm.HasSelectedProject);
                await vm.RemoveSelectedProjectFromLibraryCommand.ExecuteAsync(null);
                Assert.Empty(vm.ProjectEntries);
                Assert.True(File.Exists(savedPath));

                // Launch QuickStart directly from library -> closes library automatically when clean
                await vm.StartQuickStartFromLibraryCommand.ExecuteAsync("quickstart.idPhotoSheet");
                Assert.False(vm.IsProjectLibraryOpen);
                Assert.True(vm.IsCollageMode);

                Assert.True(await vm.RequestCloseAsync());
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [AvaloniaFact]
    public async Task DocumentRename_SupportsSingleUndoAndRejectsBlankName()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            string initialTitle = vm.DocumentTitle;
            vm.EditableDocumentTitle = "   ";
            vm.RenameDocumentCommand.Execute(null);
            Assert.Equal(initialTitle, vm.DocumentTitle);

            vm.EditableDocumentTitle = "بطاقة دعوة زفاف VIP";
            vm.RenameDocumentCommand.Execute(null);
            Assert.Equal("بطاقة دعوة زفاف VIP", vm.DocumentTitle);

            await vm.UndoCommand.ExecuteAsync(null);
            Assert.Equal(initialTitle, vm.DocumentTitle);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class TestDialogs(string savePath) : IProjectDialogs
    {
        public Task<string?> PickOpenProjectAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(savePath);
        public Task<string?> PickSaveProjectAsync(string title, string? currentPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>(savePath);
        public Task<string?> PickOpenBackupAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveBackupAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(UnsavedChangesChoice.Discard);
    }
}
