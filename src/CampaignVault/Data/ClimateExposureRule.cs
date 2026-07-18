using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Each AdvanceWorld tick, computes ambient temperature (ClimateCycle, resolved via ClimateResolver)
/// minus each located character's WarmthRating and writes the result to SystemExtension.Temperature
/// via the existing "attribute" delta shape. Sustained extremes already narrate through the existing
/// CharacterDistressPressureContributor (Temperature &lt;= -20 / &gt;= 50) — this rule only ever writes
/// the reading, it never applies a StatusEffect itself; that consequence call stays with the DM-LLM.
/// </summary>
public class ClimateExposureRule : ISimulationRule
{
    public string Name => "Climate Exposure";

    // Runs near NeedsAccumulationRule (35) — both are ambient per-tick character state updates.
    public int Order => 37;

    public virtual async Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<RuleNarrative>();
        var deltas = new List<WorldChange>();

        var exposed = context.ScheduledNpcs.Where(c => !string.IsNullOrEmpty(c.CurrentLocationId)).ToList();
        if (exposed.Count == 0)
        {
            return new RuleResult(narratives, deltas);
        }

        var locationIds = exposed.Select(c => c.CurrentLocationId!).Distinct().ToList();
        var locations = await context.Session.LoadAsync<Location>(locationIds, ct);

        // Memoize per-location zone resolution: characters sharing a location (or an ancestor chain)
        // are common, and re-walking ParentLocationId for each one would multiply session requests.
        var resolvedZones = new Dictionary<string, ClimateZone>(StringComparer.OrdinalIgnoreCase);

        foreach (var npc in exposed)
        {
            if (!locations.TryGetValue(npc.CurrentLocationId!, out var location) || location == null)
            {
                continue;
            }

            if (!resolvedZones.TryGetValue(location.Id, out var zone))
            {
                zone = await ClimateResolver.ResolveEffectiveZoneAsync(context.Session, location, ct);
                resolvedZones[location.Id] = zone;
            }

            var ambientTemp = ClimateCycle.GetTemperatureCelsius(zone, context.Time.TimeOfDay);
            var warmth = npc.SystemStats?.WarmthRating ?? 0f;
            var feltTemp = ambientTemp - warmth;

            // Skip emitting if temperature hasn't changed (within epsilon)
            var currentTemp = npc.SystemStats?.Temperature ?? 0f;
            if (Math.Abs(feltTemp - currentTemp) < 0.01f)
            {
                continue;
            }

            deltas.Add(new AttributeChange
            {
                CharacterId = npc.Id,
                Attribute = "temperature",
                Value = feltTemp,
                IsDelta = false,
            });
        }

        return new RuleResult(narratives, deltas);
    }
}
