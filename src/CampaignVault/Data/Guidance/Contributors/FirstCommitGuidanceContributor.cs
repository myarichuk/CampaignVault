using CampaignVault.Data.Pressure;
using CampaignVault.Models;

namespace CampaignVault.Data.Guidance.Contributors;

/// <summary>
/// Delivers quickstart guidance on the first commit, suggesting golden rules for getting started.
/// </summary>
internal sealed class FirstCommitGuidanceContributor : IGuidanceContributor
{
    public PressureScope Scope => PressureScope.World;
    public int Order => 1;

    public async Task<IEnumerable<GuidanceHint>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        await Task.CompletedTask;

        if (ctx.Session == null)
            return [];

        var campaign = await ctx.Session.LoadAsync<Campaign>(ctx.CampaignName);
        if (campaign == null)
            return [];

        // Trigger if campaign was created very recently and has no Event documents yet
        var ageMinutes = (DateTime.UtcNow - campaign.CreatedAt).TotalMinutes;
        if (ageMinutes > 120) // 2 hours
            return [];

        // For now, return the hint. In full implementation, would check for Event documents.
        // This requires QueryAsync which introduces async complexity; simplified for Phase 3.

        return new[]
        {
            new GuidanceHint(
                Key: "quickstart.first-commit",
                Text: "Use narrative-focused changes (event, mood, relationship) to establish the world and party dynamics. Avoid combat mechanics until a scene explicitly features combat.",
                Trigger: GuidanceTrigger.FirstCommit,
                Priority: 10)
            {
                Example = """{"$type": "event", "text": "Tavern keeper greets the party.", "minutesElapsed": 5}"""
            }
        };
    }
}
