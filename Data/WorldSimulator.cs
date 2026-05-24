using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

public class WorldSimulator
{
    public List<string> Run(CampaignTime time, List<Rumor> rumors, List<Character> npcs)
    {
        var events = new List<string>();

        // 1. NPC Needs & Mood
        foreach (var npc in npcs)
        {
            if (npc.Mind == null) continue;

            // Simple accumulation
            UpdateNeed(npc, "fatigue", 1);
            if (npc.Mind.Needs.GetValueOrDefault("fatigue") > 50)
            {
                npc.Mind.CurrentMood = "Exhausted";
            }
        }

        // 2. Rumor Decay/Escalation
        foreach (var rumor in rumors)
        {
            if (rumor.State == RumorState.Resolved || rumor.State == RumorState.Forgotten) continue;

            var daysSinceUpdate = time.TotalDaysElapsed - rumor.LastStateChangeDay;
            if (daysSinceUpdate > 14) // Auto-fade after 2 weeks of silence
            {
                rumor.State = RumorState.Fading;
                rumor.LastStateChangeDay = time.TotalDaysElapsed;
                events.Add($"The rumor '{rumor.Subject}' is starting to fade from public memory.");
            }
        }

        return events;
    }

    private void UpdateNeed(Character c, string need, int amount)
    {
        if (!c.Mind.Needs.ContainsKey(need)) c.Mind.Needs[need] = 0;
        c.Mind.Needs[need] += amount;
    }
}
