using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;

namespace CampaignVault.Data.Migrations;

/// <summary>
/// Adds the class-derived resource pools a character should have but doesn't (a Fighter with no action_surge, a
/// Wizard with no spell slots). Pools were only ever created by character_create and level_up, so characters
/// built or edited any other way (systemStats patches, imports, older versions) were left pool-less.
///
/// Additive and conservative: it computes the expected pools with the same <see cref="ResourcePoolInitializer"/>
/// rules the engine uses, then adds only the missing ones. Existing pools (including spent amounts) are never
/// touched or removed, and characters that declare no class are skipped, so stat-block creatures don't gain pools.
/// Idempotent: a second run finds nothing to add.
/// </summary>
public class RepairMissingResourcePools(IDocumentStore documentStore, ResourcePoolInitializer initializer)
{
    private const int PageSize = 256;
    private readonly CampaignDocumentKeys _keys = new();

    public async Task<(int Repaired, List<string> Details)> ExecuteAsync(CancellationToken ct = default)
    {
        var repaired = 0;
        var details = new List<string>();
        var configs = new Dictionary<string, CampaignConfig?>(StringComparer.OrdinalIgnoreCase);

        for (var skip = 0; ; skip += PageSize)
        {
            using var session = documentStore.OpenAsyncSession();
            var page = await session.Query<Character>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
                .Where(c => c.CampaignName != null)
                .OrderBy(c => c.Id)
                .Skip(skip)
                .Take(PageSize)
                .ToListAsync(ct);

            foreach (var character in page)
            {
                if (character.SystemStats is null
                    || CharacterClassResolver.ResolveClassLevels(character).Count == 0
                    || string.IsNullOrWhiteSpace(character.CampaignName))
                {
                    continue;
                }

                if (!configs.TryGetValue(character.CampaignName, out var config))
                {
                    config = await session.LoadAsync<CampaignConfig>(_keys.Config(character.CampaignName), ct);
                    configs[character.CampaignName] = config;
                }

                var system = config?.ActiveSystem;
                var matchesSystem = (system == RulesetSystem.Dnd5e && character.SystemStats is Dnd5eExtension)
                                    || (system == RulesetSystem.Pathfinder2e && character.SystemStats is Pf2eExtension);
                if (!matchesSystem)
                {
                    continue;
                }

                var expected = initializer.ComputeDesiredPools(character, system!, config);
                var added = expected.Where(kv => !character.SystemStats.ResourcePools.ContainsKey(kv.Key)).ToList();
                if (added.Count == 0)
                {
                    continue;
                }

                foreach (var (name, pool) in added)
                {
                    character.SystemStats.ResourcePools[name] = pool;
                }

                repaired++;
                details.Add($"{character.Id} ({character.Name}, campaign={character.CampaignName}): +{string.Join(", +", added.Select(a => a.Key))}");
            }

            if (repaired > 0)
            {
                await session.SaveChangesAsync(ct);
            }

            if (page.Count < PageSize)
            {
                break;
            }
        }

        return (repaired, details);
    }
}
