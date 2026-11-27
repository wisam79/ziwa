using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.Controls;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Models;

namespace Ziwa.App.Tests;

public sealed class ContextualQuickBarHeadlessTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhotoToolsAtMinimumSize_FitCanvasAndOpenLocalScannerByKeyboard(bool identitySlot)
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("ziwa-quickbar-");
        try
        {
            using ServiceProvider services = Bootstrapper.BuildServiceProvider(new ProjectStorageOptions(root.FullName));
            var vm = services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 960, Height = 640 };
            try
            {
                window.Show();
                if (identitySlot)
                {
                    await vm.StartIdPhotoSheetCommand.ExecuteAsync(null);
                    CollageSlot slot = vm.ActivePage.CollageLayout!.Slots.First(s => s.AssignedAssetId is not null);
                    vm.Session.SelectCollageSlot(slot.Id);
                    Assert.True(vm.CanFillSheetWithSelectedSlot);
                }
                else
                {
                    vm.Session.SetSelection([vm.ActivePage.Nodes.OfType<ImageNode>().First().Id]);
                }

                Layout(window);
                Assert.Equal(new Size(960, 640), window.ClientSize);
                Assert.True(window.FindControl<Border>("InspectorRegion")!.IsEffectivelyVisible);
                var canvas = Assert.IsType<SkiaViewportHost>(window.FindControl<SkiaViewportHost>("ViewportHost"));
                var bar = Assert.IsType<Border>(window.FindControl<Border>("ContextualQuickBar"));
                AssertInside(canvas, window);
                AssertInside(bar, canvas);
                Button[] direct = bar.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToArray();
                Assert.All(direct, button =>
                {
                    AssertInside(button, bar);
                    AssertInside(button, canvas);
                    Assert.True(button.Bounds.Width >= 28 && button.Bounds.Height >= 28, button.Name);
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                    Assert.NotNull(ToolTip.GetTip(button));
                });

                Assert.Single(direct.Select(b => b.PointToScreen(default).Y).Distinct());
                ICommand[] expectedDirect = identitySlot
                    ? [vm.ImportImageCommand, vm.RotateSelectedSlotCommand, vm.ClearSelectedSlotCommand]
                    : [vm.EnterSelectedCropModeCommand, vm.DuplicateSelectionCommand, vm.AlignSelectionCenterHCommand,
                       vm.AlignSelectionCenterVCommand, vm.DeleteSelectionCommand];
                foreach (ICommand command in expectedDirect)
                {
                    Assert.Single(direct, b => b.Command == command);
                }

                Assert.DoesNotContain(direct, b => b.Command == (identitySlot ? (ICommand)vm.DeleteSelectionCommand : vm.ClearSelectedSlotCommand));
                Button primary = Assert.Single(direct, b => b.Classes.Contains("quickBarLabeledIcon"));
                TextBlock caption = Assert.Single(primary.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("quickBarCaption"));
                Assert.False(caption.IsVisible);
                double compactWidth = primary.Bounds.Width;
                window.Width = 1360;
                Layout(window);
                Assert.True(caption.IsVisible);
                Assert.True(primary.Bounds.Width > compactWidth);
                AssertInside(bar, canvas);
                window.Width = 960;
                Layout(window);
                Assert.False(caption.IsVisible);

                Button tools = Assert.Single(direct, b => b.Flyout is MenuFlyout);
                Assert.Contains(tools.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "المزيد");
                MenuFlyout menu = Assert.IsType<MenuFlyout>(tools.Flyout);
                Assert.True(tools.Focus(NavigationMethod.Tab));
                Press(window, PhysicalKey.Enter);
                Assert.True(menu.IsOpen);

                MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
                foreach (ICommand command in new ICommand[] { vm.RemoveBackgroundCommand, vm.AutoFrameFaceCommand, vm.OpenDocumentScannerCommand, vm.EnhancePhotoCloudCommand })
                {
                    Assert.DoesNotContain(direct, b => b.Command == command);
                    MenuItem item = Assert.Single(items, i => i.Command == command);
                    Assert.True(item.IsEffectivelyVisible && item.IsEffectivelyEnabled);
                    Assert.Equal(FlowDirection.RightToLeft, item.FlowDirection);
                    Assert.Contains(command == vm.EnhancePhotoCloudCommand ? "سحابي" : "محلي", Assert.IsType<string>(item.Header));
                    AssertInside(item, canvas);
                }

                ICommand[] slotCommands = [vm.FillAllSlotsWithSelectedSlotCommand, vm.FlipSelectedSlotCommand, vm.ResetSelectedSlotTransformCommand];
                ICommand[] layerCommands = [vm.BringForwardSelectionCommand, vm.SendBackwardSelectionCommand];
                foreach (ICommand command in slotCommands.Concat(layerCommands))
                {
                    Assert.DoesNotContain(direct, b => b.Command == command);
                    MenuItem item = Assert.Single(items, i => i.Command == command);
                    Assert.Equal(identitySlot == slotCommands.Contains(command), item.IsEffectivelyVisible);
                }

                MenuItem scanner = Assert.Single(items, i => i.Command == vm.OpenDocumentScannerCommand);
                for (int i = 0; i < items.Length && !scanner.IsFocused; ++i)
                {
                    Press(window, PhysicalKey.ArrowDown);
                }

                Assert.True(scanner.IsFocused);
                Press(window, PhysicalKey.Enter);
                Assert.False(menu.IsOpen);
                Assert.NotNull(vm.OpenDocumentScannerCommand.ExecutionTask);
                await vm.OpenDocumentScannerCommand.ExecutionTask!;
                Assert.True(vm.IsDocumentScannerOpen);
                Assert.NotNull(vm.ScannerSourceBitmap);
                vm.CloseDocumentScannerCommand.Execute(null);

                if (identitySlot)
                {
                    var before = vm.ActivePage.CollageLayout!.Slots;
                    var selected = vm.Session.SelectedCollageSlotId;
                    Button clear = Assert.Single(direct, b => b.Command == vm.ClearSelectedSlotCommand);
                    Assert.Contains("تفريغ الخانة", Assert.IsType<string>(clear.Content));
                    Assert.True(clear.Focus(NavigationMethod.Tab));
                    Press(window, PhysicalKey.Enter);
                    var after = vm.ActivePage.CollageLayout.Slots;
                    Assert.Equal(before.Select(s => s.Id), after.Select(s => s.Id));
                    Assert.Null(Assert.Single(after, s => s.Id == selected).AssignedAssetId);
                    Assert.True(tools.IsEffectivelyVisible);
                    Assert.True(tools.Focus(NavigationMethod.Tab));
                    Press(window, PhysicalKey.Enter);
                    Assert.True(menu.IsOpen);
                    Assert.False(scanner.IsEffectivelyVisible);
                    Assert.True(Assert.Single(items, i => i.Command == vm.FlipSelectedSlotCommand).IsEffectivelyVisible);
                    menu.Hide();
                }
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static void AssertInside(Control control, Control owner)
    {
        // Screen space is shared by the native popup, the RTL toolbar and the LTR canvas.
        static Rect ScreenBounds(Control element)
        {
            PixelPoint start = element.PointToScreen(default);
            PixelPoint end = element.PointToScreen(new Point(element.Bounds.Width, element.Bounds.Height));
            return new Rect(new Point(start.X, start.Y), new Point(end.X, end.Y)).Normalize();
        }

        Rect bounds = ScreenBounds(control);
        Rect container = ScreenBounds(owner);
        Assert.True(bounds.Width > 0 && bounds.Height > 0 && container.Contains(bounds),
            $"{control.Name ?? AutomationProperties.GetName(control)}: {bounds} outside {owner.Name}: {container}");
    }

    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
        Layout(window);
    }

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
