namespace CampaignVault.Data.Templates;

/// <summary>
/// Reference creature/monster stat block, surfaced via get_system_handbook as a discoverable
/// template the LLM DM can draw on when improvising an encounter. Not a live gameplay type —
/// NPCs and creatures are represented as ordinary Character documents; this is seed data only.
/// </summary>
public record CreatureDefinition : RulesetTemplate
{
    public string System { get; init; } = null!;
    public int? Level { get; init; }
    public string? ChallengeRating { get; init; }
    public int? Hp { get; init; }
    public int? Defense { get; init; }
    public List<string> Skills { get; init; } = [];
    public List<string> Abilities { get; init; } = [];

    /// <summary>Offered as a party companion template in the character builder.</summary>
    public bool Companion { get; init; }

    /// <summary>Companions only: the stat block fields (<c>statblocks/companion.yaml</c> keys) the builder copies into a draft.</summary>
    public Dictionary<string, string> StatBlock { get; init; } = [];

    public static CreatureDefinition Merge(CreatureDefinition child, CreatureDefinition parent) =>
        child with
        {
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            Description = child.Description ?? parent.Description,
            Level = child.Level ?? parent.Level,
            ChallengeRating = child.ChallengeRating ?? parent.ChallengeRating,
            Hp = child.Hp ?? parent.Hp,
            Defense = child.Defense ?? parent.Defense,
            Skills = child.Skills.Count > 0 ? child.Skills : parent.Skills,
            Abilities = child.Abilities.Count > 0 ? child.Abilities : parent.Abilities,
            Companion = child.Companion || parent.Companion,
            StatBlock = child.StatBlock.Count > 0 ? child.StatBlock : parent.StatBlock,
        };
}
