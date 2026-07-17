using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

/// <summary>
/// Fires when a PC's current location has a materially different computed temperature than their
/// WarmthRating suggests they're dressed for, nudging the DM-LLM to narrate discomfort or flag a
/// gear mismatch. No TravelChangeHandler edit needed — this rides the existing pressure pipeline
/// that already surfaces on every commit response for the requested scene location.
/// </summary>
public sealed class ClimateShiftPressureContributor : IPressureContributor
{
    public const string GroupingKey = "Climate:GearMismatch";

    // "Comfortable" reference felt-temperature (reasonably dressed, no extra insulation) and how far
    // from it counts as materially different before nudging the DM-LLM.
    private const float ComfortableFeltTemp = 18f;
    private const float MismatchThreshold = 15f;

    public PressureScope Scope => PressureScope.Scene;
    public int Order => 40;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();

        if (string.IsNullOrEmpty(ctx.RequestedLocationId) || !ctx.PartyPresent)
        {
            return pressures;
        }

        var location = await ctx.Session.LoadAsync<Location>(ctx.RequestedLocationId, ct);
        if (location == null)
        {
            return pressures;
        }

        var zone = await ClimateResolver.ResolveEffectiveZoneAsync(ctx.Session, location, ct);
        var ambientTemp = ClimateCycle.GetTemperatureCelsius(zone, ctx.Time.TimeOfDay);

        var party = await PressureQueryHelper.QueryPartyAtLocationAsync(ctx.Session, ctx.CampaignName, ctx.RequestedLocationId, 10, ct);

        foreach (var pc in party)
        {
            var warmth = pc.SystemStats?.WarmthRating ?? 0f;
            var feltTemp = ambientTemp - warmth;
            var mismatch = feltTemp - ComfortableFeltTemp;

            if (Math.Abs(mismatch) < MismatchThreshold)
            {
                continue;
            }

            var direction = mismatch < 0 ? "underdressed for the cold" : "overdressed / lacking cooling for the heat";
            pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, pc.Id,
                $"{pc.Name} looks {direction} for {location.Name}'s {zone} climate (felt {feltTemp:0}°C). " +
                "Consider narrating discomfort/gear commentary, or adjusting worn gear via item_equip/item_unequip.",
                GroupingKey));
        }

        return pressures;
    }
}
