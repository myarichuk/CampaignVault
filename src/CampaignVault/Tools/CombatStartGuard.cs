using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets;

namespace CampaignVault.Tools;

/// <summary>
/// A hostile <c>ruleset_action</c> committed while no combat is active starts a fight, so the commit is refused until
/// the DM starts one. Outside combat the handler skips turn order and action economy and just resolves the hit, so an
/// opening stab or Magic Missile at one of two goblins used to land while the other goblin never got a turn. The engine
/// doesn't start combat itself: who is in the fight and who is surprised are the DM's calls, and under initiative the
/// attacker acts on their own turn, which may not come first. Scoped to the take_turn boundary (the LLM's commits), so
/// engine-internal and test resolution through the handler is untouched.
/// </summary>
internal static class CombatStartGuard
{
    /// <summary>Opt-out for a strike that isn't a fight: a coup de grâce on a lone helpless target, a friendly spar.</summary>
    public const string OutsideCombatParameter = "outsideCombat";

    public static string? FindConflict(
        IEnumerable<WorldChange> changes, bool combatActive, Func<RulesetAction, SpellDefinition?> lookupSpell)
    {
        if (combatActive)
        {
            return null;
        }

        foreach (var change in changes)
        {
            if (change is not RulesetAction action || !IsHostile(action, lookupSpell))
            {
                continue;
            }

            var targets = string.Join(", ", OtherTargets(action));
            return $"[CombatNotStarted] '{action.ActionName}' by {action.CharacterId} against {targets} is a hostile action, and no combat is active. " +
                   "Start the fight first: combat(action: \"start\", locationId, combatantIds: [the attacker, the targets and every other creature " +
                   "present who would join or flee], surprisedIds: [anyone who had not noticed the threat]). Initiative decides who acts first; " +
                   "commit this action on the attacker's turn. Each NPC then chooses on its own turn to fight, flee or surrender. " +
                   $"For a strike that isn't a fight (a coup de grâce on a lone helpless target, a friendly spar), pass parameters.{OutsideCombatParameter}: \"true\".";
        }

        return null;
    }

    /// <summary>An attack, a grapple or shove, or an attack/save spell aimed at someone other than the actor.</summary>
    internal static bool IsHostile(RulesetAction action, Func<RulesetAction, SpellDefinition?> lookupSpell)
    {
        if (!OtherTargets(action).Any()
            || (action.Parameters.TryGetValue(OutsideCombatParameter, out var optOut)
                && (optOut.Equals("true", StringComparison.OrdinalIgnoreCase) || optOut == "1")))
        {
            return false;
        }

        return action.ActionType switch
        {
            RulesetActionType.Attack => true,
            RulesetActionType.ContestedCheck or RulesetActionType.OpposedCheck =>
                EngagementMutationHelper.IsGrappleAction(action)
                || action.ActionName.Contains("shove", StringComparison.OrdinalIgnoreCase)
                || (action.Parameters.TryGetValue("maneuver", out var maneuver) && maneuver.Contains("shove", StringComparison.OrdinalIgnoreCase)),
            RulesetActionType.Spell => IsHostileSpell(action, lookupSpell(action)),
            _ => false,
        };
    }

    /// <summary>
    /// A spell attack is hostile; a save spell only when it deals damage. Charm Person on a shopkeeper or Suggestion on a
    /// guard is a save spell cast in conversation, not the start of a fight.
    /// </summary>
    private static bool IsHostileSpell(RulesetAction action, SpellDefinition? spell)
    {
        if (!SpellResolutionHelper.TryInferMode(action, spell, out var mode, out _))
        {
            return false;
        }

        return mode switch
        {
            SpellResolutionMode.Attack => true,
            SpellResolutionMode.Save => SpellResolutionHelper.HasDamageData(spell) || action.Parameters.ContainsKey("damageDice"),
            _ => false,
        };
    }

    private static IEnumerable<string> OtherTargets(RulesetAction action) =>
        action.TargetIds.Where(id => !string.IsNullOrWhiteSpace(id)
                                     && !string.Equals(id, action.CharacterId, StringComparison.OrdinalIgnoreCase));
}
