using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.App.Tests;

public sealed class Phase5UiPolishHeadlessTests
{
    [AvaloniaFact]
    public async Task InspectorPolish_ColorPickerSlidersPaperCollageAndToastManager_WorkEndToEnd()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-ui-polish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(
                storageOptions: new ProjectStorageOptions(tempRoot));

            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1360, Height = 892 };
            window.Show();

            Assert.NotNull(window.FindControl<WindowNotificationManager>("ToastNotificationManager"));
            Assert.NotNull(window.FindControl<ColorPicker>("TextColorPicker"));
            Assert.NotNull(window.FindControl<ColorPicker>("ShapeFillColorPicker"));
            Assert.NotNull(window.FindControl<ColorPicker>("SvgTintColorPicker"));

            // Toast notifications fire cleanly without throwing
            vm.ShowToast("اختبار", "إشعار تجريبي", NotificationType.Success);

            // Collage grid customization (3x4 with 2mm gap) while project is clean
            await vm.StartIdPhotoSheetCommand.ExecuteAsync(null);
            Assert.True(vm.IsCollageMode);
            vm.CollageRowsInput = 3;
            vm.CollageColumnsInput = 4;
            vm.CollageGapInputMm = 2.0;
            vm.ApplyCollageGridAdjustmentsCommand.Execute(null);
            Assert.Equal(12, vm.ActivePage.CollageLayout!.Slots.Length);

            // Text color & font style update via Inspector ColorPicker
            await vm.AddHeadingTextCommand.ExecuteAsync(null);
            TextNode textNode = Assert.IsType<TextNode>(vm.ActivePage.Nodes.Last(n => n is TextNode));
            vm.Session.SetSelection([textNode.Id]);
            vm.SelectedTextColor = Colors.Crimson;
            vm.SelectedTextFontSizePt = 36.0;
            vm.ApplySelectedTextStyleCommand.Execute(null);

            TextNode updatedText = Assert.IsType<TextNode>(vm.ActivePage.Nodes.First(n => n.Id == textNode.Id));
            Assert.Equal(Colors.Crimson.R, updatedText.Color.R);
            Assert.Equal(36.0, updatedText.FontSizePt);

            // Shape fill color & opacity update via Inspector ColorPicker
            await vm.AddStarShapeCommand.ExecuteAsync(null);
            ShapeNode shapeNode = Assert.IsType<ShapeNode>(vm.ActivePage.Nodes.Last(n => n is ShapeNode));
            vm.Session.SetSelection([shapeNode.Id]);
            vm.SelectedShapeFillColor = Colors.Teal;
            vm.SelectedShapeOpacityPercent = 65.0;
            vm.ApplySelectedShapeStyleCommand.Execute(null);

            ShapeNode updatedShape = Assert.IsType<ShapeNode>(vm.ActivePage.Nodes.First(n => n.Id == shapeNode.Id));
            SolidShapeFill solidFill = Assert.IsType<SolidShapeFill>(updatedShape.Fill);
            Assert.Equal(Colors.Teal.G, solidFill.Color.G);
            Assert.Equal(0.65, updatedShape.Opacity, 2);

            // Paper geometry update (A5)
            vm.PageWidthInputMm = 148.0;
            vm.PageHeightInputMm = 210.0;
            vm.PageBleedInputMm = 3.0;
            vm.ApplyPageGeometryCommand.Execute(null);
            Assert.Equal(148.0, vm.ActivePage.WidthMm);
            Assert.Equal(210.0, vm.ActivePage.HeightMm);
            Assert.Equal(3.0, vm.ActivePage.BleedMm);

            window.Close();
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
