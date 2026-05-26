using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

public class WorldSimulator
{
    public List<string> Run(CampaignTime time, List<Rumor> rumors, List<Character> npcs, double daysPassed)
    {
        var events = new List<string>();

        // 1. NPC Needs & Mood
        foreach (var npc in npcs)
        {
            if (npc.Mind == null) continue;

            // Scale accumulation by time passed (base rate: 10 units per day)
            var amount = (float)(10.0 * daysPassed);
            
            UpdateNeed(npc, "hunger", amount);
            UpdateNeed(npc, "thirst", amount * 1.2f); // Thirst grows faster
            UpdateNeed(npc, "tiredness", amount * 0.8f);

            // Mood evaluation based on needs
            var hunger = npc.Mind.Needs.GetValueOrDefault("hunger");
            var tiredness = npc.Mind.Needs.GetValueOrDefault("tiredness");

            if (tiredness > 80) npc.Mind.CurrentMood = "Exhausted";
            else if (hunger > 70) npc.Mind.CurrentMood = "Ravenous";
            else if (hunger > 40 || tiredness > 40) npc.Mind.CurrentMood = "Grumpy";
            else npc.Mind.CurrentMood = "Content";
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

    private void UpdateNeed(Character c, string need, float amount)
    {
        var current = c.Mind.Needs.GetValueOrDefault(need, 0f);
        c.Mind.Needs[need] = Math.Clamp(current + amount, 0f, 100f);
    }
}
