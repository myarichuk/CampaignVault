using CampaignVault.Models;

namespace CampaignVault.Rulesets;

public static class EngagementMutationHelper
{
    public const string GrapplingVerb = "Grappling";

    public static bool IsGrappleAction(RulesetAction action) =>
        action.ActionName.Contains("grapple", StringComparison.OrdinalIgnoreCase)
        || (TryGetParameter(action.Parameters, "maneuver", out var maneuver)
            && maneuver.Contains("grapple", StringComparison.OrdinalIgnoreCase));

    public static bool IsEscapeGrappleAction(RulesetAction action) =>
        action.ActionName.Contains("escape", StringComparison.OrdinalIgnoreCase)
        || (TryGetParameter(action.Parameters, "escape", out var escape)
            && (escape.Equals("true", StringComparison.OrdinalIgnoreCase) || escape == "1"));

    public static void ApplyGrappleSuccess(string characterId, string targetId, List<WorldChange> mutations)
    {
        mutations.Add(new EngagementRelationChange
        {
            CharacterId = characterId,
            TargetId = targetId,
            Category = EngagementCategory.Physical,
            Verb = GrapplingVerb,
            Bidirectional = true
        });
    }

    public static void ApplyGrappleEscape(string escapedId, string grapplerId, List<WorldChange> mutations)
    {
        mutations.Add(new EngagementRelationChange
        {
            CharacterId = escapedId,
            TargetId = grapplerId,
            Verb = null,
            Bidirectional = true
        });
    }

    private static bool TryGetParameter(Dictionary<string, string> parameters, string key, out string value)
    {
        if (parameters.TryGetValue(key, out value!))
            return true;

        value = string.Empty;
        return false;
    }
}