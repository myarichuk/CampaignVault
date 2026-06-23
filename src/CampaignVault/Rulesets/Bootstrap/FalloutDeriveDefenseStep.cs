using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Rulesets.Bootstrap;

public sealed class FalloutDeriveDefenseStep : IBootstrapStep
{
    public string Name => "fallout2d20.derive_defense";

    public bool CanApply(BootstrapContext context) =>
        context.Character.SystemStats is Fallout2d20Extension stats && IsUnarmoredDefense(stats.Defense);

    public async Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default)
    {
        var stats = (Fallout2d20Extension)context.Character.SystemStats;
        var expected = DeriveUnarmoredDefense(stats.Agility);
        if (stats.Defense == expected)
        {
            return null;
        }

        stats.Defense = expected;
        var hints = new List<string>();

        if (context.Session is not null && !await HasWornArmorAsync(context.Session, context.Character.Id, ct))
        {
            hints.Add(
                $"No worn armor detected for {context.Character.Name}. Unarmored defense is {expected} (AGI {stats.Agility}). "
                + "For DR from equipment, commit item_create with coreCategory Armor and damageResistance on system_stats, e.g.: "
                + $"[ {{ \"$type\": \"item_create\", \"itemId\": \"items/{context.Character.Id}-leather\", "
                + $"\"name\": \"Leather Armor\", \"holderId\": \"{context.Character.Id}\", \"coreCategory\": \"Armor\" }}, "
                + $"{{ \"$type\": \"system_stats\", \"characterId\": \"{context.Character.Id}\", "
                + $"\"systemStats\": {{ \"$system\": \"fallout2d20\", \"damageResistance\": {{ \"Physical\": 2 }} }} }} ]");
        }

        return new BootstrapStepResult
        {
            StepName = Name,
            Message = $"Set defense={expected} for {context.Character.Name} (AGI {stats.Agility}, unarmored).",
            LlmHints = hints,
        };
    }

    internal static int DeriveUnarmoredDefense(int agility) => agility >= 9 ? 2 : 1;

    internal static bool IsUnarmoredDefense(int defense) => defense is 1 or 2;

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