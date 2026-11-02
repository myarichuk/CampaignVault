using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;

namespace CampaignVault.Data.Impressions;

/// <summary>
/// What the rules say about an illusion once it's cast (IMPRESSIONS_PLAN.md I3): which form it takes, what it can't do, and the
/// check that sees through it. 5e: "a creature that uses its action to examine" it makes an Intelligence (Investigation) check
/// against the spell save DC (SRD 5.1, Minor Illusion, Silent Image, Major Image). PF2e: a creature that interacts with it or
/// Seeks attempts to disbelieve, a Perception check against the spell DC (Player Core, illusions).
/// </summary>
public static class IllusionRules
{
    public static bool IsIllusion(SpellDefinition? spell) =>
        spell?.IllusionLimits != null || string.Equals(spell?.School, "illusion", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A figment: an illusion that is believed until someone examines it. Not every illusion-school spell is one: Fear and
    /// Hypnotic Pattern are resolved by a saving throw, not seen through with Investigation. The quoted limits mark the figments;
    /// a homebrew illusion without them counts when it has no save.
    /// </summary>
    public static bool IsFigment(SpellDefinition? spell) =>
        spell?.IllusionLimits != null
        || (string.Equals(spell?.School, "illusion", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(spell?.SaveType));

    public static string DisbeliefSkill(string? system) =>
        string.Equals(system, RulesetSystem.Pathfinder2e, StringComparison.OrdinalIgnoreCase) ? "Perception" : "Investigation";

    /// <summary>The caster's spell DC from the sheet (5e: 8 + proficiency + ability, unless recorded), or null when there's none.</summary>
    public static int? CasterDc(Character? caster)
    {
        switch (caster?.SystemStats)
        {
            case Dnd5eExtension five:
            {
                var ability = five.SpellcastingAbility ?? Dnd5eSpellcastingHelper.InferSpellcastingAbility(five.ClassLevels);
                if (five.SpellSaveDc is not > 0 && ability == null)
                {
                    return null;
                }

                return Dnd5eSpellcastingHelper.ComputeSpellSaveDc(five, Dnd5eClassProfileResolver.ProficiencyBonus(five.Level ?? 1), ability ?? "Intelligence");
            }
            case Pf2eExtension pf2:
                return pf2.SpellDc is > 0 ? pf2.SpellDc : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// A spell with caster-chosen forms takes exactly one per cast (SRD 5.1 Minor Illusion: "a sound or an image"; Player Core
    /// Figment: "a simple illusory sound or vision"). Returns the refusal, or null when the mode is one of them.
    /// </summary>
    public static string? ModeRefusal(SpellDefinition? spell, RulesetAction action)
    {
        if (spell?.PerceptibleModes is not { Count: > 0 } modes)
        {
            return null;
        }

        var mode = action.Parameters.GetValueOrDefault("mode")?.Trim();
        if (!string.IsNullOrEmpty(mode) && modes.Keys.Any(k => string.Equals(k, mode, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var forms = string.Join(" or ", modes.Keys.Order(StringComparer.OrdinalIgnoreCase));
        return $"{action.ActionName} makes one form per cast, so it needs parameters.mode: {forms}"
               + (string.IsNullOrEmpty(mode) ? "." : $" ('{mode}' isn't one).")
               + " A sound and an image at once is two casts, or a bigger illusion (Major Image, Illusory Creature).";
    }

    /// <summary>
    /// F3: what an onlooker can perceive of the casting itself. 5e (SRD 5.1 Components): a verbal component is heard, a somatic
    /// or material one seen. PF2e (Player Core): every spell is cast with observable manifestations unless it has the subtle trait.
    /// A subtle cast (<c>parameters.subtle</c>: Subtle Spell, Conceal Spell) shows nothing.
    /// </summary>
    public static (bool Seen, bool Heard) CastingShows(SpellDefinition? spell, RulesetAction action, string? system)
    {
        if (string.Equals(action.Parameters.GetValueOrDefault("subtle")?.Trim(), "true", StringComparison.OrdinalIgnoreCase) || spell?.Subtle == true)
        {
            return (false, false);
        }

        if (string.Equals(system, RulesetSystem.Pathfinder2e, StringComparison.OrdinalIgnoreCase))
        {
            return (true, true);
        }

        return spell is null ? (true, false) : (spell.Somatic == true || spell.Material == true, spell.Verbal == true);
    }

    /// <summary>The narrator's reminder after the cast: the spell's own limits and how to see through it.</summary>
    public static string? LimitsNote(SpellDefinition? spell, Character? caster, string? system, string actionName)
    {
        if (spell?.IllusionLimits is not { Length: > 0 } limits)
        {
            return null;
        }

        var dc = CasterDc(caster);
        return $"ILLUSION LIMITS ({actionName}, rules text): {limits} Narrate only what this allows. "
               + $"Seeing through it: an observer who examines it makes {DisbeliefSkill(system)} vs DC {(dc?.ToString() ?? "the caster's spell DC")}"
               + " (a skill_check with parameters.disbelieve = the caster's id).";
    }
}
