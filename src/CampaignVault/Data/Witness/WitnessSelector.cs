using CampaignVault.Data.Context;
using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data.Witness;

/// <summary>One observer and what they got from the beat.</summary>
public sealed record WitnessNotice(Character Character, Notice Notice);

/// <summary>Witnesses at one location within earshot of the beat (WITNESS_CHANNEL_PLAN.md W5).</summary>
public sealed record EarshotWitnesses(Location Where, IReadOnlyList<WitnessNotice> Heard);

/// <summary>
/// Who witnessed a beat (WITNESS_CHANNEL_PLAN.md W6): the non-PC characters at a location, minus the excluded (the actor, targets,
/// combatants), each run through <see cref="AwarenessResolver"/>. Shared by the witness line and, later, the knownBy audience
/// default (KNOWN_BY_AND_CI_PLAN.md K1), so "who was there and noticed" has one answer.
/// </summary>
public interface IWitnessSelector
{
    /// <summary>Everyone at <paramref name="locationId"/> who perceived the beat, companions and keep-alive NPCs first.</summary>
    Task<IReadOnlyList<WitnessNotice>> AtAsync(IAsyncDocumentSession session, string campaignName, string locationId, Perceptible signal,
        Character? source, IReadOnlySet<string> excluded, CancellationToken ct = default);

    /// <summary>
    /// Everyone one exit away (at most <see cref="WitnessSelector.EarshotMaxTravelHours"/>) who heard a loud beat. Distance muffles
    /// it: they hear it but don't see it, and a sleeper there sleeps through it. Empty when the beat isn't loud.
    /// </summary>
    Task<IReadOnlyList<EarshotWitnesses>> InEarshotAsync(IAsyncDocumentSession session, string campaignName, string locationId,
        Perceptible signal, IReadOnlySet<string> excluded, CancellationToken ct = default);
}

public sealed class WitnessSelector : IWitnessSelector
{
    /// <summary>A neighbour a quarter hour's walk away or nearer is in earshot; farther ones aren't, whatever the exit says.</summary>
    public const double EarshotMaxTravelHours = 0.25;

    private const int MaxPerLocation = 25;

    public async Task<IReadOnlyList<WitnessNotice>> AtAsync(IAsyncDocumentSession session, string campaignName, string locationId,
        Perceptible signal, Character? source, IReadOnlySet<string> excluded, CancellationToken ct = default)
    {
        var witnesses = new List<WitnessNotice>();
        foreach (var c in await PresentAsync(session, campaignName, locationId, excluded, ct))
        {
            var notice = AwarenessResolver.Perceive(c, signal, source);
            if (notice.Perceived)
            {
                witnesses.Add(new WitnessNotice(c, notice));
            }
        }

        return witnesses;
    }

    public async Task<IReadOnlyList<EarshotWitnesses>> InEarshotAsync(IAsyncDocumentSession session, string campaignName,
        string locationId, Perceptible signal, IReadOnlySet<string> excluded, CancellationToken ct = default)
    {
        if ((signal & Perceptible.Loud) == 0 || await session.LoadAsync<Location>(locationId, ct) is not { } here)
        {
            return [];
        }

        // Exits are kept reciprocal (LocationConnectivity), so this location's own exits are its neighbours.
        var neighbourIds = (here.Exits ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e.TargetLocationId) && (e.TravelCostHours ?? 0) <= EarshotMaxTravelHours
                        && !string.Equals(e.TargetLocationId, locationId, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.TargetLocationId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<EarshotWitnesses>();
        foreach (var neighbourId in neighbourIds)
        {
            if (await session.LoadAsync<Location>(neighbourId, ct) is not { } neighbour)
            {
                continue;
            }

            var heard = (await PresentAsync(session, campaignName, neighbourId, excluded, ct))
                .Select(c => new WitnessNotice(c, AwarenessResolver.Perceive(c, Perceptible.Loud)))
                .Where(w => w.Notice.Outcome == Noticed.Perceives)
                .ToList();
            if (heard.Count > 0)
            {
                result.Add(new EarshotWitnesses(neighbour, heard));
            }
        }

        return result;
    }

    private static async Task<IEnumerable<Character>> PresentAsync(IAsyncDocumentSession session, string campaignName, string locationId,
        IReadOnlySet<string> excluded, CancellationToken ct)
    {
        var here = await session.Query<Character>()
            .Where(c => c.CampaignName == campaignName && c.CurrentLocationId == locationId && !c.IsPc && c.Death == null)
            .Take(MaxPerLocation)
            .ToListAsync(ct);
        return here.Where(c => !excluded.Contains(c.Id))
            .OrderByDescending(c => c.IsPartyCompanion || c.KeepAlive)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }
}
