using CampaignVault.Models;

namespace CampaignVault.Rulesets;

public static class EngagementMutationHelper
{
    public const string GrapplingVerb = "Grappling";

    /// <summary>
    /// A grapple: the action is named for it or says so in <c>maneuver</c>. "Grapple", "Grab" and "Seize" all count; "Trip" and
    /// "Shove" don't.
    /// </summary>
    public static bool IsGrappleAction(RulesetAction action) =>
        action.ActionName.Contains("grapple", StringComparison.OrdinalIgnoreCase)
        || (TryGetParameter(action.Parameters, "maneuver", out var maneuver)
            && maneuver.Contains("grapple", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Breaking out of a grapple. A bare "escape" in the name (escape the room, escape notice) is not enough: the name must also
    /// speak of the grapple, or the caller states it (<c>escape: true</c> or <c>maneuver: escape</c>).
    /// </summary>
    public static bool IsEscapeGrappleAction(RulesetAction action) =>
        (action.ActionName.Contains("escape", StringComparison.OrdinalIgnoreCase)
            && (action.ActionName.Contains("grapple", StringComparison.OrdinalIgnoreCase)
                || action.ActionName.Contains("grab", StringComparison.OrdinalIgnoreCase)
                || action.ActionName.Contains("hold", StringComparison.OrdinalIgnoreCase)))
        || (TryGetParameter(action.Parameters, "escape", out var escape)
            && (escape.Equals("true", StringComparison.OrdinalIgnoreCase) || escape == "1"))
        || (TryGetParameter(action.Parameters, "maneuver", out var maneuver)
            && maneuver.Contains("escape", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The grappler holds the target (the engagement relation) and the target is Grappled (a condition, so its speed is 0). The
    /// condition records who applied it, so the grapple can end with the grappler. PF2e names it <c>Grabbed</c>.
    /// </summary>
    public static void ApplyGrappleSuccess(string characterId, string targetId, List<WorldChange> mutations, string conditionName = GrappledCondition)
    {
        mutations.Add(new EngagementRelationChange
        {
            CharacterId = characterId,
            TargetId = targetId,
            Category = EngagementCategory.Physical,
            Verb = GrapplingVerb,
            Bidirectional = true
        });
        mutations.Add(new StatusChange
        {
            CharacterId = targetId,
            Effect = new StatusEffect
            {
                Name = char.ToUpperInvariant(conditionName[0]) + conditionName[1..],
                Category = "Condition",
                ConditionName = conditionName,
                AppliedBy = characterId,
            },
        });
    }

    public static void ApplyGrappleEscape(string escapedId, string grapplerId, List<WorldChange> mutations, string conditionName = GrappledCondition)
    {
        mutations.Add(new EngagementRelationChange
        {
            CharacterId = escapedId,
            TargetId = grapplerId,
            Verb = null,
            Bidirectional = true
        });
        mutations.Add(new StatusRemove { CharacterId = escapedId, Status = char.ToUpperInvariant(conditionName[0]) + conditionName[1..] });
    }

    /// <summary>The condition a grapple puts on its target (dnd5e); see <see cref="ApplyGrappleSuccess"/> for PF2e.</summary>
    public const string GrappledCondition = "grappled";

    public const string GrabbedCondition = "grabbed";

    /// <summary>A PF2e grapple's critical success: restrained, which implies grabbed and off-guard.</summary>
    public const string RestrainedCondition = "restrained";

    /// <summary>
    /// Ends every grapple <paramref name="grappler"/> holds: the relation on both sides and the Grappled or Grabbed condition it
    /// applied. Called when the grappler is incapacitated. Returns the names of those let go.
    /// </summary>
    public static List<string> ReleaseHeldBy(Character grappler, Func<string, Character?> find)
    {
        var released = new List<string>();
        if (grappler.SystemStats is null)
            return released;

        var held = grappler.SystemStats.EngagementRelations
            .Where(r => string.Equals(r.Verb, GrapplingVerb, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var relation in held)
        {
            grappler.SystemStats.EngagementRelations.Remove(relation);
            if (find(relation.TargetId) is not { } target)
                continue;

            target.SystemStats?.EngagementRelations.RemoveAll(r => r.TargetId == grappler.Id);
            target.SystemStats?.StatusEffects.RemoveAll(e =>
                string.Equals(e.AppliedBy, grappler.Id, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(e.ConditionName, GrappledCondition, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.ConditionName, GrabbedCondition, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.ConditionName, RestrainedCondition, StringComparison.OrdinalIgnoreCase)));
            released.Add(target.Name);
        }

        return released;
    }

    private static bool TryGetParameter(Dictionary<string, string> parameters, string key, out string value)
    {
        if (parameters.TryGetValue(key, out value!))
            return true;

        value = string.Empty;
        return false;
    }
}