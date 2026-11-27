using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Application.Collage;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Validation;

namespace Ziwa.App.Tests;

public sealed class Joint4CollageEngineHeadlessTests
{
    [AvaloniaFact]
    public async Task CollagePresetSizingAlignmentAndMixedReflowFix_WorkCleanlyWithUndo()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-joint4-presets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(
                storageOptions: new ProjectStorageOptions(tempRoot));
            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                await vm.StartIdPhotoSheetCommand.ExecuteAsync(null);
                Assert.True(vm.IsCollageMode);
                Assert.Equal(4, vm.ActivePage.CollageLayout!.Slots.Length);

                // Switch to 35x45 passport preset (3x2 = 6 slots)
                CollagePresetOption passportPreset = vm.AvailableCollagePresetOptions.First(p => p.PresetId == "id-35x45");
                vm.SelectedCollagePresetOption = passportPreset;
                Assert.Equal(6, vm.ActivePage.CollageLayout!.Slots.Length);
                Assert.Equal(35.0, vm.ActivePage.CollageLayout.Slots[0].BoundsMm.Width, 1);
                Assert.Equal(45.0, vm.ActivePage.CollageLayout.Slots[0].BoundsMm.Height, 1);

                // Switch to mixed identity sheet (1*3 + 2*4 = 11 slots)
                CollagePresetOption mixedPreset = vm.AvailableCollagePresetOptions.First(p => p.PresetId == "mixed-identity");
                vm.SelectedCollagePresetOption = mixedPreset;
                Assert.Equal(11, vm.ActivePage.CollageLayout!.Slots.Length);

                // Applying uniform 2x3 grid after MixedIdentitySheet must clear MixedBands (bug fix)
                vm.CollageRowsInput = 2;
                vm.CollageColumnsInput = 3;
                vm.SelectedCollageSizingOption = vm.AvailableCollageSizingOptions
                    .First(s => s.Mode == CollageSizingMode.FixedPhysicalCellMm);
                vm.CollageCellWidthInputMm = 30.0;
                vm.CollageCellHeightInputMm = 40.0;
                vm.ApplyCollageGridAdjustmentsCommand.Execute(null);

                Assert.True(vm.ActivePage.CollageLayout!.Spec.MixedBands.IsDefaultOrEmpty);
                Assert.Equal(6, vm.ActivePage.CollageLayout.Slots.Length);
                Assert.True(SchemaValidator.ValidateDocument(vm.Document).IsValid);

                // Undo returns to mixed-identity (11 slots)
                await vm.UndoCommand.ExecuteAsync(null);
                Assert.Equal(11, vm.ActivePage.CollageLayout!.Slots.Length);
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
    public async Task CollageSlotStyleMaskFillAllResetAndClear_WorkEndToEnd()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-joint4-slots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(
                storageOptions: new ProjectStorageOptions(tempRoot));
            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                await vm.StartIdPhotoSheetCommand.ExecuteAsync(null);
                CollageLayoutModel layout = vm.ActivePage.CollageLayout!;
                SlotId slot0Id = layout.Slots[0].Id;
                var photo = new AssetId("joint4-id-photo");

                vm.Session.SelectCollageSlot(slot0Id);
                vm.Session.ApplyCommand("تعيين صورة", d => CollageOperations.AssignAssetToSlot(d, slot0Id, photo));
                Assert.True(vm.CanFillSheetWithSelectedSlot);

                // Fill all slots with selected slot's image + verify AutoRepeat propagation on future AssignAssetToSlot
                vm.FillAllSlotsWithSelectedSlotCommand.Execute(null);
                Assert.True(vm.IsCollageAutoRepeatEnabled);
                Assert.All(vm.ActivePage.CollageLayout!.Slots, s => Assert.Equal(photo, s.AssignedAssetId));

                var photoReplacement = new AssetId("joint4-id-photo-v2");
                vm.Session.ApplyCommand("استبدال مع التكرار", d => CollageOperations.AssignAssetToSlot(d, slot0Id, photoReplacement));
                Assert.All(vm.ActivePage.CollageLayout!.Slots, s => Assert.Equal(photoReplacement, s.AssignedAssetId));

                // Apply mask + corner radius + border to all slots
                vm.SelectedMaskOption = vm.AvailableMaskOptions.First(m => m.Kind == MaskShapeKind.Circle);
                Assert.Equal(MaskShapeKind.Circle, vm.ActivePage.CollageLayout!.Slots[0].Mask);

                vm.CollageSlotCornerRadiusMm = 3.5;
                vm.CollageSlotBorderWidthMm = 1.5;
                vm.CollageSlotBorderColor = Colors.Navy;
                vm.ApplySlotStyleToAllCommand.Execute(null);

                Assert.All(vm.ActivePage.CollageLayout!.Slots, s =>
                {
                    Assert.Equal(MaskShapeKind.Circle, s.Mask);
                    Assert.Equal(3.5, s.CornerRadiusMm, 1);
                    Assert.NotNull(s.Border);
                    Assert.Equal(1.5, s.Border!.WidthMm, 1);
                });

                // Rotate, flip, and reset slot transform
                vm.RotateSelectedSlotCommand.Execute(null);
                vm.FlipSelectedSlotCommand.Execute(null);
                Assert.Equal(1, vm.ActivePage.CollageLayout!.Slots[0].QuarterTurns);
                Assert.True(vm.ActivePage.CollageLayout!.Slots[0].FlipHorizontal);

                vm.ResetSelectedSlotTransformCommand.Execute(null);
                Assert.Equal(0, vm.ActivePage.CollageLayout!.Slots[0].QuarterTurns);
                Assert.False(vm.ActivePage.CollageLayout!.Slots[0].FlipHorizontal);

                // Clear all slots
                vm.ClearAllCollageSlotsCommand.Execute(null);
                Assert.All(vm.ActivePage.CollageLayout!.Slots, s => Assert.Null(s.AssignedAssetId));
                Assert.True(SchemaValidator.ValidateDocument(vm.Document).IsValid);
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
}
