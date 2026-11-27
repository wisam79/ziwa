using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
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
using Ziwa.App.Views.Dialogs;
using Ziwa.App.Views.Inspector;
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

            InspectorUi.Layout(window);
            Assert.NotNull(window.FindControl<WindowNotificationManager>("ToastNotificationManager"));
            var inspector = InspectorUi.Visible<InspectorPanelView>(window);
            var tabs = InspectorUi.Visible<TabControl>(inspector, "InspectorTabs");

            // Toast notifications fire cleanly without throwing
            vm.ShowToast("اختبار", "إشعار تجريبي", NotificationType.Success);

            // Collage grid customization (3x4 with 2mm gap) while project is clean
            await vm.StartIdPhotoSheetCommand.ExecuteAsync(null);
            Assert.True(vm.IsCollageMode);
            InspectorUi.Layout(window);
            InspectorUi.Click(window, InspectorUi.Visible<TabItem>(tabs, "PageTab"));
            var page = InspectorUi.Visible<InspectorPageView>(tabs);
            vm.CollageRowsInput = 3;
            vm.CollageColumnsInput = 4;
            vm.CollageGapInputMm = 2.0;
            InspectorUi.Click(window, InspectorUi.Visible<Button>(page, "ApplyCollageGridButton"));
            Assert.Equal(12, vm.ActivePage.CollageLayout!.Slots.Length);

            // Text color & font style update via Inspector ColorPicker
            InspectorUi.Click(window, InspectorUi.Visible<TabItem>(tabs, "PropertiesTab"));
            await vm.AddHeadingTextCommand.ExecuteAsync(null);
            TextNode textNode = Assert.IsType<TextNode>(vm.ActivePage.Nodes.Last(n => n is TextNode));
            vm.Session.SetSelection([textNode.Id]);
            InspectorUi.Layout(window);
            var properties = InspectorUi.Visible<InspectorPropertiesView>(tabs);
            var textColor = InspectorUi.Visible<ColorPicker>(properties, "TextColorPicker");
            textColor.SetCurrentValue(ColorPicker.ColorProperty, Colors.Crimson);
            vm.SelectedTextFontSizePt = 36.0;
            vm.ApplySelectedTextStyleCommand.Execute(null);

            TextNode updatedText = Assert.IsType<TextNode>(vm.ActivePage.Nodes.First(n => n.Id == textNode.Id));
            Assert.Equal(Colors.Crimson.R, updatedText.Color.R);
            Assert.Equal(36.0, updatedText.FontSizePt);

            // Shape fill color & opacity update via Inspector ColorPicker
            await vm.AddStarShapeCommand.ExecuteAsync(null);
            ShapeNode shapeNode = Assert.IsType<ShapeNode>(vm.ActivePage.Nodes.Last(n => n is ShapeNode));
            vm.Session.SetSelection([shapeNode.Id]);
            InspectorUi.Layout(window);
            InspectorUi.Visible<ColorPicker>(properties, "ShapeFillColorPicker").SetCurrentValue(ColorPicker.ColorProperty, Colors.Teal);
            vm.SelectedShapeOpacityPercent = 65.0;
            vm.ApplySelectedShapeStyleCommand.Execute(null);

            ShapeNode updatedShape = Assert.IsType<ShapeNode>(vm.ActivePage.Nodes.First(n => n.Id == shapeNode.Id));
            SolidShapeFill solidFill = Assert.IsType<SolidShapeFill>(updatedShape.Fill);
            Assert.Equal(Colors.Teal.G, solidFill.Color.G);
            Assert.Equal(0.65, updatedShape.Opacity, 2);

            await vm.InsertBrandLogoCommand.ExecuteAsync(null);
            SvgNode logo = vm.ActivePage.Nodes.OfType<SvgNode>().Last();
            vm.Session.SetSelection([logo.Id]);
            InspectorUi.Layout(window);
            InspectorUi.Visible<ColorPicker>(properties, "SvgTintColorPicker").SetCurrentValue(ColorPicker.ColorProperty, Colors.Gold);
            Assert.Equal(Colors.Gold.R, vm.ActivePage.Nodes.OfType<SvgNode>().Single(n => n.Id == logo.Id).TintColor!.Value.R);

            // Paper geometry update (A5)
            InspectorUi.Click(window, InspectorUi.Visible<TabItem>(tabs, "PageTab"));
            page = InspectorUi.Visible<InspectorPageView>(tabs);
            vm.PageWidthInputMm = 148.0;
            vm.PageHeightInputMm = 210.0;
            vm.PageBleedInputMm = 3.0;
            InspectorUi.Click(window, InspectorUi.Visible<Button>(page, "ApplyPageGeometryButton"));
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

    [AvaloniaFact]
    public void StudioPolish_FloatingInspectorZenButtonsScrimCloseAndPaletteEnter_WorkEndToEnd()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 892 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Button reopenBtn = Assert.IsType<Button>(window.FindControl<Button>("ReopenInspectorFloatingButton"));
            Button exitZenBtn = Assert.IsType<Button>(window.FindControl<Button>("ExitZenModeFloatingButton"));

            Assert.False(reopenBtn.IsVisible);
            Assert.False(exitZenBtn.IsVisible);

            // Closing inspector reveals floating reopen chip; clicking it restores inspector
            vm.CloseInspectorCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(vm.CanShowReopenInspectorButton);
            Assert.True(reopenBtn.IsVisible);

            ClickCenter(window, reopenBtn);
            Assert.True(vm.IsInspectorOpen);
            Assert.False(reopenBtn.IsVisible);

            // Entering Zen Mode hides reopen chip and shows exit-zen floating pill
            vm.ToggleZenModeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(vm.IsZenMode);
            Assert.True(exitZenBtn.IsVisible);
            Assert.False(reopenBtn.IsVisible);

            ClickCenter(window, exitZenBtn);
            Assert.False(vm.IsZenMode);
            Assert.False(exitZenBtn.IsVisible);

            // CommandPalette Enter key executes top filtered command
            vm.ToggleCommandPaletteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            CommandPaletteView palette = Assert.Single(window.GetVisualDescendants().OfType<CommandPaletteView>());
            TextBox searchBox = Assert.IsType<TextBox>(palette.FindControl<TextBox>("CommandSearchTextBox"));
            vm.CommandSearchQuery = "صور هوية";
            Dispatcher.UIThread.RunJobs();
            Assert.True(searchBox.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsCommandPaletteOpen);
            Assert.True(vm.IsCollageMode);

            // Clicking dialog scrim closes modal StudioDialogShell
            vm.OpenPrintDialogCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(vm.IsPrintDialogOpen);
            PrintExportDialogView printView = Assert.Single(window.GetVisualDescendants().OfType<PrintExportDialogView>());
            StudioDialogShell shell = Assert.Single(printView.GetVisualDescendants().OfType<StudioDialogShell>());
            Border scrim = Assert.Single(shell.GetVisualDescendants().OfType<Border>(), b => b.Name == "PART_Scrim");
            Point scrimStart = scrim.TranslatePoint(default, window)!.Value;
            Point scrimEnd = scrim.TranslatePoint(new Point(scrim.Bounds.Width, scrim.Bounds.Height), window)!.Value;
            Point corner = new Rect(scrimStart, scrimEnd).Normalize().TopLeft + new Vector(8, 8);
            window.MouseDown(corner, MouseButton.Left);
            window.MouseUp(corner, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsPrintDialogOpen);
        }
        finally
        {
            window.Close();
        }
    }

    private static void ClickCenter(Window window, Control control)
    {
        Point start = control.TranslatePoint(default, window)!.Value;
        Point end = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window)!.Value;
        Point center = new Rect(start, end).Normalize().Center;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
