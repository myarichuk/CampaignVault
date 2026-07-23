using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

/// <summary>
/// Surfaces a "hasn't spoken up in a while" candidate for party companions specifically — distinct
/// from NeedActivityConflictProvider's generic social_drive-need pressure and RelationalInitiativeProvider's
/// reactive gratitude/affection beats, neither of which tracks plain conversational staleness. Only
/// fires when a PC is actually present (no point wanting to talk to nobody) and a prior Conversation
/// event exists for this companion — a companion with no conversation history yet doesn't fire on day
/// one, since we can't distinguish "never talked" from "just outside the retrieval window."
/// </summary>
public sealed class ConversationStalenessInitiativeProvider : INpcInitiativeSignalProvider
{
    public IReadOnlyList<InitiativeCandidate> GetCandidates(NpcInitiativeContext ctx)
    {
        var npc = ctx.Npc;
        if (!npc.IsPartyCompanion)
        {
            return [];
        }

        var presentPcs = ctx.PresentEntities.Where(e => e.IsPc).ToList();
        if (presentPcs.Count == 0)
        {
            return [];
        }

        var lastConversationDay = ctx.NpcRecentEvents
            .Where(e => e.Category == EventCategory.Conversation)
            .Select(e => (int?)e.DayLogged)
            .Max();

        if (lastConversationDay is null)
        {
            return [];
        }

        var daysSince = ctx.CurrentDay - lastConversationDay.Value;
        if (daysSince < ctx.Config.ConversationStalenessDaysThreshold)
        {
            return [];
        }

        var social = npc.Social ?? new SocialProfile();
        var bestRelationship = presentPcs
            .Select(pc => social.Relationships.GetValueOrDefault(pc.Id, 0))
            .Max();
        if (bestRelationship < 0)
        {
            // Negative relationship with every present PC — staying quiet is more plausible
            // than volunteering conversation.
            return [];
        }

        var urgency = daysSince >= ctx.Config.ConversationStalenessDaysThreshold * 2
            ? MemoryUrgency.High
            : MemoryUrgency.Normal;
        var weight = Math.Clamp(40 + (daysSince - ctx.Config.ConversationStalenessDaysThreshold) * 10, 40, 80);

        return
        [
            new InitiativeCandidate(
                $"staleness:{npc.Id}",
                npc.Id,
                InitiativeDriver.Relational,
                urgency,
                $"Hasn't spoken with the party in {daysSince} day(s) — may bring up something on their mind, ask how the PC is doing, or share an observation unprompted.",
                weight)
        ];
    }
}
