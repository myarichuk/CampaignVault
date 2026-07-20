using CampaignVault.Models;

namespace CampaignVault.Tools;

/// <summary>
/// Detects the one double-application pattern the DM manual repeatedly and explicitly warns
/// against: a `ruleset_action` in the same commit batch as a manual `hp`/`status`/
/// `engagement_relation` change targeting the same character it already auto-applies to.
/// Deliberately scoped to only this pattern — other side-effect-marked $types (rest, travel,
/// quest_progress, plot_thread_clue) declare "event" as a side effect, but the manual actively
/// *recommends* pairing those with a manual `event` commit, so flagging that pairing would be a
/// false positive rather than a real bug.
/// </summary>
internal static class SideEffectDuplicationGuard
{
    public static string? FindConflict(WorldChange[] changes)
    {
        foreach (var change in changes)
        {
            if (change is not RulesetAction action)
            {
                continue;
            }

            var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { action.CharacterId };
            foreach (var targetId in action.TargetIds)
            {
                affected.Add(targetId);
            }

            foreach (var other in changes)
            {
                if (ReferenceEquals(other, change))
                {
                    continue;
                }

                switch (other)
                {
                    case HpChange hp when affected.Contains(hp.CharacterId):
                        return $"'ruleset_action' ({action.ActionName}) on {action.CharacterId} already auto-applies 'hp' to {hp.CharacterId} — " +
                               $"remove the separate hp change for {hp.CharacterId} (or split it into a second commit if it's an unrelated adjustment).";
                    case StatusChange sc when affected.Contains(sc.CharacterId):
                        return $"'ruleset_action' ({action.ActionName}) on {action.CharacterId} already auto-applies 'status' to {sc.CharacterId} — " +
                               $"remove the separate status change for {sc.CharacterId} (or split it into a second commit if it's an unrelated adjustment).";
                    case EngagementRelationChange erc when affected.Contains(erc.CharacterId) || affected.Contains(erc.TargetId):
                        return $"'ruleset_action' ({action.ActionName}) on {action.CharacterId} already auto-applies 'engagement_relation' — " +
                               $"remove the separate engagement_relation change between {erc.CharacterId} and {erc.TargetId} (or split it into a second commit if it's an unrelated adjustment).";
                    case SceneSetupChange ssc when ssc.Engagement is not null && (affected.Contains(ssc.CharacterId) || affected.Contains(ssc.TargetId)):
                        return $"'ruleset_action' ({action.ActionName}) on {action.CharacterId} already auto-applies 'engagement_relation' — " +
                               $"remove the scene_setup engagement between {ssc.CharacterId} and {ssc.TargetId} (or split it into a second commit if it's an unrelated adjustment).";
                }
            }
        }

        return null;
    }
}
