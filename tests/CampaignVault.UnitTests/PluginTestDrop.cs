using System;
using System.IO;
using System.Linq;

namespace CampaignVault.Tests;

internal static class PluginTestDrop
{
    public static string BuiltCraftingDll
    {
        get
        {
            var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var candidates = new[]
            {
                Path.Combine(repo, "plugins", "CraftingMode", "bin", "Debug", "net10.0", "CraftingMode.dll"),
                Path.Combine(repo, "plugins", "CraftingMode", "bin", "Release", "net10.0", "CraftingMode.dll"),
            };
            return candidates.First(File.Exists);
        }
    }

    public static string InstallCrafting(string pluginsRoot)
    {
        var dest = Path.Combine(pluginsRoot, "CraftingMode");
        Directory.CreateDirectory(dest);
        var srcDir = Path.GetDirectoryName(BuiltCraftingDll)!;
        foreach (var file in Directory.GetFiles(srcDir))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, "CampaignVault.PluginSdk.dll", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(file, Path.Combine(dest, name), overwrite: true);
            }
        }

        return dest;
    }
}
