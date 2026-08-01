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
                    UninitializedHpGroupingKey));
                continue; // skip dying/dead check; HP is simply not set yet
            }

            if (c.CurrentHp <= c.MaxHp * threshold && c.CurrentHp > 0)
            {
                pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is critically wounded ({c.CurrentHp}/{c.MaxHp} HP).", CriticallyWoundedGroupingKey));
            }
            else if (c.MaxHp > 0 && c.CurrentHp <= 0)
            {
                pressure.Add(new(PressureSeverity.EngineWarning, c.Id, $"{c.Name} is dying or dead ({c.CurrentHp}/{c.MaxHp} HP). Resolve this: stabilize, death save, or mark as deceased.", DyingGroupingKey));
            }

            if (c.SystemStats?.StatusEffects != null)
            {
                foreach (var status in c.SystemStats.StatusEffects)
                {
                    if (status.Category == null || badCategories.Contains(status.Category, StringComparer.OrdinalIgnoreCase))
                    {
                        pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is suffering from {status.Name} ({status.Category ?? "Unknown"}).", GetStatusGroupingKey(status.Name)));
                    }
                }
            }

            if (c.Needs?.ActiveNeeds != null)
            {
                // Narrative fatigue pressure ("tiredness") flows through here, ruleset-agnostic.
                // Mechanical D&D exhaustion pressure is a separate, ruleset-specific concern
                // (see Dnd5eExhaustionPressureContributor).
                foreach (var kvp in c.Needs.ActiveNeeds)
                {
                    switch (kvp.Value)
                    {
                        case > 80f:
                            pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is in desperate need: {kvp.Key} ({kvp.Value:F0}%).", GetNeedGroupingKey(kvp.Key)));
                            break;
                        case > 50f:
                            pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} needs should be acted upon: {kvp.Key} ({kvp.Value:F0}%).", GetNeedGroupingKey(kvp.Key)));
                            break;
                        case > 25f:
                            pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} start feeling the need: {kvp.Key} ({kvp.Value:F0}%).", GetNeedGroupingKey(kvp.Key)));
                            break;
                    }
                }
            }

            if (c.SystemStats != null)
            {
                if (c.SystemStats.Morale <= 10f)
                {
                    pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name}'s morale is broken ({c.SystemStats.Morale:F0}%). Consider a breakdown, retreat, or refusal to fight.", MoraleGroupingKey));
                }

                if (c.SystemStats.Willpower <= 10f)
                {
                    pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name}'s willpower is drained ({c.SystemStats.Willpower:F0}%). They are highly susceptible to manipulation, fear, or giving up.", WillpowerGroupingKey));
                }

                if (c.SystemStats.Temperature <= -20f)
                {
                    pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is freezing to death ({c.SystemStats.Temperature:F0}). They should exhibit severe physical symptoms.", TemperatureLowGroupingKey));
                }
                else if (c.SystemStats.Temperature >= 50f)
                {
                    pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is suffering from extreme heat ({c.SystemStats.Temperature:F0}). They should exhibit exhaustion or heatstroke.", TemperatureHighGroupingKey));
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
                            pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is consumed by {attribute.Key} ({attribute.Value:F0}). They should exhibit severe physical or mental symptoms.", GetAttributeGroupingKey(attribute.Key)));
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
                        pressure.Add(new(PressureSeverity.NarrativePrompt, c.Id, $"{c.Name} actively despises '{rel.Key}' ({rel.Value} relationship). Their dialogue and actions towards them should be highly antagonistic or hostile.", GetRelationshipGroupingKey(rel.Key)));
                    }
                    else if (rel.Value >= 80)
                    {
                        pressure.Add(new(PressureSeverity.NarrativePrompt, c.Id, $"{c.Name} has deep trust and affection for '{rel.Key}' (+{rel.Value} relationship). They should act protective or highly agreeable towards them.", GetRelationshipGroupingKey(rel.Key)));
                    }
                }
            }
        }

        return pressure;
    }
}