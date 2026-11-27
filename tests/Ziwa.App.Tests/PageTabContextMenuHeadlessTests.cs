using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.ViewModels;
using Ziwa.App.Views.Workspace;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.App.Tests;

public sealed class PageTabContextMenuHeadlessTests
{
    private const string Duplicate = "تكرار الصفحة";
    private const string Earlier = "تقديم الصفحة";
    private const string Later = "تأخير الصفحة";
    private const string Delete = "حذف الصفحة";

    [AvaloniaTheory]
    [InlineData(Duplicate, 0)]
    [InlineData(Earlier, 2)]
    [InlineData(Later, 0)]
    [InlineData(Delete, 0)]
    public void RightClickInactiveTab_OperatesOnThatPageInOneUndoStep(string header, int targetIndex)
    {
        WithStrip((window, vm) =>
        {
            // Equal labels, distinct identities and contents; the middle page stays active when opening the menu.
            PageModel first = vm.ActivePage with { Name = "صفحة", Nodes = [], CollageLayout = null };
            PageModel middle = first with { Id = PageId.New(), WidthMm = first.WidthMm + 10 };
            PageModel last = first with { Id = PageId.New(), WidthMm = first.WidthMm + 20 };
            ZiwaDocument before = vm.Document with { Pages = [first, middle, last], ActivePageIndex = 1 };
            vm.Session.ReplaceDocument(before);
            InspectorUi.Layout(window);

            ContextMenu menu = RightClickTab(window, before.Pages[targetIndex].Id);
            Assert.Same(before, vm.Document);
            Assert.Equal(0, vm.Session.UndoCount);
            Assert.Equal(targetIndex > 0, Item(menu, Earlier).IsEffectivelyEnabled);
            Assert.Equal(targetIndex < before.Pages.Length - 1, Item(menu, Later).IsEffectivelyEnabled);
            Assert.True(Item(menu, Delete).IsEffectivelyEnabled);

            MenuItem action = Item(menu, header);
            Assert.True(action.IsEffectivelyEnabled);
            for (int i = 0; i < menu.Items.Count && !action.IsFocused; i++)
            {
                Press(window, PhysicalKey.ArrowDown);
            }

            Assert.True(action.IsFocused);
            Press(window, PhysicalKey.Enter);
            Assert.False(menu.IsOpen);
            Assert.Equal(1, vm.Session.UndoCount);

            PageId[] expectedOrder = header switch
            {
                Duplicate => [first.Id, vm.ActivePage.Id, middle.Id, last.Id],
                Earlier => [first.Id, last.Id, middle.Id],
                Later => [middle.Id, first.Id, last.Id],
                Delete => [middle.Id, last.Id],
                _ => throw new ArgumentOutOfRangeException(nameof(header)),
            };
            Assert.Equal(expectedOrder, vm.Document.Pages.Select(p => p.Id));
            if (header == Duplicate)
            {
                Assert.DoesNotContain(before.Pages, p => p.Id == vm.ActivePage.Id);
                Assert.Equal(first.WidthMm, vm.ActivePage.WidthMm);
            }
            else
            {
                Assert.Equal(middle.Id, vm.ActivePage.Id);
            }

            Assert.True(vm.Session.Undo());
            Assert.Same(before, vm.Document);
            Assert.Equal(0, vm.Session.UndoCount);
        });
    }

    [AvaloniaFact]
    public void OnlyPageMenu_DisablesBothMovesAndDeletionButAllowsDuplication()
    {
        WithStrip((window, vm) =>
        {
            Assert.Single(vm.Document.Pages);
            ContextMenu menu = RightClickTab(window, vm.ActivePage.Id);
            Assert.True(Item(menu, Duplicate).IsEffectivelyEnabled);
            Assert.False(Item(menu, Earlier).IsEffectivelyEnabled);
            Assert.False(Item(menu, Later).IsEffectivelyEnabled);
            Assert.False(Item(menu, Delete).IsEffectivelyEnabled);
            menu.Close();
            Assert.Equal(0, vm.Session.UndoCount);
        });
    }

    private static ContextMenu RightClickTab(Window window, PageId id)
    {
        Button tab = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
            button => button.DataContext is PageTabItemViewModel row && row.Id == id && button.Classes.Contains("pageTab"));
        Point center = InspectorUi.BoundsIn(tab, window).Center;
        window.MouseMove(center);
        window.MouseDown(center, MouseButton.Right);
        window.MouseUp(center, MouseButton.Right);
        InspectorUi.Layout(window);
        var menu = Assert.IsType<ContextMenu>(tab.ContextMenu);
        Assert.True(menu.IsOpen);
        return menu;
    }

    private static MenuItem Item(ContextMenu menu, string header) =>
        Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(header, item.Header));

    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
        InspectorUi.Layout(window);
    }

    private static void WithStrip(Action<Window, MainWindowViewModel> verify)
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("ziwa-page-menu-");
        try
        {
            using ServiceProvider services = Bootstrapper.BuildServiceProvider(new ProjectStorageOptions(root.FullName));
            var vm = services.GetRequiredService<MainWindowViewModel>();
            var window = new Window
            {
                DataContext = vm,
                Content = new PageStripView(),
                FlowDirection = FlowDirection.RightToLeft,
                Width = 1120,
                Height = 160,
            };
            try
            {
                window.Show();
                InspectorUi.Layout(window);
                verify(window, vm);
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
}
