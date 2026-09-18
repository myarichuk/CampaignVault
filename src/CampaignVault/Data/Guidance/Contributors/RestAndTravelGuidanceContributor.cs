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
                Text: "Use travel and rest to advance time — hunger/thirst/social_drive accrue automatically for the hours spent, and the full state-decay tick (memories fade, rumors age, transient NPCs evict, climate exposure, faction/plot evolution) fires immediately afterward, regardless of whether the hours crossed midnight. Natural pacing between major scenes. Custom needs you've invented (e.g. 'homesickness', 'bloodlust') never move on their own — the engine doesn't know their meaning — so commit an explicit need change for them yourself when a long journey or rest narratively warrants it.",
                Trigger: GuidanceTrigger.RestAndTravel,
                Priority: 6)
            {
                Example = """{"$type": "travel", "characterId": "chars/grog", "destinationLocationId": "locs/capital-gate"}"""
            }
        };
    }
}
