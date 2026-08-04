using System.Reflection;

namespace CampaignVault.Data.Templates;

/// <summary>
/// Discovers ruleset systems from both embedded resources and disk directories.
/// Enables data-only plugins by scanning dynamically instead of hardcoding a fixed system list.
/// </summary>
internal static class RulesetDataSystemDiscovery
{
    /// <summary>
    /// Discover all available systems by scanning embedded resources and disk directories.
    /// Returns (systemSlug, matchedSubfolder) pairs for each discovered system.
    /// </summary>
    public static IEnumerable<(string systemSlug, string subfolder)> Discover(
        string rulesetDataDirectory,
        Assembly embeddedAssembly,
        string[] subfolderCandidates)
    {
        var discovered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. Discover from embedded resources: CampaignVault.RulesetData.<system>.<subfolder>.*
        var embeddedPrefix = "CampaignVault.RulesetData.";
        foreach (var resourceName in embeddedAssembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(embeddedPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var afterPrefix = resourceName.Substring(embeddedPrefix.Length);
            var parts = afterPrefix.Split('.');
            if (parts.Length < 2)
                continue;

            var systemSlug = parts[0];
            var subfolder = parts[1];

            // Only consider this system/subfolder if it matches one of our candidates
            if (!subfolderCandidates.Contains(subfolder, StringComparer.OrdinalIgnoreCase))
                continue;

            // Store with case-insensitive key, case-preserving value
            discovered[systemSlug] = subfolder;
        }

        // 2. Discover from disk: scan for system directories and check for matching subfolders
        if (Directory.Exists(rulesetDataDirectory))
        {
            foreach (var systemDir in Directory.EnumerateDirectories(rulesetDataDirectory))
            {
                var systemSlug = Path.GetFileName(systemDir);

                // Check which subfolder candidates exist in this system directory
                foreach (var candidate in subfolderCandidates)
                {
                    var fullPath = Path.Combine(systemDir, candidate);
                    if (Directory.Exists(fullPath))
                    {
                        discovered[systemSlug] = candidate;
                        break; // Take first matching candidate
                    }
                }
            }
        }

        // 3. Return deduplicated results (case-insensitive keys, case-preserving values)
        return discovered
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => (x.Key, x.Value));
    }
}
