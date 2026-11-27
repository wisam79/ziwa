using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Stickers;
using Ziwa.Domain.Validation;

namespace Ziwa.App.Tests;

public sealed class Joint5StickerAndCurvedTextHeadlessTests
{
    [AvaloniaFact]
    public async Task StickerStudioLivePreviewTemplateSwitchCustomPrimaryAndOvalStamp_WorkEndToEnd()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Open Sticker Studio -> renders live Skia preview bitmap
            await vm.OpenStickerStudioCommand.ExecuteAsync(null);
            Assert.True(vm.IsStickerStudioOpen);
            Assert.NotNull(vm.StickerPreviewBitmap);
            Assert.True(vm.StickerPreviewBitmap!.Width > 0);

            // Switch to new OvalOfficialStamp template and pick a custom brand primary color
            StickerTemplateDefinition ovalTemplate = vm.StickerTemplates.First(t => t.Id == "oval-official-stamp");
            vm.SelectedStickerTemplate = ovalTemplate;
            Assert.Equal("oval-official-stamp", vm.SelectedStickerTemplate.Id);
            Assert.Equal(3, vm.StickerFieldItems.Count);

            vm.StickerCustomPrimaryColor = Colors.DarkGreen;
            Assert.Equal(Colors.DarkGreen.G, vm.CurrentStickerPalette.Primary.G);
            double primaryContrast = OklchPaletteGenerator.ComputeContrastRatio(
                vm.CurrentStickerPalette.TextOnPrimary,
                vm.CurrentStickerPalette.Primary);
            Assert.True(primaryContrast >= 4.5);

            vm.StickerFieldItems[0].Value = "مختبر الدقة للوثائق";
            vm.ApplyStickerStudioCommand.Execute(null);
            Assert.False(vm.IsStickerStudioOpen);

            StickerNode created = Assert.IsType<StickerNode>(vm.ActivePage.Nodes.Last(n => n is StickerNode));
            Assert.Equal("oval-official-stamp", created.TemplateId);
            Assert.Equal("مختبر الدقة للوثائق", created.Fields["org"]);
            Assert.Equal("ختم رسمي بيضاوي", vm.SelectedStickerTemplateTitle);

            // Re-editing an existing sticker allows switching its template cleanly (bug fix)
            vm.EditSelectedStickerCommand.Execute(null);
            Assert.True(vm.IsStickerStudioOpen);
            vm.SelectedStickerTemplate = ParametricStickerCatalog.WarrantyBadge;
            Assert.Contains(vm.StickerFieldItems, f => f.Key == "warranty" || f.Key == "years");
            vm.ApplyStickerStudioCommand.Execute(null);

            StickerNode switched = Assert.IsType<StickerNode>(vm.ActivePage.Nodes.First(n => n.Id == created.Id));
            Assert.Equal("warranty-badge", switched.TemplateId);
            Assert.True(SchemaValidator.ValidateDocument(vm.Document).IsValid);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CurvedTextControlsTypographyPresetsBoldSpacingAndEffects_WorkWithUndo()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Insert Capsule Badge & Watermark presets via commands
            await vm.AddCapsuleBadgeCommand.ExecuteAsync(null);
            TextNode capsule = Assert.IsType<TextNode>(vm.ActivePage.Nodes.Last(n => n is TextNode));
            Assert.NotNull(capsule.EffectiveEffects.PillBackgroundColor);

            await vm.AddWatermarkTextCommand.ExecuteAsync(null);
            TextNode watermark = Assert.IsType<TextNode>(vm.ActivePage.Nodes.Last(n => n is TextNode));
            Assert.True(watermark.Opacity < 0.5);

            // Select heading and convert to ArcUp curve + Bend + Bold + LetterSpacing + Outline/Pill/Shadow
            await vm.AddHeadingTextCommand.ExecuteAsync(null);
            TextNode heading = Assert.IsType<TextNode>(vm.ActivePage.Nodes.Last(n => n is TextNode));
            vm.Session.SetSelection([heading.Id]);

            vm.SelectedTextCurveOption = vm.AvailableTextCurveOptions.First(o => o.Kind == TextCurveKind.ArcUp);
            vm.SelectedTextCurveBend = 0.85;
            vm.SelectedTextBold = true;
            vm.SelectedTextLetterSpacingEm = 0.12;
            vm.SelectedTextHasOutline = true;
            vm.SelectedTextOutlineWidthMm = 0.6;
            vm.SelectedTextOutlineColor = Colors.Yellow;
            vm.SelectedTextHasShadow = true;
            vm.ApplySelectedTextEffectsCommand.Execute(null);

            TextNode styled = Assert.IsType<TextNode>(vm.ActivePage.Nodes.First(n => n.Id == heading.Id));
            Assert.NotNull(styled.Curve);
            Assert.Equal(TextCurveKind.ArcUp, styled.Curve!.Kind);
            Assert.Equal(0.85, styled.Curve.BendFactor, 2);
            Assert.Equal(700, styled.FontWeight);
            Assert.Equal(0.12, styled.LetterSpacingEm, 2);
            Assert.NotNull(styled.EffectiveEffects.Outline);
            Assert.Equal(0.6, styled.EffectiveEffects.Outline!.WidthMm, 2);
            Assert.NotNull(styled.EffectiveEffects.Shadow);
            Assert.True(SchemaValidator.ValidateDocument(vm.Document).IsValid);

            // Flatten curve back to None
            vm.SelectedTextCurveOption = vm.AvailableTextCurveOptions.First(o => o.Kind == TextCurveKind.None);
            TextNode flattened = Assert.IsType<TextNode>(vm.ActivePage.Nodes.First(n => n.Id == heading.Id));
            Assert.Null(flattened.Curve);
        }
        finally
        {
            window.Close();
        }
    }
}
