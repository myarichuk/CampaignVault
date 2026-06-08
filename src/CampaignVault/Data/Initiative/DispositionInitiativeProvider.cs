using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public sealed class DispositionInitiativeProvider : INpcInitiativeSignalProvider
{
    public IReadOnlyList<InitiativeCandidate> GetCandidates(NpcInitiativeContext ctx)
    {
        var psych = ctx.Npc.Psychology ?? new PsychologyProfile();
        var (_, _, dispositionStress) = DispositionMatcher.Score(
            psych,
            ctx.PresentEntities,
            ctx.Location,
            ctx.Config);

        if (dispositionStress < 20)
        {
            return [];
        }

        var matchedFears = DispositionMatcher.GetMatchedFears(
            psych,
            ctx.PresentEntities,
            ctx.Location,
            ctx.Config);

        if (matchedFears.Count == 0)
        {
            return [];
        }

        var candidates = new List<InitiativeCandidate>();
        foreach (var fear in matchedFears)
        {
            var sceneHint = ctx.Location?.VisualTags.FirstOrDefault()
                            ?? ctx.Location?.AmbientCrowd
                            ?? "the scene";
            candidates.Add(new InitiativeCandidate(
                $"disposition:{ctx.Npc.Id}:{fear}",
                ctx.Npc.Id,
                InitiativeDriver.Disposition,
                dispositionStress >= 50 ? MemoryUrgency.High : MemoryUrgency.Normal,
                $"Fear of {fear} meets cues in {sceneHint} — may withdraw, cling to a familiar face, or seek reassurance.",
                dispositionStress * 0.9));
        }

        return candidates;
    }
}