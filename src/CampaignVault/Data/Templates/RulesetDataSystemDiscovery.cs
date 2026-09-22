using System.Reflection;

namespace CampaignVault.Data.Templates;

/// <summary>
/// Discovers ruleset systems from both embedded resources and disk directories.
/// Enables data-only plugins by scanning dynamically instead of hardcoding a fixed system list.
/// Additional plugin RulesetData roots are returned alongside the primary root so providers can
/// merge templates (plugin last-wins on duplicate <c>name:</c>).
/// </summary>
internal static class RulesetDataSystemDiscovery
{
    /// <summary>
    /// Discover available (systemSlug, subfolder, diskRoot) triples. May return multiple roots for
    /// the same system when plugins contribute the same subfolder — callers must merge loads.
    /// Order: embedded/primary seed first, then primary disk, then each additional plugin root.
    /// </summary>
    public static IEnumerable<(string systemSlug, string subfolder, string diskRoot)> Discover(
        string rulesetDataDirectory,
        Assembly embeddedAssembly,
        string[] subfolderCandidates,
        IEnumerable<string>? additionalRulesetDataRoots = null)
    {
        // Preserve insertion order; allow duplicate systemSlugs with different diskRoots.
        var results = new List<(string systemSlug, string subfolder, string diskRoot)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // key: system|root|subfolder

        void Add(string systemSlug, string subfolder, string diskRoot)
        {
            var key = $"{systemSlug}|{diskRoot}|{subfolder}";
            if (!seen.Add(key))
                return;
            results.Add((systemSlug, subfolder, diskRoot));
        }

        void ConsiderDiskRoot(string root)
        {
            if (!Directory.Exists(root))
                return;

            foreach (var systemDir in Directory.EnumerateDirectories(root))
            {
                var systemSlug = Path.GetFileName(systemDir);
                foreach (var candidate in subfolderCandidates)
                {
                    var fullPath = Path.Combine(systemDir, candidate);
                    // Ignore empty plugin stubs (.gitkeep only) so they cannot shadow host data.
                    if (Directory.Exists(fullPath) &&
                        Directory.EnumerateFiles(fullPath, "*.yaml", SearchOption.TopDirectoryOnly).Any())
                    {
                        Add(systemSlug, candidate, root);
                        break;
                    }
                }
            }
        }

        // 1. Embedded resources seed systems (diskRoot = primary for path composition / extract).
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
            if (!subfolderCandidates.Contains(subfolder, StringComparer.OrdinalIgnoreCase))
                continue;

            Add(systemSlug, subfolder, rulesetDataDirectory);
        }

        // 2. Primary disk root
        ConsiderDiskRoot(rulesetDataDirectory);

        // 3. Plugin / additional roots (appended; providers merge last-wins on template name)
        if (additionalRulesetDataRoots != null)
        {
            foreach (var root in additionalRulesetDataRoots)
                ConsiderDiskRoot(root);
        }

        return results
            .OrderBy(x => x.systemSlug, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.diskRoot, StringComparer.OrdinalIgnoreCase);
    }
}
