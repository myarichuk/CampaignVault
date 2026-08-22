using CampaignVault.Models;
using CampaignVault.Rulesets;

namespace CampaignVault.Data.Migrations;

/// <summary>
/// One-time repair for characters whose SystemStats collapsed to the base SystemExtension type
/// before SystemExtensionNewtonsoftConverter existed (see that class and SystemStatsMerger.Merge for
/// the root cause: RavenDB's Newtonsoft serializer had no way to reconstruct Dnd5eExtension/
/// Pf2eExtension on document load, since that polymorphism was declared only via System.Text.Json
/// attributes). Every combatant character (KeepAlive/MaxHp/IsPc/IsPartyCompanion) in a dnd5e or pf2e
/// campaign whose SystemStats is *exactly* the base type — not a subtype — gets upgraded to the
/// correct concrete type for its campaign's active ruleset.
///
/// This does NOT attempt to recover the specific field values that were already silently dropped
/// (ArmorClass, ability scores, hitDie, skillModifiers, ...) — those are genuinely gone; this
/// database has revisions disabled, so there is no prior version to recover them from. What it does
/// is stop the corruption from being permanent: once a character's SystemStats is the right type
/// again, the engine's existing IncompleteSystemStats/UninitializedHp pressure will correctly flag it
/// as needing bootstrap fields (same as any newly-created character), letting a normal
/// character_update repopulate it going forward instead of that fix being silently lost again.
///
/// Idempotent: safe to run multiple times (only touches documents currently at the base type).
/// </summary>
public class RepairDegradedSystemStats
{
    private readonly IDocumentStore _documentStore;
    private readonly CampaignDocumentKeys _keys = new();

    public RepairDegradedSystemStats(IDocumentStore documentStore)
    {
        _documentStore = documentStore;
    }

    public async Task<(int Repaired, List<string> Details)> ExecuteAsync(CancellationToken ct = default)
    {
        using var session = _documentStore.OpenAsyncSession();

        var candidates = await session.Query<Character>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
            .Where(c => c.CampaignName != null)
            .ToListAsync(ct);

        var repaired = 0;
        var details = new List<string>();
        var rulesetByCampaign = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var character in candidates)
        {
            // Only the exact base type is a repair candidate — Dnd5eExtension/Pf2eExtension (or any
            // future subtype) is already correctly typed, and shouldn't be touched.
            if (character.SystemStats.GetType() != typeof(SystemExtension))
            {
                continue;
            }

            if (!SystemStatsCompleteness.IsCombatant(character))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(character.CampaignName))
            {
                continue;
            }

            if (!rulesetByCampaign.TryGetValue(character.CampaignName, out var activeSystem))
            {
                var config = await session.LoadAsync<CampaignConfig>(_keys.Config(character.CampaignName), ct);
                activeSystem = config?.ActiveSystem;
                rulesetByCampaign[character.CampaignName] = activeSystem;
            }

            if (activeSystem is not (RulesetSystem.Dnd5e or RulesetSystem.Pathfinder2e))
            {
                // Narrative/unrecognized systems are expected to stay on the base type.
                continue;
            }

            // Merge preserves whatever base-class fields (Willpower, Morale, Attributes, ...) the
            // degraded object already carries — CreateDefault's own fields all equal the ruleset
            // default, so DeepMerge's "skip if it matches the default" rule leaves them alone. Only
            // the type of the result changes.
            character.SystemStats = SystemStatsMerger.Merge(
                character.SystemStats, SystemStatsMerger.CreateDefault(activeSystem), activeSystem);

            repaired++;
            details.Add($"{character.Id} ({character.Name}, campaign={character.CampaignName}, system={activeSystem})");
        }

        if (repaired > 0)
        {
            await session.SaveChangesAsync(ct);
        }

        return (repaired, details);
    }
}
