namespace CampaignVault.Data.Templates;

/// <summary>
/// How a summoned creature regards its summoner once control lapses: animate dead's
/// skeleton stays loyal while controlled, an unbound elemental is neutral, a demon
/// escaping a broken circle is hostile.
/// </summary>
public enum SummonDisposition
{
    Loyal,
    Neutral,
    Hostile,
}

/// <summary>
/// Cap on how many summoned creatures one caster may control through a single summon
/// effect. Enforced at cast; excess oldest-first is released.
/// </summary>
public record SummonControlCap
{
    public int? MaxCreatures { get; init; }
    public int? MaxHitDice { get; init; }
    public int? HitDicePerCasterLevel { get; init; }
}

/// <summary>
/// Inline stat seed for a homebrew summon (or a stock creature the handbook catalog
/// has no entry for): the minion Character is raised from this.
/// </summary>
public record SummonStatSeed
{
    public int? Hp { get; init; }
    public int? Defense { get; init; }
    public List<string> Attacks { get; init; } = [];
}

/// <summary>
/// Summoning effect of a spell (animate dead, conjure animals, ...). Null means the
/// spell summons nothing — casting stays slot validation plus narration.
/// </summary>
public record SummonEffect
{
    /// <summary>
    /// Handbook creature reference(s) the minion is raised from; the caster picks one
    /// when several fit (animate dead: skeleton from bones, zombie from a corpse).
    /// </summary>
    public List<string> Creatures { get; init; } = [];

    public SummonStatSeed? InlineSeed { get; init; }

    /// <summary>Minions created per cast, keyed by spell-slot level.</summary>
    public Dictionary<int, int>? CountAtSlotLevel { get; init; }

    /// <summary>
    /// Caller-chosen headcounts keyed by slot, strongest option first (conjure
    /// animals at 3rd: one CR2 beast, two CR1, four CR1/2, or eight CR1/4). The
    /// cast names one via the action's <c>count</c> parameter; omitted means the
    /// first (strongest) option.
    /// </summary>
    public Dictionary<int, List<int>>? CountChoicesAtSlotLevel { get; init; }

    /// <summary>
    /// Recast-to-retain cap keyed by slot (animate dead reasserts control over up to
    /// four creatures at 3rd level instead of animating a new one). Null means a
    /// recast starts fresh (concentration summons).
    /// </summary>
    public Dictionary<int, int>? RetainCountAtSlotLevel { get; init; }

    /// <summary>Control lasts this many combat rounds (concentration-bound summons).</summary>
    public int? DurationRounds { get; init; }

    /// <summary>
    /// Control lasts this many campaign days (animate dead: 1). Null means indefinite —
    /// bound until dispelled or destroyed.
    /// </summary>
    public int? DurationDays { get; init; }

    public SummonControlCap? ControlCap { get; init; }

    public SummonDisposition Disposition { get; init; } = SummonDisposition.Loyal;
}
