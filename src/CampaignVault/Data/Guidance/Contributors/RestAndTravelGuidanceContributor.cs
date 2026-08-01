using CampaignVault.Data.Pressure;

namespace CampaignVault.Data.Guidance.Contributors;

/// <summary>
/// Delivers guidance on first rest or travel commit, explaining how to use those tools.
/// </summary>
internal sealed class RestAndTravelGuidanceContributor : IGuidanceContributor
{
    public PressureScope Scope => PressureScope.World;
    public int Order => 6;

    public async Task<IEnumerable<GuidanceHint>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        await Task.CompletedTask;

        // Check if campaign is past the very start (has time advancement)
        if (ctx.Time == null || ctx.Time.TotalDaysElapsed < 1)
            return [];

        return new[]
        {
            new GuidanceHint(
                Key: "patterns.wilderness-transients",
                Text: "Use travel and rest to advance time and trigger state decay (memories fade, rumors age, transient NPCs evict). These provide natural pacing between major scenes.",
                Trigger: GuidanceTrigger.RestAndTravel,
                Priority: 6)
            {
                Example = """{"$type": "travel", "route": "road to capital", "minutesElapsed": 480}"""
            }
        };
    }
}
