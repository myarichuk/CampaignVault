using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public sealed class NeedActivityConflictProvider : INpcInitiativeSignalProvider
{
    public IReadOnlyList<InitiativeCandidate> GetCandidates(NpcInitiativeContext ctx)
    {
        var (hasConflict, need, _) = NeedActivityConflictHelper.Detect(ctx.Npc, ctx.Config);
        if (!hasConflict || string.IsNullOrWhiteSpace(need))
        {
            return [];
        }

        var activity = ctx.Npc.CurrentActivity ?? "current duties";
        var needValue = ctx.Npc.Needs?.ActiveNeeds.GetValueOrDefault(need, 0f) ?? 0f;
        var urgency = needValue >= 85 ? MemoryUrgency.High : MemoryUrgency.Normal;
        var weight = Math.Clamp(needValue * 0.8, 40, 90);

        var adjective = DeriveAdjective(need);
        var framing = GetFraming(need, activity, adjective);

        return
        [
            new InitiativeCandidate(
                $"need:{ctx.Npc.Id}:{need}",
                ctx.Npc.Id,
                InitiativeDriver.Need,
                urgency,
                framing,
                weight)
        ];
    }

    private static string GetFraming(string need, string activity, string adjective)
    {
        return need.ToLower() switch
        {
            "tiredness" => $"Exhausted but still {activity} — may slip, snap, or ask for help.",
            "hunger" => $"Ravenous while {activity} — distracted, irritable, or fixated on food.",
            "thirst" => $"Parched while {activity} — strained voice, impatience, or frequent pauses.",
            "bloodlust" => $"Bloodthirsty while {activity} — fixated on combat, spoiling for violence.",
            "paranoia" => $"Paranoid while {activity} — seeing threats everywhere, trusting no one.",
            "obsession" => $"Consumed while {activity} — mind locked on obsession, barely present.",
            "despair" => $"Despairing while {activity} — going through motions, hollow and hopeless.",
            "guilt" => $"Guilt-ridden while {activity} — wracked with remorse, hesitant and withdrawn.",
            _ => $"{adjective} while {activity} — struggling to keep composure."
        };
    }

    private static string DeriveAdjective(string need)
    {
        return need.ToLower() switch
        {
            "paranoia" => "Paranoid",
            "wanderlust" => "Restless",
            "obsession" => "Obsessed",
            "hunger" => "Ravenous",
            "thirst" => "Parched",
            "tiredness" => "Exhausted",
            "greed" => "Greedy",
            "guilt" => "Guilt-ridden",
            "rage" => "Furious",
            "despair" => "Despairing",
            _ => TryApplySuffixRules(need)
        };
    }

    private static string TryApplySuffixRules(string need)
    {
        var lower = need.ToLower();

        // -ia → -iac (amnesia → amnesiac, insomnia → insomniac)
        if (lower.EndsWith("ia"))
            return Capitalize(lower[..^2] + "iac");

        // -tion → -ted (obsession → obsessed, ambition → ambitious)
        if (lower.EndsWith("tion"))
            return Capitalize(lower[..^4] + "ted");

        // -ure → -ured (exposure → exposed)
        if (lower.EndsWith("ure"))
            return Capitalize(lower[..^3] + "ured");

        // -ness → -ness (already an adjective descriptor, just capitalize)
        if (lower.EndsWith("ness"))
            return Capitalize(lower);

        // Default: assume it's a noun that can take -y or -ed
        return Capitalize(lower + "ed");
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
}