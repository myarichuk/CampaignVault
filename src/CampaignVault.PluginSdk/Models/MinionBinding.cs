using CampaignVault.Data.Templates;

namespace CampaignVault.Models;

/// <summary>
/// Minion-control link on a summoned creature: who controls it, under what spell,
/// and how/when that control ends. The caster mirrors the link in
/// <see cref="Character.ControlsMinionIds"/>; both sides are kept in sync by the
/// summon, dismiss, cap-release, and lapse paths (never hand-edited separately).
/// </summary>
public record MinionBinding
{
    /// <summary>Character id of the controlling caster.</summary>
    public string ControllerId { get; init; } = null!;

    /// <summary>Spell that raised this minion (e.g. "animate_dead"). Null for non-spell bindings.</summary>
    public string? SpellName { get; init; }

    /// <summary>
    /// How the minion regards its summoner once control lapses (copied from the
    /// spell's summon effect at creation). While bound the minion obeys; after a
    /// lapse the GM plays this disposition.
    /// </summary>
    public SummonDisposition Disposition { get; init; } = SummonDisposition.Loyal;

    /// <summary>True when control rides on the caster's concentration (conjure/summon spells).</summary>
    public bool ConcentrationBound { get; init; }

    /// <summary>Control lasts this many combat rounds from the summoning round. Null means not round-scoped.</summary>
    public int? DurationRounds { get; init; }

    /// <summary>Absolute combat round control ends (anchored at creation when combat is active).</summary>
    public int? ExpiresAtRound { get; init; }

    /// <summary>Control lasts this many campaign days from the summoning day. Null means not day-scoped.</summary>
    public int? DurationDays { get; init; }

    /// <summary>Absolute campaign day control ends (anchored at creation when the clock is available).</summary>
    public int? ExpiresAtDay { get; init; }

    /// <summary>
    /// True once control has lapsed (concentration broken, caster downed, duration
    /// elapsed, cap released, dismissed). A lapsed minion stays in the world as an
    /// ordinary NPC played at <see cref="Disposition"/> — it never silently rebinds.
    /// </summary>
    public bool ControlLapsed { get; init; }
}
