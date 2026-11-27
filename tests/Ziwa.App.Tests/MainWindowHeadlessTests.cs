using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.Controls;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.App.Views.Workspace;

namespace Ziwa.App.Tests;

public sealed partial class MainWindowHeadlessTests
{
    [AvaloniaFact]
    public void MainWindow_EnforcesRtlAndFiveRegionsAndCompiledBindings()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };

        window.Show();

        Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
        Assert.NotNull(window.FindControl<CommandBarView>("CommandBarRegion"));
        Assert.NotNull(window.FindControl<WorkspaceRailView>("WorkspaceRailRegion"));
        Assert.NotNull(window.FindControl<Grid>("CanvasWorkspaceRegion"));
        Assert.NotNull(window.FindControl<Border>("InspectorRegion"));
        Assert.NotNull(window.FindControl<PageStripView>("PageStripRegion"));
        Assert.NotNull(window.FindControl<WorkspaceStatusBarView>("StatusBarRegion"));

        SkiaViewportHost? host = window.FindControl<SkiaViewportHost>("ViewportHost");
        Assert.NotNull(host);
        Assert.Same(vm.ActivePage, host!.Page);
        Assert.Same(vm.SceneRenderer, host.SceneRenderer);
        Assert.Same(vm.Session, host.Session);

        Assert.NotNull(window.FindControl<CanvasRulerControl>("HorizontalRuler"));
        Assert.NotNull(window.FindControl<CanvasRulerControl>("VerticalRuler"));

        int initialNodeCount = vm.ActivePage.Nodes.Length;
        vm.AddStarShapeCommand.Execute(null);
        Assert.Equal(initialNodeCount + 1, vm.ActivePage.Nodes.Length);
        Assert.True(vm.CanUndo);

        vm.UndoCommand.Execute(null);
        Assert.Equal(initialNodeCount, vm.ActivePage.Nodes.Length);
        Assert.True(vm.CanRedo);

        List<Button> buttons = window.GetVisualDescendants().OfType<Button>().ToList();
        Assert.NotEmpty(buttons);
        foreach (Button btn in buttons)
        {
            string? autoName = AutomationProperties.GetName(btn);
            object? tip = ToolTip.GetTip(btn);
            Assert.False(string.IsNullOrWhiteSpace(autoName), $"Button '{btn.Name}' is missing AutomationProperties.Name.");
            Assert.True(tip is not null, $"Button '{btn.GetType().Name}:{btn.Name}' (TemplatedParent={btn.TemplatedParent?.GetType().Name}) is missing ToolTip.Tip.");
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Workspace_UsesEmbeddedIbmPlexSansArabic()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        var window = new MainWindow { DataContext = sp.GetRequiredService<MainWindowViewModel>() };
        try
        {
            window.Show();
            FontFamily family = window.FontFamily;
            Assert.Equal("IBM Plex Sans Arabic", family.Name);
            Assert.NotNull(family.Key);
            foreach (FontWeight weight in new[] { FontWeight.Normal, FontWeight.Bold })
            {
                var face = new Typeface(family, weight: weight).GlyphTypeface;
                Assert.Equal(family.Name, face.FamilyName);
                Assert.NotEqual((ushort)0, face.GetGlyph('م'));
            }

            var toolbar = window.FindControl<CommandBarView>("CommandBarRegion")!;
            Assert.Equal(family, toolbar.FindControl<Button>("ProjectFileMenuButton")!.FontFamily);
            Assert.Equal(family, toolbar.FindControl<TextBlock>("DocumentTitleLabel")!.FontFamily);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ThemeToggle_UpdatesResolvedSurfaceAndTextBrushesWithoutErrors()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var app = global::Avalonia.Application.Current!;
        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);

        Color darkSurface = Assert.IsType<SolidColorBrush>(window.Background).Color;

        vm.ToggleThemeCommand.Execute(null);
        Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
        Color lightSurface = Assert.IsType<SolidColorBrush>(window.Background).Color;

        Assert.NotEqual(darkSurface, lightSurface);

        // Reset back to Dark for subsequent tests
        vm.ToggleThemeCommand.Execute(null);
        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);

        window.Close();
    }

    [Fact]
    public void AxamlViews_ContainZeroRawHexColorsOutsideTokens()
    {
        string repoRoot = LocateRepoRoot();
        string appDir = Path.Combine(repoRoot, "src", "Ziwa.App");
        string[] axamlFiles = Directory.GetFiles(appDir, "*.axaml", SearchOption.AllDirectories);

        foreach (string file in axamlFiles)
        {
            if (Path.GetFileName(file).Equals("Tokens.axaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string content = File.ReadAllText(file);
            Assert.False(HexColorRegex().IsMatch(content), $"Raw hex color found in '{file}'.");
        }
    }

    [Fact]
    public void TokensAxaml_MeetsWcagContrastRatioAcrossLightAndDarkThemes()
    {
        string repoRoot = LocateRepoRoot();
        string tokensPath = Path.Combine(repoRoot, "src", "Ziwa.App", "Themes", "Tokens.axaml");
        XDocument doc = XDocument.Load(tokensPath);
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement? themeDicts = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries");
        Assert.NotNull(themeDicts);

        foreach (string themeKey in new[] { "Light", "Dark" })
        {
            XElement? dict = themeDicts!.Elements().FirstOrDefault(e => e.Attribute(xNs + "Key")?.Value == themeKey);
            Assert.NotNull(dict);

            Dictionary<string, Color> colors = dict!.Elements()
                .Where(e => e.Name.LocalName == "Color")
                .ToDictionary(
                    e => e.Attribute(xNs + "Key")!.Value,
                    e => Color.Parse(e.Value));

            AssertContrastAtLeast(colors["ZiwaPrimaryTextColor"], colors["ZiwaSurfaceColor"], 4.5, $"{themeKey} PrimaryText/Surface");
            AssertContrastAtLeast(colors["ZiwaPrimaryTextColor"], colors["ZiwaCardColor"], 4.5, $"{themeKey} PrimaryText/Card");
            AssertContrastAtLeast(colors["ZiwaSecondaryTextColor"], colors["ZiwaSurfaceColor"], 4.5, $"{themeKey} SecondaryText/Surface");
            AssertContrastAtLeast(colors["ZiwaOnPrimaryActionColor"], colors["ZiwaPrimaryActionColor"], 4.5, $"{themeKey} OnPrimaryAction/PrimaryAction");
            AssertContrastAtLeast(colors["ZiwaDestructiveActionColor"], colors["ZiwaSurfaceColor"], 4.5, $"{themeKey} Destructive/Surface");
            AssertContrastAtLeast(colors["ZiwaSuccessColor"], colors["ZiwaSurfaceColor"], 4.5, $"{themeKey} Success/Surface");
            AssertContrastAtLeast(colors["ZiwaWarningColor"], colors["ZiwaSurfaceColor"], 4.5, $"{themeKey} Warning/Surface");
            AssertContrastAtLeast(colors["ZiwaInfoColor"], colors["ZiwaSurfaceColor"], 4.5, $"{themeKey} Info/Surface");
        }
    }

    private static void AssertContrastAtLeast(Color fg, Color bg, double minRatio, string pairName)
    {
        double l1 = RelativeLuminance(fg);
        double l2 = RelativeLuminance(bg);
        double ratio = (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        Assert.True(ratio >= minRatio, $"Contrast for {pairName} was {ratio:0.00}:1 (< {minRatio}:1).");
    }

    private static double RelativeLuminance(Color c)
    {
        static double Linearize(byte channel)
        {
            double s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(c.R)) + (0.7152 * Linearize(c.G)) + (0.0722 * Linearize(c.B));
    }

    private static string LocateRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ziwa.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Ziwa.sln.");
    }

    [GeneratedRegex("#[0-9A-Fa-f]{3,8}")]
    private static partial Regex HexColorRegex();
}
