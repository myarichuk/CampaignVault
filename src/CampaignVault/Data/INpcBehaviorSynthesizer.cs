using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Responsible for turning raw NPC state (Mind, Schedule, recent events, simulation state)
/// into a concise, narrative-ready behavioral summary for the LLM DM.
/// 
/// This is a key part of delivering the V4 promise of "Behavioral Prompts over Raw Data".
/// The first implementation is purely deterministic and template-based (cheap + predictable).
/// It can later be swapped for an LLM-backed implementation behind the same interface.
/// </summary>
public interface INpcBehaviorSynthesizer
{
    string GenerateSummary(Character npc, CampaignTime? currentTime = null, IEnumerable<Event>? recentEvents = null);
}
