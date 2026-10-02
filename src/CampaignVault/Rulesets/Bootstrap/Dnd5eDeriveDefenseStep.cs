using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Bootstrap;

/// <summary>
/// Armor class from worn armor, or without armor the best a class feature gives (Unarmored Defense: 10 + Dexterity +
/// Constitution, from the progression data when <paramref name="progressions"/> is given), else 10 + Dexterity.
/// </summary>
public sealed class Dnd5eDeriveDefenseStep(ProgressionDefinitionProvider? progressions = null) : IBootstrapStep
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
            var feature = CharacterClassFeatures.UnarmoredArmorClass(
                context.Character, RulesetSystem.Dnd5e, progressions, a => stats.GetAbilityModifier(Score(stats, a)));
            stats.ArmorClass = Math.Max(10 + dexMod, feature ?? 0);

            hints.Add(
                $"Worn armor not detected for {context.Character.Name}. Base AC is unarmored ({(feature > 10 + dexMod ? "a class feature's formula" : "10 + DEX")}). "
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

    private static int Score(Dnd5eExtension stats, string ability) => ability.ToLowerInvariant() switch
    {
        "strength" => stats.Strength,
        "dexterity" => stats.Dexterity,
        "constitution" => stats.Constitution,
        "intelligence" => stats.Intelligence,
        "wisdom" => stats.Wisdom,
        "charisma" => stats.Charisma,
        _ => 10,
    };
}
