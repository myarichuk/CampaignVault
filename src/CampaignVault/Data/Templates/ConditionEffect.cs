namespace CampaignVault.Data.Templates;

/// <summary>The closed vocabulary of what a condition does mechanically.</summary>
public static class ConditionEffectKinds
{
    /// <summary>Advantage / disadvantage on the rolls named by <c>on</c> (attack, check or save); <c>subject</c> narrows it.</summary>
    public const string Advantage = "advantage";
    public const string Disadvantage = "disadvantage";

    /// <summary>The bearer automatically fails saves of the abilities in <c>abilities</c> (Strength, Dexterity...).</summary>
    public const string AutoFailSave = "autoFailSave";

    /// <summary>An attack that hits the bearer from within <c>range</c> (melee) is a critical hit. Only meaningful against the bearer.</summary>
    public const string AutoCrit = "autoCrit";

    /// <summary>The bearer's speed: <c>set</c> a fixed number of feet (0 for none) or multiply it by <c>multiplier</c>.</summary>
    public const string Speed = "speed";

    /// <summary>The bearer cannot take actions or reactions. Read by the action gate (turn flow), not by rolls.</summary>
    public const string BlocksActions = "blocksActions";

    /// <summary>The bearer's effective hit point maximum is multiplied by <c>multiplier</c> (the stored maximum is untouched).</summary>
    public const string MaxHpMultiplier = "maxHpMultiplier";

    /// <summary>The bearer dies when the condition reaches this level (see <c>levels</c>).</summary>
    public const string Death = "death";

    /// <summary>
    /// A numeric modifier (<c>value</c>, times the condition's level when <c>perLevel</c>) to the rolls named by <c>on</c>: attack,
    /// check, save or ac. PF2e's penalties. <c>bonusType</c> (status, circumstance, untyped) follows PF2e stacking: of the same type
    /// only the worst penalty and best bonus count.
    /// </summary>
    public const string Bonus = "bonus";

    /// <summary>The bearer counts as having the condition named in <c>subject</c> (PF2e: prone, grabbed and paralyzed imply off-guard).</summary>
    public const string Implies = "implies";

    public static readonly IReadOnlyList<string> All =
        [Advantage, Disadvantage, AutoFailSave, AutoCrit, Speed, BlocksActions, MaxHpMultiplier, Death, Bonus, Implies];
}

/// <summary>Whose rolls an effect touches.</summary>
public static class ConditionScopes
{
    /// <summary>The bearer's own rolls and stats (the default).</summary>
    public const string Self = "self";

    /// <summary>Rolls other creatures make against the bearer (an attack against it).</summary>
    public const string AgainstSelf = "againstSelf";

    public static readonly IReadOnlyList<string> All = [Self, AgainstSelf];
}

/// <summary>One mechanical effect of a condition. Authored in the condition's YAML (<c>effects:</c> or one <c>levels:</c> entry).</summary>
public record ConditionEffect
{
    public string Kind { get; init; } = "";

    /// <summary><c>self</c> (default) or <c>againstSelf</c>.</summary>
    public string? Scope { get; init; }

    /// <summary>advantage / disadvantage / bonus: attack, check, save, or (bonus only) ac.</summary>
    public string? On { get; init; }

    /// <summary>Narrows an advantage or disadvantage to one skill or ability, e.g. <c>dexterity</c>. Omit for all.</summary>
    public string? Subject { get; init; }

    /// <summary>autoFailSave: the abilities whose saves fail automatically.</summary>
    public List<string> Abilities { get; init; } = [];

    /// <summary>An attack-only gate: <c>melee</c> or <c>ranged</c>. Omit for either.</summary>
    public string? Range { get; init; }

    /// <summary>
    /// A roll tag the roll must carry for the effect to apply. The DM supplies tags through the action's <c>assert</c> parameter,
    /// e.g. <c>sourceInSight</c> for a frightened creature whose source of fear is in view.
    /// </summary>
    public string? RequiresTag { get; init; }

    /// <summary>bonus: the modifier (negative for a penalty).</summary>
    public int? Value { get; init; }

    /// <summary>bonus: multiply <c>value</c> by the condition's level (frightened 2 is a -2 penalty).</summary>
    public bool PerLevel { get; init; }

    /// <summary>bonus (PF2e): status, circumstance or untyped (the default).</summary>
    public string? BonusType { get; init; }

    /// <summary>speed: feet to set (0 stops movement).</summary>
    public int? Set { get; init; }

    /// <summary>speed, maxHpMultiplier: the factor.</summary>
    public float? Multiplier { get; init; }

    public bool AppliesAgainstSelf => string.Equals(Scope, ConditionScopes.AgainstSelf, StringComparison.OrdinalIgnoreCase);
}
