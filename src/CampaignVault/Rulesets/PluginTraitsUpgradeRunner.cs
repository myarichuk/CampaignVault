using CampaignVault.Models;
using CampaignVault.Plugins;

namespace CampaignVault.Rulesets;

/// <summary>
/// Runs every registered <see cref="IPluginTraitsUpgrader"/> against a character's
/// <c>SystemStats.Traits</c> on load, and — as a separate, startup-only check — warns about trait-key
/// prefixes in the data that no loaded plugin claims (a "missing master": the data is left exactly as
/// it is, never deleted, until the plugin that owns it is reinstalled).
/// </summary>
public static class PluginTraitsUpgradeRunner
{
    /// <summary>
    /// Applies every upgrader to <paramref name="character"/>'s Traits dictionary. An upgrader that
    /// throws is logged and skipped; it never blocks character load or any other upgrader. Returns
    /// true if any upgrader reported a change, so the caller knows the character needs persisting.
    /// </summary>
    public static bool ApplyUpgrades(
        Character character,
        IEnumerable<IPluginTraitsUpgrader>? upgraders,
        ILogger? logger = null)
    {
        var traits = character.SystemStats?.Traits;
        if (traits is null || traits.Count == 0 || upgraders is null)
        {
            return false;
        }

        var changed = false;
        foreach (var upgrader in upgraders)
        {
            try
            {
                if (upgrader.TryUpgrade(traits))
                {
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Plugin traits upgrader '{PluginId}' threw while upgrading character '{CharacterId}'; " +
                    "any Traits changes it made before throwing were kept as-is.",
                    upgrader.PluginId, character.Id);
            }
        }

        return changed;
    }

    /// <summary>
    /// Startup-only advisory scan: finds every <c>"&lt;prefix&gt;."</c> in any character's Traits keys
    /// (across all campaigns) whose prefix is not in <paramref name="claimedPrefixes"/> — a plugin id
    /// or mode id from a currently loaded plugin — and logs one summary warning per orphaned prefix.
    /// Unprefixed keys (no '.') always ride per the "Key convention" in PLUGINS.md and are never
    /// orphaned. Never mutates data. Mirrors ConventionRegistration.WarnOnUnpublishedEventSubscriptions:
    /// advisory only, never fails startup.
    /// </summary>
    public static async Task<IReadOnlyList<string>> WarnOnOrphanedTraitPrefixesAsync(
        IDocumentStore documentStore,
        IReadOnlyCollection<string> claimedPrefixes,
        ILogger? logger,
        CancellationToken ct = default)
    {
        using var session = documentStore.OpenAsyncSession();

        var characters = await session.Query<Character>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
            .Where(c => c.CampaignName != null)
            .ToListAsync(ct);

        var claimed = new HashSet<string>(claimedPrefixes, StringComparer.OrdinalIgnoreCase);
        var orphaned = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var character in characters)
        {
            var traits = character.SystemStats?.Traits;
            if (traits is null)
            {
                continue;
            }

            foreach (var key in traits.Keys)
            {
                var dot = key.IndexOf('.');
                if (dot <= 0)
                {
                    continue; // unprefixed keys always ride; nothing to check.
                }

                var prefix = key[..dot];
                if (!claimed.Contains(prefix))
                {
                    orphaned.Add(prefix);
                }
            }
        }

        foreach (var prefix in orphaned)
        {
            logger?.LogWarning(
                "Character trait key prefix '{Prefix}.' has no loaded plugin claiming id/mode '{Prefix}'. " +
                "The data is left exactly as-is — not deleted, not upgraded — until that plugin is reinstalled " +
                "(same as a missing master: inert, not lost).",
                prefix, prefix);
        }

        return orphaned.ToList();
    }
}
