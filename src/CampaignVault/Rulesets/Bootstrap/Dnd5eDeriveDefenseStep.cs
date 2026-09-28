using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

public sealed class Dnd5eDeriveDefenseStep : IBootstrapStep
{
    public string Name => "dnd5e.derive_defense";

    public bool CanApply(BootstrapContext context) =>
        context.Character.SystemStats is Dnd5eExtension stats && stats.ArmorClass == 10;

    public async Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default)
    {
        var stats = (Dnd5eExtension)context.Character.SystemStats;
        var hints = new List<string>();

        var equippedItems = context.EquipmentAccess is not null
            ? await context.EquipmentAccess.GetEquippedItemsAsync(context.Character.Id, ct)
            : [];

        if (equippedItems.Count > 0)
        {
            ArmorParameterResolver.Apply(context.Character, equippedItems);
        }
        else
        {
            var dexMod = stats.GetAbilityModifier(stats.Dexterity);
            stats.ArmorClass = 10 + dexMod;

            hints.Add(
                $"Worn armor not detected for {context.Character.Name}. Base AC is unarmored (10 + DEX). "
                + "To equip starting armor, world_build's items[] with equipZones/equipLayer/isEquipped:true so AC applies immediately, e.g.: "
                + $"{{ \"id\": \"items/{context.Character.Id}-armor\", \"name\": \"Chain Shirt\", "
                + $"\"holderId\": \"{context.Character.Id}\", \"coreCategory\": \"Armor\", "
                + "\"equipZones\": [\"Torso\"], \"equipLayer\": \"Armor\", \"isEquipped\": true, "
                + "\"properties\": { \"acBonus\": \"3\", \"armorType\": \"medium\" } }. "
                + "For gear equipped mid-campaign, use the item_equip commit instead.");
        }

        return new BootstrapStepResult
        {
            StepName = Name,
            Message = $"Set armorClass={stats.ArmorClass} for {context.Character.Name}.",
            LlmHints = hints,
        };
    }
}