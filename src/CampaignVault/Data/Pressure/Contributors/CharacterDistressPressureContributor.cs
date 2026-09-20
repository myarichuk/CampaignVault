using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class CharacterDistressPressureContributor : IPressureContributor
{
    public const string UninitializedHpGroupingKey = "Character:UninitializedHp";
    public const string CriticallyWoundedGroupingKey = "Character:CriticallyWounded";
    public const string DyingGroupingKey = "Character:Dying";
    public const string MoraleGroupingKey = "Character:Attribute:Morale";
    public const string WillpowerGroupingKey = "Character:Attribute:Willpower";
    public const string TemperatureLowGroupingKey = "Character:Attribute:TemperatureLow";
    public const string TemperatureHighGroupingKey = "Character:Attribute:TemperatureHigh";

    public static string GetStatusGroupingKey(string statusName) => $"Character:Status:{statusName}";
    public static string GetNeedGroupingKey(string needKey) => $"Character:Need:{needKey}";
    public static string GetAttributeGroupingKey(string attributeKey) => $"Character:Attribute:{attributeKey}";
    public static string GetRelationshipGroupingKey(string targetId) => $"Character:Relationship:{targetId}";

    public PressureScope Scope => PressureScope.World;
    public int Order => 20;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var threshold = ctx.Config.CharacterPressureHpCriticalThreshold;
        var characters = await PressureQueryHelper.QueryKeepAliveCharactersAsync(ctx.Session, ctx.CampaignName, 100, ct);

        // Ambient "needs" drift (hunger/thirst/tiredness/...) is narrative flavor tied to who is
        // actually on-screen right now. Surfacing it for every KeepAlive character in the campaign
        // floods scenes with noise about NPCs the party has never met. When we know who the party is
        // (PartyCharacterIds set), restrict needs pressure to them; HP/status/morale/willpower/
        // temperature/relationship checks below stay campaign-wide since those flag urgent problems
        // the DM should know about regardless of scene. When PartyCharacterIds is null (e.g.
        // advance_world, or a contributor test with no scene context), fall back to the prior
        // campaign-wide behavior so ambient drift elsewhere in the world still gets flagged.
        HashSet<string>? needsRelevantIds = ctx.PartyCharacterIds != null
            ? new HashSet<string>(ctx.PartyCharacterIds, StringComparer.OrdinalIgnoreCase)
            : null;

        var pressure = new List<WorldPressureItem>();
        var badCategories = new[] { "Injury", "Condition", "Disease", "Poison", "Curse" };

        foreach (var c in characters)
        {
            // MaxHp == 0 means the character was created without HP — the LLM must fix this.
            // D&D 5e PCs: max hit die + CON modifier. NPCs/creatures: use stat block value.
            if (c.MaxHp <= 0)
            {
                pressure.Add(new(
                    PressureSeverity.EngineWarning,
                    c.Id,
                    $"[ENGINE] {c.Name} has no MaxHp set (created with 0 or omitted). "
                    + $"PCs: omit maxHp and supply bootstrap fields — engine derives HP. Fix via commit's character_update: "
                    + $"{{ \"$type\": \"character_update\", \"characterId\": \"{c.Id}\", "
                    + $"\"systemStats\": {{ \"$system\": \"dnd5e\", \"hitDie\": \"d10\", \"level\": 1, \"constitution\": 14 }} }} "
                    + "Creature stat blocks: set systemStats.statBlockHp or maxHp (e.g. Goblin statBlockHp: 7). "
                    + "Optional currentHp alone for wounded state at create.",
                    UninitializedHpGroupingKey) { EntityName = c.Name });
                continue; // skip dying/dead check; HP is simply not set yet
            }

            if (c.CurrentHp <= c.MaxHp * threshold && c.CurrentHp > 0)
            {
                pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is critically wounded ({c.CurrentHp}/{c.MaxHp} HP).", CriticallyWoundedGroupingKey) { EntityName = c.Name });
            }
            else if (c.MaxHp > 0 && c.CurrentHp <= 0)
            {
                pressure.Add(new(PressureSeverity.EngineWarning, c.Id, $"{c.Name} is dying or dead ({c.CurrentHp}/{c.MaxHp} HP). Resolve this: stabilize, death save, or mark as deceased.", DyingGroupingKey) { EntityName = c.Name });
            }

            if (c.SystemStats?.StatusEffects != null)
            {
                foreach (var status in c.SystemStats.StatusEffects)
                {
                    if (status.Category == null || badCategories.Contains(status.Category, StringComparer.OrdinalIgnoreCase))
                    {
                        pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is suffering from {status.Name} ({status.Category ?? "Unknown"}).", GetStatusGroupingKey(status.Name)) { EntityName = c.Name });
                    }
                }
            }

            if (c.Needs?.ActiveNeeds != null && (needsRelevantIds == null || needsRelevantIds.Contains(c.Id)))
            {
                // Narrative fatigue pressure ("tiredness") flows through here, ruleset-agnostic.
                // Mechanical D&D exhaustion pressure is a separate, ruleset-specific concern
                // (see Dnd5eExhaustionPressureContributor).
                foreach (var kvp in c.Needs.ActiveNeeds)
                {
                    switch (kvp.Value)
                    {
                        case > 80f:
                            pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is in desperate need: {kvp.Key} ({kvp.Value:F0}%).", GetNeedGroupingKey(kvp.Key)) { EntityName = c.Name });
                            break;
                        case > 50f:
                            pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} needs should be acted upon: {kvp.Key} ({kvp.Value:F0}%).", GetNeedGroupingKey(kvp.Key)) { EntityName = c.Name });
                            break;
                        case > 25f:
                            pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is starting to feel the need: {kvp.Key} ({kvp.Value:F0}%).", GetNeedGroupingKey(kvp.Key)) { EntityName = c.Name });
                            break;
                    }
                }
            }

            if (c.SystemStats != null)
            {
                if (c.SystemStats.Morale <= 10f)
                {
                    pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name}'s morale is broken ({c.SystemStats.Morale:F0}%). Consider a breakdown, retreat, or refusal to fight.", MoraleGroupingKey) { EntityName = c.Name });
                }

                if (c.SystemStats.Willpower <= 10f)
                {
                    pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name}'s willpower is drained ({c.SystemStats.Willpower:F0}%). They are highly susceptible to manipulation, fear, or giving up.", WillpowerGroupingKey) { EntityName = c.Name });
                }

                if (c.SystemStats.Temperature <= SurvivalThresholds.SevereCold)
                {
                    pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is freezing to death ({c.SystemStats.Temperature:F0}). They should exhibit severe physical symptoms.", TemperatureLowGroupingKey) { EntityName = c.Name });
                }
                else if (c.SystemStats.Temperature >= SurvivalThresholds.SevereHeat)
                {
                    pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is suffering from extreme heat ({c.SystemStats.Temperature:F0}). They should exhibit exhaustion or heatstroke.", TemperatureHighGroupingKey) { EntityName = c.Name });
                }

                if (c.SystemStats.Attributes != null)
                {
                    // Deliberately does not read mechanical "exhaustion_level" — narrative fatigue
                    // pressure comes from the "tiredness" need above; mechanical D&D exhaustion
                    // pressure is handled separately by Dnd5eExhaustionPressureContributor.
                    foreach (KeyValuePair<string, float> attribute in c.SystemStats.Attributes)
                    {
                        var attrKey = attribute.Key.ToLowerInvariant();
                        if ((attrKey == "corruption" || attrKey == "fear") && attribute.Value >= 90f)
                        {
                            pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is consumed by {attribute.Key} ({attribute.Value:F0}). They should exhibit severe physical or mental symptoms.", GetAttributeGroupingKey(attribute.Key)) { EntityName = c.Name });
                        }
                    }
                }
            }

            if (c.Social?.Relationships != null)
            {
                foreach (var rel in c.Social.Relationships)
                {
                    if (rel.Value <= -80)
                    {
                        pressure.Add(new(PressureSeverity.NarrativePrompt, c.Id, $"{c.Name} actively despises '{rel.Key}' ({rel.Value} relationship). Their dialogue and actions towards them should be highly antagonistic or hostile.", GetRelationshipGroupingKey(rel.Key)) { EntityName = c.Name });
                    }
                    else if (rel.Value >= 80)
                    {
                        pressure.Add(new(PressureSeverity.NarrativePrompt, c.Id, $"{c.Name} has deep trust and affection for '{rel.Key}' (+{rel.Value} relationship). They should act protective or highly agreeable towards them.", GetRelationshipGroupingKey(rel.Key)) { EntityName = c.Name });
                    }
                }
            }
        }

        return pressure;
    }
}