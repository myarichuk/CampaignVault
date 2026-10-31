using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Rulesets;

internal enum SpellResolutionMode
{
    Attack,
    Save,
    Check,
    Utility,
    Heal,
}

internal static class SpellResolutionHelper
{
    public const string ValidResolutions = "attack, save, check, heal or utility";

    /// <summary>
    /// Picks how a spell resolves. An explicit <c>resolution</c> wins; an unknown value fails closed (it used to
    /// fall back to utility, so a typo like "autohit" turned Magic Missile into a no-damage cast). Without one, the
    /// handbook entry decides when it carries mechanics (save type, damage, healing), then the caller's parameters.
    /// </summary>
    public static bool TryInferMode(RulesetAction action, SpellDefinition? spell, out SpellResolutionMode mode, out string? error)
    {
        error = null;
        if (TryGetParameter(action.Parameters, out var explicitMode, "resolution", "spellResolution"))
        {
            if (TryParseMode(explicitMode, out mode))
            {
                return true;
            }

            error = $"Error: '{action.ActionName}' has resolution '{explicitMode}', which is not one of: {ValidResolutions}. " +
                    "An auto-hit spell like Magic Missile is resolution=attack: the engine skips the attack roll when the handbook says it never misses. " +
                    "Omit resolution for a handbook spell and the engine picks it from the spell's data.";
            return false;
        }

        mode = FromSpellData(action, spell) ?? InferMode(action);
        return true;
    }

    /// <summary>
    /// Mode from the handbook entry, for a spell aimed at someone else; null when there is no such target or the entry
    /// carries nothing to go on. Untargeted casts keep the parameter heuristics: damage riders like Divine Favor and
    /// Branding Smite carry damage data but are self-buffs, not attacks. A spell-attack type beats a save type (Ray of
    /// Enfeeblement rolls to hit, then the save only shakes the effect off). Damage with neither is an attack: the
    /// attack path reads requiresAttackRoll, so an auto-hit spell like Magic Missile lands without a roll.
    /// </summary>
    public static SpellResolutionMode? FromSpellData(RulesetAction action, SpellDefinition? spell)
    {
        if (spell is null || spell.Summon is not null || spell.DamageIsPool == true
            || !action.TargetIds.Any(id => !string.Equals(id, action.CharacterId, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        // Ray of Enfeeblement and Contagion carry an attack type and a save type but no damage table: as attacks they
        // would fall back to the resolver's generic 1d4, and as saves they'd skip the attack roll. Infer nothing, so
        // the caller gets the "no mechanics" hint and states the resolution.
        if (!string.IsNullOrWhiteSpace(spell.AttackType))
        {
            return HasDamageData(spell) ? SpellResolutionMode.Attack : null;
        }

        if (!string.IsNullOrWhiteSpace(spell.SaveType))
        {
            return SpellResolutionMode.Save;
        }

        return HasDamageData(spell) ? SpellResolutionMode.Attack : null;
    }

    /// <summary>True when the handbook entry deals damage (single, per-instance or multi-pool).</summary>
    public static bool HasDamageData(SpellDefinition? spell) =>
        spell is not null && spell.DamageIsPool != true
        && (spell.DamageAtSlotLevel is { Count: > 0 }
            || spell.DamageAtCharacterLevel is { Count: > 0 }
            || spell.PerInstanceDamageAtSlotLevel is { Count: > 0 }
            || spell.PerInstanceDamageAtCharacterLevel is { Count: > 0 }
            || spell.DamagePools is { Count: > 0 });

    public static SpellResolutionMode InferMode(RulesetAction action)
    {
        if (TryGetParameter(action.Parameters, out var explicitMode, "resolution", "spellResolution"))
        {
            return TryParseMode(explicitMode, out var parsed) ? parsed : SpellResolutionMode.Utility;
        }

        if (action.Parameters.ContainsKey("healDice")
            || (action.Parameters.TryGetValue("damageDice", out var dmg) && dmg.StartsWith('-')))
        {
            return SpellResolutionMode.Heal;
        }

        if (action.Parameters.ContainsKey("save"))
        {
            return SpellResolutionMode.Save;
        }

        if (TryGetParameter(action.Parameters, out _, "bonus", "toHitBonus", "spellAttackBonus")
            || (action.Parameters.ContainsKey("damageDice") && !action.Parameters.ContainsKey("dc")))
        {
            return SpellResolutionMode.Attack;
        }

        if (action.Parameters.ContainsKey("dc")
            && TryGetParameter(action.Parameters, out _, "skill", "ability"))
        {
            return SpellResolutionMode.Check;
        }

        if (action.ActionCategory is ActionCategory.Social or ActionCategory.Survival)
        {
            return SpellResolutionMode.Utility;
        }

        if (action.Parameters.ContainsKey("dc"))
        {
            return SpellResolutionMode.Check;
        }

        return SpellResolutionMode.Utility;
    }

    public static bool IsNonCombatMode(SpellResolutionMode mode) =>
        mode is SpellResolutionMode.Check or SpellResolutionMode.Utility or SpellResolutionMode.Heal;

    public static bool RequiresTargets(SpellResolutionMode mode, RulesetAction action)
    {
        if (mode is SpellResolutionMode.Attack or SpellResolutionMode.Save or SpellResolutionMode.Heal)
        {
            return true;
        }

        return action.TargetIds.Count > 0;
    }

    private static bool TryParseMode(string value, out SpellResolutionMode mode)
    {
        SpellResolutionMode? parsed = value.Trim().ToLowerInvariant() switch
        {
            "attack" or "spellattack" or "spell_attack" => SpellResolutionMode.Attack,
            "save" or "savingthrow" or "saving_throw" or "spellsave" or "spell_save" => SpellResolutionMode.Save,
            "check" or "skillcheck" or "skill_check" => SpellResolutionMode.Check,
            "utility" or "narrative" or "exploration" or "social" => SpellResolutionMode.Utility,
            "heal" or "healing" or "recovery" => SpellResolutionMode.Heal,
            _ => null,
        };
        mode = parsed ?? SpellResolutionMode.Utility;
        return parsed.HasValue;
    }

    private static bool TryGetParameter(
        Dictionary<string, string> parameters,
        out string value,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (parameters.TryGetValue(key, out value!))
            {
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}