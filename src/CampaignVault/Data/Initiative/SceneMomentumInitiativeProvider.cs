using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

/// <summary>
/// Surfaces a "hasn't acted on their own in a while" candidate purely from Character.IdleSceneBeats
/// (tracked by WorldChangeDispatcher.ApplyMomentumTracking) — independent of need/relational/memory/
/// disposition state, which the other providers key off of. This is the only driver that responds to
/// "several beats of banter went by with no state change" rather than "this NPC has unfinished business
/// from before this scene": a companion at neutral relationship, no pressing need, and no notable memory
/// still accrues idle-beat pressure just from standing in the scene without a verb of their own.
///
/// Scoped to party companions and keepAlive NPCs — transient background characters aren't expected to
/// carry their own agenda into a scene the way a recurring companion is.
///
/// Threshold comparisons use MomentumTraitScoring.EffectiveIdleBeats rather than the raw counter, so an
/// extraverted/impulsive NPC crosses the threshold sooner and an introverted/reserved one holds back
/// longer for the same number of actual idle beats. The framing prompt still reports the raw count.
/// </summary>
public sealed class SceneMomentumInitiativeProvider : INpcInitiativeSignalProvider
{
    public IReadOnlyList<InitiativeCandidate> GetCandidates(NpcInitiativeContext ctx)
    {
        var npc = ctx.Npc;
        if (!npc.IsPartyCompanion && !npc.KeepAlive)
        {
            return [];
        }

        if (!ctx.PresentEntities.Any(e => e.IsPc))
        {
            // No point surfacing "acts unprompted" for a companion with nobody to act in front of.
            return [];
        }

        var rawIdleBeats = npc.IdleSceneBeats;
        var idleBeats = MomentumTraitScoring.EffectiveIdleBeats(rawIdleBeats, npc.Psychology?.Traits);
        var normalThreshold = ctx.Config.MomentumIdleBeatsNormalThreshold;
        if (idleBeats < normalThreshold)
        {
            return [];
        }

        var highThreshold = ctx.Config.MomentumIdleBeatsHighThreshold;
        var urgency = idleBeats >= highThreshold ? MemoryUrgency.High : MemoryUrgency.Normal;
        var weight = Math.Clamp(30 + (idleBeats - normalThreshold) * 10, 30, 90);

        return
        [
            new InitiativeCandidate(
                $"momentum:{npc.Id}",
                npc.Id,
                InitiativeDriver.Momentum,
                urgency,
                $"Has gone {rawIdleBeats} beat(s) without acting on their own — may change the subject, wander off to do something, offer a gesture, or otherwise act unprompted.",
                weight)
        ];
    }
}
