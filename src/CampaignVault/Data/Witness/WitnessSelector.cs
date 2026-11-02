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
    /// With <paramref name="sight"/>, a visible beat is seen only by those whose distance and the light allow it (I1); the rest
    /// still hear a loud one.
    Task<IReadOnlyList<WitnessNotice>> AtAsync(IAsyncDocumentSession session, string campaignName, string locationId, Perceptible signal,
        Character? source, IReadOnlySet<string> excluded, CancellationToken ct = default, SightContext? sight = null);

    /// <summary>
    /// Everyone one exit away (at most <see cref="WitnessSelector.EarshotMaxTravelHours"/>) who heard a loud beat. Distance muffles
    /// it: they hear it but don't see it, and a sleeper there sleeps through it. Empty when the beat isn't loud.
    /// </summary>
    Task<IReadOnlyList<EarshotWitnesses>> InEarshotAsync(IAsyncDocumentSession session, string campaignName, string locationId,
        Perceptible signal, IReadOnlySet<string> excluded, CancellationToken ct = default);
}

public sealed class WitnessSelector : IWitnessSelector
{
    /// <summary>A neighbour a quarter hour's walk away or nearer is in earshot; farther ones aren't. An uncosted exit: see Nested.</summary>
    public const double EarshotMaxTravelHours = 0.25;

    private const int MaxPerLocation = 25;

    public async Task<IReadOnlyList<WitnessNotice>> AtAsync(IAsyncDocumentSession session, string campaignName, string locationId,
        Perceptible signal, Character? source, IReadOnlySet<string> excluded, CancellationToken ct = default, SightContext? sight = null)
    {
        var witnesses = new List<WitnessNotice>();
        foreach (var c in await PresentAsync(session, campaignName, locationId, excluded, ct))
        {
            var notice = Perceive(c, signal, source, sight);
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
        var exits = (here.Exits ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e.TargetLocationId) && (e.TravelCostHours ?? 0) <= EarshotMaxTravelHours
                        && !string.Equals(e.TargetLocationId, locationId, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(e => e.TargetLocationId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<EarshotWitnesses>();
        foreach (var exit in exits)
        {
            if (await session.LoadAsync<Location>(exit.TargetLocationId, ct) is not { } neighbour
                || (exit.TravelCostHours is not > 0 && !Nested(here, neighbour)))
            {
                continue;
            }

            var heard = (await PresentAsync(session, campaignName, neighbour.Id, excluded, ct))
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

    /// <summary>One observer: <see cref="AwarenessResolver.Perceive"/>, less what their position hides (I1).</summary>
    public static Notice Perceive(Character observer, Perceptible signal, Character? source, SightContext? sight)
    {
        if (sight is null || (signal & Perceptible.Visible) == 0)
        {
            return AwarenessResolver.Perceive(observer, signal, source);
        }

        var line = SightResolver.Sees(observer, sight);
        return line.Sees
            ? AwarenessResolver.Perceive(observer, signal, source)
            : AwarenessResolver.Perceive(observer, signal, source, Perceptible.Visible, line.Note);
    }

    /// <summary>
    /// An exit with no travel cost says nothing about distance (it's the default), so it counts only between places that are
    /// plainly next door: one inside the other, or two rooms of the same place. "The road to the capital" with no cost doesn't.
    /// </summary>
    private static bool Nested(Location a, Location b) =>
        string.Equals(a.ParentLocationId, b.Id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(b.ParentLocationId, a.Id, StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrEmpty(a.ParentLocationId) && string.Equals(a.ParentLocationId, b.ParentLocationId, StringComparison.OrdinalIgnoreCase));

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
