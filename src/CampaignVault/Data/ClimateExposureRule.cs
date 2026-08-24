using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Each AdvanceWorld tick, computes ambient temperature (ClimateCycle, resolved via ClimateResolver)
/// plus each observed character's WarmthRating and writes the result to SystemExtension.Temperature
/// via the existing "attribute" delta shape. Sustained extremes already narrate through the existing
/// CharacterDistressPressureContributor (Temperature &lt;= -20 / &gt;= 50) — this rule only ever writes
/// the reading, it never applies a StatusEffect itself; that consequence call stays with the DM-LLM.
///
/// SCOPE — who gets a reading:
///   - PCs and party companions, always, wherever they are. They are the ones exposure is *about*.
///   - Anyone standing where the party is standing. If the PCs are freezing in the pass, so is the
///     guide next to them, and the DM needs that on screen.
///
/// Everyone else is off screen, and this rule deliberately does not simulate their weather. It used to
/// simulate all of them: every character in the campaign, every tick, each one staged as an
/// AttributeChange through the full commit path — for a value nothing reads unless it crosses ±a
/// threshold, on characters nobody is looking at. The write volume scaled with world size while the
/// useful output scaled with party size.
///
/// STALENESS — why off-screen characters are reset rather than simply skipped: an untouched character
/// would keep whatever reading they had when the party last saw them. A named NPC last met in a desert
/// at 41°C would carry that reading forever, and CharacterDistressPressureContributor reads every
/// KeepAlive character regardless of where they are — so a frozen extreme would generate "X is
/// suffering from extreme heat" pressure indefinitely for someone sitting comfortably indoors
/// somewhere else. Instead each such character is written back to the neutral default exactly once, as
/// they leave scope; after that their reading already matches and they are never touched again, so
/// this converges rather than costing a write per tick. Off screen they carry no temperature claim
/// instead of a false one.
///
/// A character coming back on screen needs no special handling: the next tick finds them in scope and
/// computes the real reading from their location's climate. Between meeting them and that tick they
/// sit at the neutral default, which is the correct thing to say about someone whose exposure nobody
/// has established yet.
/// </summary>
public class ClimateExposureRule : ISimulationRule
{
    public string Name => "Climate Exposure";

    // Runs near NeedsAccumulationRule (35) — both are ambient per-tick character state updates.
    public int Order => 37;

    /// <summary>
    /// The reading an off-screen character carries: <see cref="SystemExtension.Temperature"/>'s own
    /// default, and comfortably inside both distress thresholds, so it reads as "unremarkable /
    /// not established" rather than as a claim about their surroundings.
    /// </summary>
    private const float NeutralTemperature = 20f;

    public virtual async Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<RuleNarrative>();
        var deltas = new List<WorldChange>();

        var located = context.ScheduledNpcs.Where(c => !string.IsNullOrEmpty(c.CurrentLocationId)).ToList();
        if (located.Count == 0)
        {
            return new RuleResult(narratives, deltas);
        }

        // Party locations come out of the character list already in memory — no extra query.
        var partyLocationIds = located
            .Where(c => c.IsPc || c.IsPartyCompanion)
            .Select(c => c.CurrentLocationId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var observed = new List<Character>();
        foreach (var character in located)
        {
            if (character.IsPc || character.IsPartyCompanion || partyLocationIds.Contains(character.CurrentLocationId!))
            {
                observed.Add(character);
            }
            else if (Math.Abs((character.SystemStats?.Temperature ?? NeutralTemperature) - NeutralTemperature) >= 0.5f)
            {
                // Leaving scope carrying a stale reading — clear it once. See STALENESS above.
                deltas.Add(NewReading(character.Id, NeutralTemperature));
            }
        }

        if (observed.Count == 0)
        {
            return new RuleResult(narratives, deltas);
        }

        var locationIds = observed.Select(c => c.CurrentLocationId!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var locations = await context.Session.LoadAsync<Location>(locationIds, ct);

        // Memoize per-location zone resolution: characters sharing a location (or an ancestor chain)
        // are common, and re-walking ParentLocationId for each one would multiply session requests.
        var resolvedZones = new Dictionary<string, ClimateZone>(StringComparer.OrdinalIgnoreCase);

        foreach (var npc in observed)
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

            var ambientTemp = ClimateCycle.GetTemperatureCelsius(zone, context.Time.Hour);
            var warmth = npc.SystemStats?.WarmthRating ?? 0f;

            // Quantize to whole degrees before comparing AND before writing. The only consumer is
            // CharacterDistressPressureContributor's <= -20 / >= 50 thresholds, so sub-degree precision
            // is noise — but at the old 0.01 epsilon that noise made the diurnal curve differ for
            // essentially every character on essentially every tick.
            var feltTemp = MathF.Round(ambientTemp + warmth);

            var currentTemp = npc.SystemStats?.Temperature ?? 0f;
            if (Math.Abs(feltTemp - currentTemp) < 0.5f)
            {
                continue;
            }

            deltas.Add(NewReading(npc.Id, feltTemp));
        }

        return new RuleResult(narratives, deltas);
    }

    private static AttributeChange NewReading(string characterId, float value) => new()
    {
        CharacterId = characterId,
        Attribute = "temperature",
        Value = value,
        IsDelta = false,
    };
}
