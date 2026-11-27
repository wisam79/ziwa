using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.Services;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Platform;
using Ziwa.Infrastructure.DependencyInjection;

namespace Ziwa.App.Tests;

public sealed class Joint9PhoneBridgeAndUpdateHeadlessTests
{
    [AvaloniaFact]
    public async Task AppUpdateView_ShowsReleaseNotesAndSupportsMidDownloadCancellation()
    {
        var fakeUpdater = new FakeUpdaterService();
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-joint9-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var services = new ServiceCollection();
            services.AddZiwaCoreServices(new ProjectStorageOptions(tempRoot));
            services.AddSingleton<IProjectDialogs>(new AvaloniaProjectDialogs());
            services.AddSingleton<ProjectWorkspace>();
            services.AddSingleton<IAppUpdaterService>(fakeUpdater);
            services.AddTransient<MainWindowViewModel>();
            using ServiceProvider sp = services.BuildServiceProvider();

            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                AppUpdateView updateView = Assert.Single(window.GetVisualDescendants().OfType<AppUpdateView>());
                foreach (Button btn in updateView.GetVisualDescendants().OfType<Button>())
                {
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(btn)));
                    Assert.NotNull(ToolTip.GetTip(btn));
                }

                await vm.OpenUpdateDialogCommand.ExecuteAsync(null);
                Assert.True(vm.IsUpdateDialogOpen);
                Assert.True(vm.HasAvailableUpdate);
                Assert.True(vm.HasUpdateReleaseNotes);
                Assert.Contains("تحسينات التجهيز المطبعي", vm.UpdateReleaseNotesText);

                // Start slow download, then cancel it cleanly via CancelUpdateOperationCommand
                Task downloadTask = vm.DownloadAndVerifyUpdateCommand.ExecuteAsync(null);
                await fakeUpdater.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(vm.IsUpdateBusy);

                vm.CancelUpdateOperationCommand.Execute(null);
                await downloadTask;

                Assert.False(vm.IsUpdateBusy);
                Assert.Equal("تم إلغاء تنزيل التحديث", vm.UpdateStatusMessage);

                vm.CloseUpdateDialogCommand.Execute(null);
                Assert.False(vm.IsUpdateDialogOpen);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [AvaloniaFact]
    public async Task PhoneBridgeView_StartsStopsAndExposesAccessibilityAndClipboardCopySafely()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-joint9-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(new ProjectStorageOptions(tempRoot));
            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                PhoneBridgeView bridgeView = Assert.Single(window.GetVisualDescendants().OfType<PhoneBridgeView>());
                foreach (Button btn in bridgeView.GetVisualDescendants().OfType<Button>())
                {
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(btn)));
                    Assert.NotNull(ToolTip.GetTip(btn));
                }

                Assert.False(vm.HasPhoneBridgeReceivedPhotos);
                await vm.OpenPhoneBridgeDialogCommand.ExecuteAsync(null);
                Assert.True(vm.IsPhoneBridgeDialogOpen);

                // Copy URL should not crash in headless environment regardless of clipboard provider
                await vm.CopyPhoneBridgeUrlCommand.ExecuteAsync(null);
                Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));

                await vm.StopPhoneBridgeCommand.ExecuteAsync(null);
                Assert.True(vm.IsPhoneBridgeStopped);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class FakeUpdaterService : IAppUpdaterService
    {
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AppUpdateInfo> CheckForUpdateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppUpdateInfo(
                HasUpdate: true,
                CurrentVersion: "1.0.0",
                LatestVersion: "1.1.0",
                ReleaseNotes: "تحسينات التجهيز المطبعي واستقرار المحرك.",
                DownloadUrl: "https://example.invalid/ziwa-1.1.0.exe",
                ExpectedSha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        public async Task<AppUpdateDownloadResult> DownloadAndVerifyUpdateAsync(
            AppUpdateInfo update,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        public Task<bool> LaunchVerifiedInstallerAsync(AppUpdateDownloadResult verifiedUpdate, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
