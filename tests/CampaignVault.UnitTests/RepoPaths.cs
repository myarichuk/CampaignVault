using System;
using System.IO;
namespace CampaignVault.Tests;

/// <summary>Repository root for tests that read checked-in guidance (skills, prompts).</summary>
internal static class RepoPaths
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "CampaignVault.slnx"))
                || File.Exists(Path.Combine(dir, "src", "CampaignVault", "CampaignVault.csproj")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
