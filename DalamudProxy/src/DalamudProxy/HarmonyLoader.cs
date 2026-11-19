using System;
using System.IO;
using System.Runtime.Loader;

namespace DalamudProxy;

internal static class HarmonyLoader
{
    private const string HarmonyAssemblyName = "0Harmony";

    private static bool ensured;

    public static void EnsureLoaded(string pluginDirectory)
    {
        if (ensured)
            return;

        foreach (var assembly in AssemblyLoadContext.Default.Assemblies)
        {
            if (string.Equals(assembly.GetName().Name, HarmonyAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                ensured = true;
                return;
            }
        }

        var path = Path.Combine(pluginDirectory, "libs", HarmonyAssemblyName + ".dll");
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到随插件发布的 {HarmonyAssemblyName}.dll：{path}");

        AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        ensured = true;
    }
}
