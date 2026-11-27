using System.Xml.Linq;
using Xunit;

namespace Ziwa.Domain.Tests;

public sealed class LayerDependencyTests
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedProjectReferences =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ziwa.Domain"] = [],
            ["Ziwa.Rendering"] = ["Ziwa.Domain"],
            ["Ziwa.Application"] = ["Ziwa.Domain"],
            ["Ziwa.Infrastructure"] = ["Ziwa.Domain", "Ziwa.Application", "Ziwa.Rendering"],
            ["Ziwa.Platform.Windows"] = ["Ziwa.Domain", "Ziwa.Application"],
            ["Ziwa.App"] = ["Ziwa.Domain", "Ziwa.Rendering", "Ziwa.Application", "Ziwa.Infrastructure", "Ziwa.Platform.Windows"],
        };

    [Fact]
    public void SourceProjects_EnforceStrictOneWayLayerBoundaries()
    {
        string repoRoot = LocateRepoRoot();
        string srcDir = Path.Combine(repoRoot, "src");
        string[] csprojFiles = Directory.GetFiles(srcDir, "*.csproj", SearchOption.AllDirectories);

        Assert.Equal(AllowedProjectReferences.Count, csprojFiles.Length);

        foreach (string csproj in csprojFiles)
        {
            string projectName = Path.GetFileNameWithoutExtension(csproj);
            Assert.True(AllowedProjectReferences.TryGetValue(projectName, out string[]? allowedRefs), $"Unexpected project '{projectName}'.");

            XDocument doc = XDocument.Load(csproj);
            string[] actualRefs = doc.Descendants("ProjectReference")
                .Select(e => Path.GetFileNameWithoutExtension(
                    (e.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();

            foreach (string actualRef in actualRefs)
            {
                Assert.Contains(actualRef, allowedRefs);
            }

            string[] packages = doc.Descendants("PackageReference")
                .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
                .ToArray();

            if (projectName == "Ziwa.Domain")
            {
                Assert.Empty(actualRefs);
                Assert.Empty(packages);
            }

            if (projectName != "Ziwa.App")
            {
                Assert.DoesNotContain(packages, p => p.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                string? compiledBindings = doc.Descendants("AvaloniaUseCompiledBindingsByDefault").FirstOrDefault()?.Value;
                Assert.Equal("true", compiledBindings);
            }

            if (projectName == "Ziwa.Platform.Windows")
            {
                string? tfm = doc.Descendants("TargetFramework").FirstOrDefault()?.Value;
                Assert.Equal("net10.0-windows", tfm);
            }
        }
    }

    [Fact]
    public void DomainAndNonUiSources_ContainNoForbiddenNamespaceImports()
    {
        string repoRoot = LocateRepoRoot();

        CheckNoImports(Path.Combine(repoRoot, "src", "Ziwa.Domain"), ["using Avalonia", "using System.Net.Http", "using System.IO.File"]);
        CheckNoImports(Path.Combine(repoRoot, "src", "Ziwa.Rendering"), ["using Avalonia", "using System.Net.Http", "File.ReadAll", "File.WriteAll"]);
        CheckNoImports(Path.Combine(repoRoot, "src", "Ziwa.Application"), ["using Avalonia", "using System.Net.Http", "File.ReadAll", "File.WriteAll"]);
    }

    private static void CheckNoImports(string directory, string[] forbiddenTokens)
    {
        foreach (string file in Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            string content = File.ReadAllText(file);
            foreach (string token in forbiddenTokens)
            {
                Assert.False(
                    content.Contains(token, StringComparison.Ordinal),
                    $"Forbidden token '{token}' found in '{file}'.");
            }
        }
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

        throw new DirectoryNotFoundException("Could not locate Ziwa.sln from test base directory.");
    }
}
