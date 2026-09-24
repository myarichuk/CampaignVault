using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

/// <summary>
/// T5c hidden content and hazards. Secret exits, concealed items, secret item details and traps stay off
/// the scene payloads; the engine resolves finding them the way it resolves dice:
/// <list type="bullet">
/// <item>an Investigation/Perception check at a location reveals what its total meets (<see cref="DiscoverAsync"/>);</item>
/// <item>arriving party members' passive Perception does the same for the obvious ones;</item>
/// <item>a live hazard fires on its trigger (passing an exit, entering a location, taking an item) unless spotted;</item>
/// <item>a check whose parameters name a hazard as "disarm" makes it safe (or sets it off on a bad miss).</item>
/// </list>
/// The DM hears about all of it once per session, on the first visit, through <see cref="DmSecretsAsync"/>.
/// </summary>
internal static class HiddenContent
{
    /// <summary>Missing a disarm check by this much sets the trap off.</summary>
    private const int DisarmBotchMargin = 5;

    internal static bool IsSearchSkill(string? skill) =>
        skill != null && (skill.Contains("perception", StringComparison.OrdinalIgnoreCase)
                          || skill.Contains("investigation", StringComparison.OrdinalIgnoreCase)
                          || skill.Contains("search", StringComparison.OrdinalIgnoreCase));

    /// <summary>Items held by a location and, one level down, by those items (a key in the desk).</summary>
    internal static async Task<List<Item>> ItemsAtAsync(IAsyncDocumentSession session, string locationId, CancellationToken ct)
    {
        var top = await session.Query<Item, Item_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.HolderId == locationId)
            .Take(64)
            .ToListAsync(ct);
        top = top.Where(i => !i.IsArchived).ToList();
        if (top.Count == 0)
        {
            return top;
        }

        var topIds = top.Select(i => i.Id).ToList();
        var nested = await session.Query<Item, Item_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.HolderId.In(topIds))
            .Take(128)
            .ToListAsync(ct);
        return top.Concat(nested.Where(i => !i.IsArchived)).ToList();
    }

    /// <summary>Reveals every secret at <paramref name="locationId"/> whose DC <paramref name="score"/> meets and
    /// returns one message per find. <paramref name="verb"/> is "FOUND" for an active check, "NOTICED" for passive.</summary>
    internal static async Task<List<string>> DiscoverAsync(
        IAsyncDocumentSession session, string locationId, int score, string verb, string? who, CancellationToken ct)
    {
        var messages = new List<string>();
        var location = await session.LoadAsync<Location>(locationId, ct);
        if (location == null)
        {
            return messages;
        }

        var by = string.IsNullOrEmpty(who) ? "" : $" ({who}, {score})";
        for (var i = 0; i < location.Exits.Count; i++)
        {
            var exit = location.Exits[i];
            if (exit.Hidden && exit.DiscoverDc is { } dc && score >= dc)
            {
                location.Exits[i] = exit = exit with { Hidden = false };
                messages.Add($"{verb}{by}: a hidden way to {exit.TargetLocationId}. {exit.Description}".TrimEnd());
            }

            if (exit.Hazard is { IsLive: true, Detected: false, DetectDc: { } hdc } h && score >= hdc)
            {
                location.Exits[i] = exit with { Hazard = h with { Detected = true } };
                messages.Add($"{verb}{by}: {h.Name} on the way to {exit.TargetLocationId} ({h.Effect}).");
            }
        }

        for (var i = 0; i < location.Hazards.Count; i++)
        {
            var h = location.Hazards[i];
            if (h is { IsLive: true, Detected: false, DetectDc: { } hdc } && score >= hdc)
            {
                location.Hazards[i] = h with { Detected = true };
                messages.Add($"{verb}{by}: {h.Name} here ({h.Effect}).");
            }
        }

        foreach (var item in await ItemsAtAsync(session, locationId, ct))
        {
            if (item.Hidden && item.DiscoverDc is { } idc && score >= idc)
            {
                item.Hidden = false;
                messages.Add($"{verb}{by}: {item.Name} [{item.Id}].");
            }

            if (item.Hazard is { IsLive: true, Detected: false, DetectDc: { } hdc } ih && score >= hdc)
            {
                item.Hazard = ih with { Detected = true };
                messages.Add($"{verb}{by}: {ih.Name} on {item.Name} ({ih.Effect}).");
            }

            foreach (var detail in item.ItemDetails.Where(d => d.Hidden && !d.IsRetired))
            {
                if (detail.DiscoverDc is { } ddc && score >= ddc)
                {
                    detail.Hidden = false;
                    messages.Add($"{verb}{by}: {item.Name} — {detail.Name}: {detail.Description}");
                }
            }
        }

        return messages;
    }

    /// <summary>The DM-only first-visit line: every secret here, with its DC and intent. Never narration.</summary>
    internal static async Task<List<string>> DmSecretsAsync(IAsyncDocumentSession session, string locationId, CancellationToken ct)
    {
        var lines = new List<string>();
        var location = await session.LoadAsync<Location>(locationId, ct);
        if (location == null)
        {
            return lines;
        }

        static string Dc(int? dc, string what) => dc is { } v ? $"{what} DC {v}" : $"{what}: your call";
        static string Why(string? intent) => string.IsNullOrWhiteSpace(intent) ? "" : $"; {intent}";
        static string Trap(Hazard h, string where) =>
            $"trap '{h.Name}' {where} ({Dc(h.DetectDc, "spot")}, {Dc(h.DisarmDc, "disarm")}, fires on {h.Trigger}): {h.Effect}{Why(h.Intent)}";

        foreach (var exit in location.Exits)
        {
            if (exit.Hidden)
            {
                lines.Add($"hidden way to {exit.TargetLocationId} ({Dc(exit.DiscoverDc, "find")}){Why(exit.Intent)}");
            }

            if (exit.Hazard is { IsLive: true } h)
            {
                lines.Add(Trap(h, $"on the way to {exit.TargetLocationId}"));
            }
        }

        lines.AddRange(location.Hazards.Where(h => h.IsLive).Select(h => Trap(h, "here")));

        foreach (var item in await ItemsAtAsync(session, locationId, ct))
        {
            if (item.Hidden)
            {
                lines.Add($"hidden {item.Name} [{item.Id}] ({Dc(item.DiscoverDc, "find")})");
            }

            if (item.Hazard is { IsLive: true } ih)
            {
                lines.Add(Trap(ih, $"on {item.Name} [{item.Id}]"));
            }

            foreach (var d in item.ItemDetails.Where(d => !d.IsRetired && (d.Hidden || !string.IsNullOrWhiteSpace(d.Intent))))
            {
                var secret = d.Hidden ? $" (secret, {Dc(d.DiscoverDc, "find")})" : "";
                lines.Add($"{item.Name}: {d.Name}{secret}{Why(d.Intent)}");
            }
        }

        return lines;
    }

    /// <summary>Fires a hazard at <paramref name="who"/>: the report the DM resolves, and the hazard's new state.</summary>
    internal static (string Message, Hazard After) Fire(Hazard h, string where, string who)
    {
        var save = h.SaveDc is { } dc
            ? $" Resolve now: ruleset_action SavingThrow ({h.SaveAbility ?? "Dexterity"}, dc {dc}) for {who}, then commit the result."
            : " Resolve the effect now and commit it.";
        return ($"HAZARD: {h.Name} {where} goes off on {who}: {h.Effect}.{save}",
            h with { Detected = true, Spent = !h.Rearms });
    }

    /// <summary>A live hazard met by someone who spotted it: no automatic firing, the DM adjudicates.</summary>
    internal static string Known(Hazard h, string where, string who) =>
        $"{who} passes the known {h.Name} {where} (still armed). If anyone blunders into it: {h.Effect}.";

    /// <summary>Resolves a disarm check against the named hazard at the actor's location.</summary>
    internal static async Task<string?> DisarmAsync(
        IAsyncDocumentSession session, string locationId, string hazardName, int total, string who, CancellationToken ct)
    {
        (string Message, Hazard After) Resolve(Hazard h, string where)
        {
            if (h.DisarmDc is { } dc && total >= dc)
            {
                return ($"DISARMED: {who} makes the {h.Name} {where} safe ({total} vs DC {dc}).", h with { Disarmed = true, Detected = true });
            }

            if (h.DisarmDc is { } miss && total <= miss - DisarmBotchMargin)
            {
                return Fire(h, where, who);
            }

            return ($"The {h.Name} {where} is still armed ({total} vs DC {h.DisarmDc?.ToString() ?? "?"}).", h with { Detected = true });
        }

        bool Matches(Hazard? h) => h is { IsLive: true } && h.Name.Equals(hazardName, StringComparison.OrdinalIgnoreCase);

        var location = await session.LoadAsync<Location>(locationId, ct);
        if (location == null)
        {
            return null;
        }

        for (var i = 0; i < location.Hazards.Count; i++)
        {
            if (Matches(location.Hazards[i]))
            {
                var (msg, after) = Resolve(location.Hazards[i], "here");
                location.Hazards[i] = after;
                return msg;
            }
        }

        for (var i = 0; i < location.Exits.Count; i++)
        {
            if (Matches(location.Exits[i].Hazard))
            {
                var (msg, after) = Resolve(location.Exits[i].Hazard!, $"on the way to {location.Exits[i].TargetLocationId}");
                location.Exits[i] = location.Exits[i] with { Hazard = after };
                return msg;
            }
        }

        foreach (var item in await ItemsAtAsync(session, locationId, ct))
        {
            if (Matches(item.Hazard))
            {
                var (msg, after) = Resolve(item.Hazard!, $"on {item.Name}");
                item.Hazard = after;
                return msg;
            }
        }

        return $"No armed hazard named '{hazardName}' here.";
    }
}
