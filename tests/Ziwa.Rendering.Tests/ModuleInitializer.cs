using System.Runtime.CompilerServices;
using DiffEngine;

namespace Ziwa.Rendering.Tests;

internal static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        DiffRunner.Disabled = true;
    }
}
