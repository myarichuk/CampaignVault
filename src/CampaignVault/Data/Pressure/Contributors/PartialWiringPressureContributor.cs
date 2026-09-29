using CampaignVault.Models;
using CampaignVault.Rulesets;

namespace CampaignVault.Data.Pressure.Contributors;

/// <summary>
/// Flags combatants (PCs, companions and NPCs) whose declared class/feats/race disagree with the mechanics
/// derived from them: missing class pools (Fighter without action_surge), unresolved class names, stale
/// proficiency, casters with no DC. Complements <see cref="IncompleteSystemStatsPressureContributor"/>, which
/// only catches characters with no ruleset stats at all.
/// </summary>
public sealed class PartialWiringPressureContributor(CharacterWiringAuditor auditor) : IPressureContributor
{
    public const string GroupingKey = "Character:PartialWiring";

    public PressureScope Scope => PressureScope.World;
    public int Order => 16;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var activeSystem = ctx.Config.ActiveSystem;
        var characters = await PressureQueryHelper.QueryCombatantCharactersAsync(ctx.Session, ctx.CampaignName, 100, ct);
        var pressures = new List<WorldPressureItem>();

        foreach (var character in characters)
        {
            if (!SystemStatsCompleteness.IsComplete(character, activeSystem))
            {
                continue; // already reported as uninitialized; partial-wiring detail would be noise.
            }

            var findings = await auditor.AuditAsync(ctx.Session, ctx.CampaignName, character, activeSystem, ctx.Config);
            if (findings.Count == 0)
            {
                continue;
            }

            pressures.Add(new WorldPressureItem(
                PressureSeverity.EngineWarning,
                character.Id,
                $"[ENGINE] {CharacterWiringAuditor.Format(character, findings)} "
                + "Fix with a character_update systemStats patch (pools re-derive on every patch), or level_up.",
                GroupingKey));
        }

        return pressures;
    }
}
