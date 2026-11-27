using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [AvaloniaFact]
    public async Task LibraryPresets_DisplayRegisteredShortcuts_AndHandleAltOnlyWhileOpen()
    {
        string root = Path.Combine(Path.GetTempPath(), "ziwa-library-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(new ProjectStorageOptions(root), new TestDialogs(Path.Combine(root, "project.ziwa")));
            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            try
            {
                window.Show();
                await vm.OpenProjectLibraryCommand.ExecuteAsync(null);
                // Presets keep their registered shortcut beside the title and their print specs below it.
                window.UpdateLayout();
                var library = Assert.Single(window.GetVisualDescendants().OfType<ProjectLibraryView>());
                var presets = library.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.Command == vm.StartQuickStartFromLibraryCommand).ToList();
                Assert.Equal(4, presets.Count);
                foreach (Button preset in presets)
                {
                    var id = Assert.IsType<string>(preset.CommandParameter);
                    Assert.True(vm.CommandRegistry.TryGet(new(id), out var command));
                    Assert.NotNull(command);
                    Assert.Equal(command.ShortcutGesture, AutomationProperties.GetAcceleratorKey(preset));
                    var labels = preset.GetVisualDescendants().OfType<TextBlock>().ToList();
                    var shortcut = Assert.Single(labels, label => label.Text == command.ShortcutGesture);
                    Assert.True(shortcut.Bounds.Height > 0);
                    double rowTop = shortcut.TranslatePoint(default, preset)!.Value.Y;
                    Assert.Contains(labels, label => label != shortcut && Math.Abs(label.TranslatePoint(default, preset)!.Value.Y - rowTop) < 1);
                    Assert.Contains(labels, label => label.TranslatePoint(default, preset)!.Value.Y >= rowTop + shortcut.Bounds.Height);
                }

                // Alt+1 works while focus is elsewhere in the dialog, then releases the library binding.
                var search = Assert.IsType<TextBox>(library.FindControl<TextBox>("ProjectLibrarySearchBox"));
                Assert.True(search.Focus());
                window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.Alt);
                window.KeyReleaseQwerty(PhysicalKey.Digit1, RawInputModifiers.Alt);
                Dispatcher.UIThread.RunJobs();
                var launch = vm.StartQuickStartFromLibraryCommand.ExecutionTask;
                Assert.NotNull(launch);
                await launch;
                Assert.False(vm.IsProjectLibraryOpen);
                Assert.True(vm.IsCollageMode);

                Dispatcher.UIThread.RunJobs();
                Assert.All(presets, preset => Assert.Null(preset.HotKey));
                window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.Alt);
                window.KeyReleaseQwerty(PhysicalKey.Digit1, RawInputModifiers.Alt);
                Dispatcher.UIThread.RunJobs();
                Assert.Same(launch, vm.StartQuickStartFromLibraryCommand.ExecutionTask);

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
