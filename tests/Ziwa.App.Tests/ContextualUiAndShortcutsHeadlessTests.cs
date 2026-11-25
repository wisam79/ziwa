using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Domain.Models;

namespace Ziwa.App.Tests;

public sealed class ContextualUiAndShortcutsHeadlessTests
{
    [AvaloniaFact]
    public void ContextualInspector_AutoOpensOnSelectionAndRestoresPreviousClosedState()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Assert.True(vm.EffectiveInspectorVisible);

        vm.CloseInspectorCommand.Execute(null);
        Assert.False(vm.EffectiveInspectorVisible);

        // Selecting a node automatically opens Inspector
        SceneNode firstNode = vm.ActivePage.Nodes[0];
        vm.Session.SetSelection([firstNode.Id]);
        Assert.True(vm.EffectiveInspectorVisible);
        Assert.True(vm.IsQuickBarVisible);

        // Clearing selection automatically closes Inspector back to its prior state
        vm.Session.ClearSelection();
        Assert.False(vm.EffectiveInspectorVisible);
        Assert.False(vm.IsQuickBarVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void StickerPriorityAndInPlaceReEdit_HidesPhotoToolsAndSupportsUndo()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.StartStickerSheetCommand.Execute(null);
        StickerNode sticker = Assert.IsType<StickerNode>(vm.ActivePage.Nodes[0]);
        vm.Session.SetSelection([sticker.Id]);

        Assert.True(vm.IsStickerSelected);
        Assert.False(vm.IsPhotoToolsVisible);

        // Non-destructive re-edit in place
        vm.EditSelectedStickerCommand.Execute(null);
        Assert.True(vm.IsStickerStudioOpen);
        vm.StickerFieldItems[0].Value = "استوديو محدث";
        vm.ApplyStickerStudioCommand.Execute(null);
        Assert.False(vm.IsStickerStudioOpen);

        StickerNode updated = Assert.IsType<StickerNode>(vm.ActivePage.Nodes[0]);
        Assert.Equal("استوديو محدث", updated.Fields[vm.StickerFieldItems[0].Key]);

        vm.UndoCommand.Execute(null);
        StickerNode undone = Assert.IsType<StickerNode>(vm.ActivePage.Nodes[0]);
        Assert.Equal(sticker.Fields[vm.StickerFieldItems[0].Key], undone.Fields[vm.StickerFieldItems[0].Key]);

        window.Close();
    }

    [AvaloniaFact]
    public void CommandPaletteAndZenMode_AndQuickStartIdSheet_WorkEndToEnd()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        // Ctrl+K Command Palette search & execute
        vm.ToggleCommandPaletteCommand.Execute(null);
        Assert.True(vm.IsCommandPaletteOpen);

        vm.CommandSearchQuery = "صور هوية";
        Assert.NotEmpty(vm.FilteredCommands);
        vm.ExecutePaletteCommandCommand.Execute(vm.FilteredCommands[0]);

        Assert.False(vm.IsCommandPaletteOpen);
        Assert.True(vm.IsCollageMode);
        Assert.NotNull(vm.ActivePage.CollageLayout);

        // Zen Mode hides chrome and inspector cleanly, then restores
        vm.ToggleZenModeCommand.Execute(null);
        Assert.True(vm.IsZenMode);
        Assert.False(vm.IsChromeVisible);
        Assert.False(vm.EffectiveInspectorVisible);

        vm.ToggleZenModeCommand.Execute(null);
        Assert.False(vm.IsZenMode);
        Assert.True(vm.IsChromeVisible);

        window.Close();
    }
}
