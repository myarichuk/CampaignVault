using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Rulesets.Bootstrap;

public sealed class Dnd5eDeriveDefenseStep : IBootstrapStep
{
    public string Name => "dnd5e.derive_defense";

    public bool CanApply(BootstrapContext context) =>
        context.Character.SystemStats is Dnd5eExtension stats && stats.ArmorClass == 10;

    public async Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default)
    {
        var stats = (Dnd5eExtension)context.Character.SystemStats;
        var dexMod = stats.GetAbilityModifier(stats.Dexterity);
        var hints = new List<string>();

        if (dexMod != 0)
        {
            stats.ArmorClass = 10 + dexMod;
        }

        if (context.Session is not null && !await HasWornArmorAsync(context.Session, context.Character.Id, ct))
        {
            hints.Add(
                $"Worn armor not detected for {context.Character.Name}. Base AC is unarmored (10 + DEX). "
                + "To equip armor, commit item_create + system_stats, e.g.: "
                + $"[ {{ \"$type\": \"item_create\", \"itemId\": \"items/{context.Character.Id}-armor\", "
                + $"\"name\": \"Chain Shirt\", \"holderId\": \"{context.Character.Id}\", \"coreCategory\": \"Armor\", "
                + $"\"properties\": {{ \"acBonus\": \"2\" }} }}, "
                + $"{{ \"$type\": \"system_stats\", \"characterId\": \"{context.Character.Id}\", "
                + $"\"systemStats\": {{ \"$system\": \"dnd5e\", \"armorClass\": {10 + dexMod + 2} }} }} ]");
        }

        return new BootstrapStepResult
        {
            StepName = Name,
            Message = $"Set armorClass={stats.ArmorClass} (unarmored 10 + DEX {dexMod:+#;-#;+0}) for {context.Character.Name}.",
            LlmHints = hints,
        };
    }

    private static async Task<bool> HasWornArmorAsync(
        IAsyncDocumentSession session,
        string characterId,
        CancellationToken ct)
    {
        var items = await session.Query<Item>()
            .Where(i => i.HolderId == characterId && i.CoreCategory == ItemCategory.Armor)
            .Take(1)
            .ToListAsync(ct);
        return items.Count > 0;
    }
}