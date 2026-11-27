using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SkiaSharp;
using Xunit;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.App.Views.Workspace;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Models;
#if WINDOWS
using Ziwa.Platform.Windows.Shell;
using Ziwa.Platform.Windows.SingleInstance;
#endif

namespace Ziwa.App.Tests;

public sealed class Phase5WindowChromeDropAndSingleInstanceHeadlessTests
{
    [AvaloniaFact]
    public async Task FramelessWindowAndDrop_RespectCaptionButtonsAndFilterNonImages()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-p503-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(
                storageOptions: new ProjectStorageOptions(tempRoot));

            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 960, Height = 640 };
            window.Show();

            Assert.True(window.ExtendClientAreaToDecorationsHint);
            Assert.True(DragDrop.GetAllowDrop(window));
            Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);

            WindowTitleBarView titleBar = Assert.Single(window.GetVisualDescendants().OfType<WindowTitleBarView>());
            foreach (Button btn in titleBar.GetVisualDescendants().OfType<Button>())
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(btn)), $"Missing AutomationProperties.Name on '{btn.Name}'.");
                Assert.NotNull(ToolTip.GetTip(btn));
            }

            AppUpdateView updateView = Assert.Single(window.GetVisualDescendants().OfType<AppUpdateView>());
            Assert.Equal(FlowDirection.RightToLeft, updateView.FlowDirection);
            foreach (Button btn in updateView.GetVisualDescendants().OfType<Button>())
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(btn)), $"Missing AutomationProperties.Name on '{btn.Name}'.");
                Assert.NotNull(ToolTip.GetTip(btn));
            }

            string textPath = Path.Combine(tempRoot, "not-image.txt");
            await File.WriteAllTextAsync(textPath, "hello");
            int beforeCount = vm.ActivePage.Nodes.Length;

            await vm.HandleDroppedPathsAsync([textPath]);
            Assert.Equal(beforeCount, vm.ActivePage.Nodes.Length);
            Assert.Contains("تجاهل", vm.StatusMessage, StringComparison.Ordinal);

            string pngPath = Path.Combine(tempRoot, "dropped_card.png");
            using (var bmp = new SKBitmap(16, 12))
            {
                bmp.Erase(SKColors.SteelBlue);
                using SKData encoded = bmp.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(pngPath, encoded.ToArray());
            }

            await vm.HandleDroppedPathsAsync([textPath, pngPath]);
            Assert.Equal(beforeCount + 1, vm.ActivePage.Nodes.Length);
            ImageNode added = Assert.IsType<ImageNode>(vm.ActivePage.Nodes[^1]);
            Assert.Equal("dropped_card", added.Name);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

#if WINDOWS
    [Fact]
    public async Task WindowsSingleInstanceAndFileAssociation_WorkEndToEnd()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string unique = Guid.NewGuid().ToString("N");
        string mutexName = $@"Local\ZiwaStudio_Test_{unique}";
        string pipeName = $"ZiwaStudio_Pipe_Test_{unique}";

        using var primary = new WindowsSingleInstanceCoordinator(mutexName, pipeName);
        var activatedTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.SecondaryInstanceActivated += path => activatedTcs.TrySetResult(path);

        Assert.True(primary.TryAcquireOrForward([], out bool primaryForwarded));
        Assert.False(primaryForwarded);
        primary.StartListening();

        using var secondary = new WindowsSingleInstanceCoordinator(mutexName, pipeName);
        bool acquiredSecondary = secondary.TryAcquireOrForward([@"C:\Projects\wedding.ziwa"], out bool forwarded);
        Assert.False(acquiredSecondary);
        Assert.True(forwarded);

        string? receivedPath = await activatedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(@"C:\Projects\wedding.ziwa", receivedPath);

        string testClassesPrefix = $@"Software\ZiwaStudioTests\Classes_{unique}";
        try
        {
            var association = new WindowsFileAssociationService(testClassesPrefix, @"C:\Program Files\ZiwaStudio\Ziwa.App.exe");
            Assert.False(association.IsZiwaExtensionRegistered());
            association.EnsureZiwaExtensionRegistered();
            Assert.True(association.IsZiwaExtensionRegistered());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\ZiwaStudioTests\Classes_{unique}", throwOnMissingSubKey: false);
        }
    }
#endif
}
