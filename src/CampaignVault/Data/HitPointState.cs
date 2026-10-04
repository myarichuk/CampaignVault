using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>What moving a character's hit points did at the zero line.</summary>
/// <param name="Wounded">PF2e: the wounded value after dying ended because hit points rose above 0; null otherwise.</param>
internal readonly record struct HpTransition(bool WentDown, bool Recovered, int? Wounded = null);

/// <summary>
/// The one place that sets a living character's hit points directly (a DM override, a level-up top-up, a revival). It keeps
/// the zero-crossing state honest: a character who rises above 0 loses the death-save tally, one who drops to 0 starts it.
/// Damage goes through <see cref="DamageApplication"/> instead, which has the dying rules.
/// </summary>
internal static class HitPointState
{
    /// <summary>Whether the 5e dying rules (death saves) apply: player characters only; NPC death stays an explicit commit.</summary>
    public static bool UsesDeathSaves(Character character) => character.IsPc && character.SystemStats is Dnd5eExtension;

    /// <summary>Whether a player character has dying rules at all: death saves in 5e, the dying and wounded counters in PF2e.</summary>
    public static bool UsesDyingRules(Character character) => UsesDeathSaves(character) || Pf2eDying.Applies(character);

    /// <summary>The key of the Unconscious status the engine applies at 0 HP, so it removes only its own and never a sleep spell's.</summary>
    public const string DownedEffectKey = "engine:downed";

    /// <summary>A player character at 0 HP who is neither dead nor stable: the one kind of downed character who still gets a combat turn (a death save).</summary>
    public static bool IsDying(Character character) =>
        !character.IsDead && character.CurrentHp <= 0
        && (Pf2eDying.Applies(character)
            ? Pf2eDying.Get(character, Pf2eDying.Dying) > 0
            : UsesDeathSaves(character) && character.DeathSaves is not { Stable: true });

    /// <summary>
    /// Keeps the Unconscious status honest: a living player character at 0 HP carries it (so the action gate stops them acting
    /// in and out of combat), and rising above 0 removes it. Only the engine's own status (<see cref="DownedEffectKey"/>) is
    /// ever removed. The dead are not flagged: death is its own state. Returns true when the status was added.
    /// </summary>
    public static bool SyncDowned(Character character)
    {
        if (character.SystemStats?.StatusEffects is not { } effects)
        {
            return false;
        }

        var marker = effects.FirstOrDefault(e => string.Equals(e.EffectKey, DownedEffectKey, StringComparison.Ordinal));
        var down = character.IsPc && !character.IsDead && character.CurrentHp <= 0;

        // PF2e: dying (which blocks actions) must not outlive 0 HP. Paths that set hit points directly (a revival, a legacy
        // upsert) end up here; ones that heal through Set have already turned it into wounded.
        if (character.CurrentHp > 0 && Pf2eDying.Applies(character))
        {
            Pf2eDying.Set(character, Pf2eDying.Dying, 0);
        }

        if (down && marker is null)
        {
            effects.Add(new StatusEffect
            {
                Name = "Unconscious",
                Category = "Condition",
                ConditionName = "unconscious",
                EffectKey = DownedEffectKey,
                RecoveryHint = "Applied by the engine at 0 HP; removed when hit points rise above 0.",
            });
            // They fall prone as well; unlike the Unconscious flag it stays when they wake.
            if (!effects.Any(e => string.Equals(e.ConditionName ?? e.Name, "prone", StringComparison.OrdinalIgnoreCase)))
            {
                effects.Add(new StatusEffect { Name = "Prone", Category = "Condition", ConditionName = "prone" });
            }

            return true;
        }

        if (!down && marker is not null)
        {
            effects.Remove(marker);
        }

        return false;
    }

    /// <summary>Sets the hit points (clamped to 0..MaxHp, or to <paramref name="effectiveMax"/> when a condition lowers the maximum) and fixes the tally. Callers say what happened; this stays silent.</summary>
    public static HpTransition Set(Character character, int newHp, int? effectiveMax = null)
    {
        var before = character.CurrentHp;
        character.CurrentHp = Math.Clamp(newHp, 0, Math.Max(0, Math.Min(character.MaxHp, effectiveMax ?? character.MaxHp)));
        var after = character.CurrentHp;

        var recovered = after > 0 && (before <= 0 || character.DeathSaves != null);
        if (after > 0)
        {
            character.DeathSaves = null;
        }

        var wentDown = before > 0 && after == 0;
        if (wentDown && UsesDeathSaves(character))
        {
            character.DeathSaves = new DeathSaveTally();
        }

        int? wounded = null;
        if (Pf2eDying.Applies(character))
        {
            if (after > 0)
            {
                wounded = Pf2eDying.Recover(character);
            }
            else if (wentDown)
            {
                Pf2eDying.Set(character, Pf2eDying.Dying, Pf2eDying.ValueWhenDowned(character, critical: false));
            }
        }

        SyncDowned(character);
        return new HpTransition(wentDown, recovered && before <= 0, wounded);
    }
}
