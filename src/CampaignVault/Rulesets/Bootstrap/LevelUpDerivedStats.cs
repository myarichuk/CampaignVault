using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Bootstrap;

/// <summary>
/// What a level-up changes in numbers derived from the ability scores: hit points from Constitution (retroactively, for
/// the levels already taken) and armor class from Dexterity, Constitution or Wisdom, or from the level in PF2e. Each is
/// measured as a change, before and after the picks and the level, so a figure the DM stated (a stat block's armor class
/// or hit points) moves by the same amount instead of being replaced by a formula.
/// </summary>
internal static class LevelUpDerivedStats
{
    public static int ArmorClassOf(Character character) => character.SystemStats switch
    {
        Dnd5eExtension d => d.ArmorClass,
        Pf2eExtension p => p.ArmorClass,
        _ => 0,
    };

    public static void SetArmorClass(Character character, int value)
    {
        switch (character.SystemStats)
        {
            case Dnd5eExtension d: d.ArmorClass = value; break;
            case Pf2eExtension p: p.ArmorClass = value; break;
        }
    }

    public static int ConstitutionModifier(Character character) => character.SystemStats switch
    {
        Dnd5eExtension d => d.GetAbilityModifier(d.Constitution),
        Pf2eExtension p => p.ConstitutionMod,
        _ => 0,
    };

    /// <summary>
    /// Maximum hit points for a Constitution modifier that changed (SRD: the maximum rises or falls with it, for every
    /// level). <paramref name="levels"/> is the levels the change covers: those already taken, since the level being gained
    /// already used the new modifier. Returns the hit points added or removed. Skipped under a stated stat-block maximum.
    /// </summary>
    public static int ApplyRetroactiveConstitution(Character character, int modifierBefore, int levels)
    {
        if (levels <= 0 || character.SystemStats is null or { StatBlockHp: > 0 })
        {
            return 0;
        }

        var delta = ConstitutionModifier(character) - modifierBefore;
        if (delta == 0)
        {
            return 0;
        }

        var newMax = Math.Max(1, character.MaxHp + delta * levels);
        var applied = newMax - character.MaxHp;
        character.MaxHp = newMax;
        if (character.CurrentHp > character.MaxHp)
        {
            HitPointState.Set(character, character.MaxHp);
        }

        return applied;
    }

    /// <summary>The armor class the equipped gear and the current scores and level give, leaving the stored figure as it was.</summary>
    public static async Task<int> DerivedArmorClassAsync(
        Character character, IBootstrapEquipmentAccess? equipment, ProgressionDefinitionProvider? progressions, CancellationToken ct)
    {
        if (character.SystemStats is null)
        {
            return 0;
        }

        var stored = ArmorClassOf(character);
        var warmth = character.SystemStats.WarmthRating;
        var movement = character.SystemStats.MovementModifier;
        var items = equipment is null ? [] : await equipment.GetEquippedItemsAsync(character.Id, ct);
        ArmorParameterResolver.Apply(character, items, progressions);
        var derived = ArmorClassOf(character);
        SetArmorClass(character, stored);
        character.SystemStats.WarmthRating = warmth;
        character.SystemStats.MovementModifier = movement;
        return derived;
    }

    /// <summary>A PF2e caster's stored spell DC and attack, and the DC the formula gives now. Null for anyone else.</summary>
    internal sealed record Pf2eSpellcastingSnapshot(int? StoredDc, int? StoredAttack, int? DerivedDc);

    public static Pf2eSpellcastingSnapshot? SnapshotPf2eSpellcasting(Character character) =>
        character.SystemStats is Pf2eExtension p
            ? new Pf2eSpellcastingSnapshot(p.SpellDc, p.SpellAttackBonus, Pf2eDeriveSpellcastingStep.ComputeSpellDc(p))
            : null;

    /// <summary>
    /// The bootstrap fills a spell DC once and never refreshes it, so a stored one would freeze at the level it was first
    /// derived. Moves it, and the attack, by the amount the formula moved. Returns the message, or null when nothing changed.
    /// </summary>
    public static string? ApplyPf2eSpellcastingDelta(Character character, Pf2eSpellcastingSnapshot? before)
    {
        if (before is not { StoredDc: { } storedDc, DerivedDc: { } derivedBefore } || character.SystemStats is not Pf2eExtension p
            || Pf2eDeriveSpellcastingStep.ComputeSpellDc(p) is not { } derivedAfter || derivedAfter == derivedBefore)
        {
            return null;
        }

        var delta = derivedAfter - derivedBefore;
        p.SpellDc = storedDc + delta;
        p.SpellAttackBonus = before.StoredAttack is { } attack ? attack + delta : p.SpellDc - 10;
        return $"Spell DC {storedDc} → {p.SpellDc}, spell attack {p.SpellAttackBonus:+#;-#;+0}.";
    }
}
