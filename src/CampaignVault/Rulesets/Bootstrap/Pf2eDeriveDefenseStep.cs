using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Rulesets.Bootstrap;

public sealed class Pf2eDeriveDefenseStep : IBootstrapStep
{
    public string Name => "pf2e.derive_defense";

    public bool CanApply(BootstrapContext context) =>
        context.Character.SystemStats is Pf2eExtension stats && stats.ArmorClass == 10;

    public async Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default)
    {
        var stats = (Pf2eExtension)context.Character.SystemStats;
        var dexMod = stats.DexterityMod;
        var hints = new List<string>();

        // PF2e unarmored AC = 10 + DEX mod + Level + Proficiency (Trained=2, Expert=4, Master=6, Legendary=8)
        // Scoped fix: assume Trained proficiency when level is set (will be superseded by full proficiency system in R2)
        stats.ArmorClass = 10 + dexMod + (stats.Level ?? 0) + (stats.Level.HasValue ? 2 : 0);

        if (stats.Level.HasValue)
        {
            hints.Add(
                $"AC assumes Trained proficiency in unarmored defense. If {context.Character.Name} has Expert+ proficiency or wears armor with a different proficiency, correct the AC via system_stats.");
        }

        if (context.Session is not null && !await HasWornArmorAsync(context.Session, context.Character.Id, ct))
        {
            hints.Add(
                $"Worn armor not detected for {context.Character.Name}. Base AC is unarmored (10 + DEX mod). "
                + "To equip armor, commit item_create + system_stats, e.g.: "
                + $"[ {{ \"$type\": \"item_create\", \"itemId\": \"items/{context.Character.Id}-armor\", "
                + $"\"name\": \"Chain Shirt\", \"holderId\": \"{context.Character.Id}\", \"coreCategory\": \"Armor\", "
                + $"\"properties\": {{ \"acBonus\": \"2\" }} }}, "
                + $"{{ \"$type\": \"system_stats\", \"characterId\": \"{context.Character.Id}\", "
                + $"\"systemStats\": {{ \"$system\": \"pf2e\", \"armorClass\": {10 + dexMod + 2} }} }} ]");
        }

        return new BootstrapStepResult
        {
            StepName = Name,
            Message =
                $"Set armorClass={stats.ArmorClass} (unarmored 10 + DEX {dexMod:+#;-#;+0}) for {context.Character.Name}.",
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