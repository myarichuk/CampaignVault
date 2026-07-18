using CampaignVault.Data;
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
        var hints = new List<string>();

        var equippedItems = context.Session is not null
            ? await GetEquippedItemsAsync(context.Session, context.Character.Id, ct)
            : [];

        if (equippedItems.Count > 0)
        {
            ArmorParameterResolver.Apply(context.Character, equippedItems);
        }
        else
        {
            // PF2e unarmored AC = 10 + DEX mod + Level + Proficiency (Trained=2, Expert=4, Master=6, Legendary=8)
            var proficiencyBonus = stats.AcProficiency == Pf2eProficiencyRank.Untrained || !stats.Level.HasValue
                ? 0
                : stats.Level.Value + (int)stats.AcProficiency;
            stats.ArmorClass = 10 + stats.DexterityMod + proficiencyBonus;

            hints.Add(
                $"Worn armor not detected for {context.Character.Name}. Base AC is unarmored (10 + DEX mod). "
                + "To equip starting armor, upsert_item with equipZones/equipLayer/isEquipped:true so AC applies immediately, e.g.: "
                + $"{{ \"id\": \"items/{context.Character.Id}-armor\", \"name\": \"Chain Shirt\", "
                + $"\"holderId\": \"{context.Character.Id}\", \"coreCategory\": \"Armor\", "
                + "\"equipZones\": [\"Torso\"], \"equipLayer\": \"Armor\", \"isEquipped\": true, "
                + "\"properties\": { \"acBonus\": \"2\", \"dexCap\": \"3\" } }. "
                + "For gear equipped mid-campaign, use the item_equip commit instead.");
        }

        if (stats.Level.HasValue && stats.AcProficiency != Pf2eProficiencyRank.Trained)
        {
            hints.Add(
                $"AC uses {stats.AcProficiency} proficiency. If {context.Character.Name}'s proficiency should be different, correct the AC via system_stats.");
        }

        return new BootstrapStepResult
        {
            StepName = Name,
            Message = $"Set armorClass={stats.ArmorClass} for {context.Character.Name}.",
            LlmHints = hints,
        };
    }

    private static async Task<List<Item>> GetEquippedItemsAsync(
        IAsyncDocumentSession session,
        string characterId,
        CancellationToken ct)
    {
        var held = await session.Advanced.AsyncDocumentQuery<Item, Item_Search>()
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
            .WhereEquals(x => x.HolderId, characterId)
            .Take(50)
            .ToListAsync(ct);

        return held.Where(i => i.IsEquipped).ToList();
    }
}