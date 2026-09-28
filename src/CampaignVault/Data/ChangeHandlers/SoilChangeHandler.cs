using CampaignVault.Events;
using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Apply, worsen, wash and clear dirt for <see cref="SoilChange"/> on a character, item or location. Dirt lives on the
/// host (<see cref="IHasDirt"/>), so the mutation rides the same atomic commit as everything else. Publishes
/// <see cref="CoreEvents.Soiled"/> for every mark that changed.
/// </summary>
public sealed class SoilChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is SoilChange;

    /// <summary>
    /// Only <c>targetId</c> names an entity. The default extractor would also scan kind/spot/note, and a spot like
    /// "locket" or a kind like "quicksilver" looks like a location or quest ID prefix.
    /// </summary>
    public bool ExtractInvolvedEntities(
        WorldChange change,
        HashSet<string>? characterIds = null,
        HashSet<string>? locationIds = null,
        HashSet<string>? factionIds = null,
        HashSet<string>? questIds = null,
        HashSet<string>? itemIds = null,
        HashSet<string>? allInvolvedIds = null)
    {
        if (change is not SoilChange { TargetId: { Length: > 0 } id }) return change is SoilChange;

        if (id.StartsWith("chars/", StringComparison.Ordinal)) characterIds?.Add(id);
        else if (id.StartsWith("loc", StringComparison.Ordinal)) locationIds?.Add(id);
        else if (id.StartsWith("item", StringComparison.Ordinal)) itemIds?.Add(id);
        allInvolvedIds?.Add(id);
        return true;
    }

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var req = (SoilChange)change;
        var clear = req.Clear == true;
        var kind = SoilHelpers.NormalizeKind(req.Kind);

        if (string.IsNullOrWhiteSpace(req.TargetId))
            return ChangeHandlerResult.Failure("targetId is required (a character, item or location ID).");
        if (!clear && kind is null)
            return ChangeHandlerResult.Failure("kind is required (blood, mud, dust...) unless clear:true.");
        if (kind is { Length: > SoilHelpers.MaxKindLength })
            return ChangeHandlerResult.Failure($"kind must be at most {SoilHelpers.MaxKindLength} characters.");
        if (!clear && req.Amount == 0)
            return ChangeHandlerResult.Failure("amount must not be 0 (+1 soils, -1 washes), or pass clear:true.");

        var targetId = req.TargetId.Trim();
        var (host, name, isLocation) = await ResolveAsync(ctx, targetId, ct);
        if (host is null)
            return ChangeHandlerResult.Failure($"{targetId} is not a known character, item or location.");
        if (SoilHelpers.Clean(req.Fixture) is not null && !isLocation)
            return ChangeHandlerResult.Failure("fixture applies to locations only; use spot to place dirt on a character or item.");

        host.Dirt ??= [];
        List<(DirtMark Mark, string Action, int Severity)> touched = [];
        string? evictedNote = null;

        if (clear)
        {
            foreach (var mark in SoilHelpers.Clear(host.Dirt, kind, req.Spot, req.Fixture))
                touched.Add((mark, "cleared", 0));
        }
        else
        {
            var day = (int)(await ctx.GetCurrentTimeAsync()).TotalDaysElapsed;
            var outcome = SoilHelpers.Apply(host.Dirt, kind!, req.Spot, req.Fixture, req.Amount, day, req.Note);
            foreach (var mark in outcome.Changed)
                touched.Add((mark, outcome.Action, mark.Severity));
            if (outcome.Evicted is { } evicted)
            {
                touched.Add((evicted, "evicted", 0));
                evictedNote = $" ({SoilHelpers.Phrase(evicted)} faded to make room)";
            }
        }

        if (touched.Count == 0)
        {
            ctx.RecordMessage($"soil: {name} had no matching dirt (no-op).");
            return ChangeHandlerResult.Ok;
        }

        foreach (var (mark, action, severity) in touched)
        {
            ctx.Publish(CoreEvents.Soiled, new Dictionary<string, object?>
            {
                [CoreEvents.Fields.TargetId] = targetId,
                [CoreEvents.Fields.Kind] = mark.Kind,
                [CoreEvents.Fields.Severity] = severity,
                [CoreEvents.Fields.Spot] = mark.Spot,
                [CoreEvents.Fields.Fixture] = mark.Fixture,
                [CoreEvents.Fields.Action] = action,
            });
        }

        ctx.RecordMessage(Describe(name, host, clear, touched.Count, evictedNote));
        return ChangeHandlerResult.Ok;
    }

    private static string Describe(string name, IHasDirt host, bool clear, int touched, string? evictedNote)
    {
        var now = SoilHelpers.Summarize(host.Dirt);
        if (clear)
            return $"{name}: cleaned {touched} mark(s); now {now ?? "clean"}.";
        return $"{name}: {now ?? "clean"}.{evictedNote}";
    }

    private static async Task<(IHasDirt? Host, string Name, bool IsLocation)> ResolveAsync(
        ChangeContext ctx, string id, CancellationToken ct)
    {
        if (ctx.Characters.TryGetValue(id, out var c)) return (c, c.Name, false);
        if (ctx.Items.TryGetValue(id, out var i)) return (i, i.Name, false);
        if (ctx.Locations.TryGetValue(id, out var l)) return (l, l.Name, true);
        if (ctx.Session is null) return (null, id, false);

        // Not preloaded (the dispatcher already applied campaign visibility to what it did preload), so hold a
        // session fallback to the same rule instead of letting a soil reach into another campaign's entity.
        if (id.StartsWith("chars/", StringComparison.Ordinal) && await ctx.Session.LoadAsync<Character>(id, ct) is { } lc && VisibleTo(ctx, lc))
            return (lc, lc.Name, false);
        if (id.StartsWith("item", StringComparison.Ordinal) && await ctx.Session.LoadAsync<Item>(id, ct) is { } li && VisibleTo(ctx, li))
            return (li, li.Name, false);
        if (id.StartsWith("loc", StringComparison.Ordinal) && await ctx.Session.LoadAsync<Location>(id, ct) is { } ll && VisibleTo(ctx, ll))
            return (ll, ll.Name, true);
        return (null, id, false);
    }

    private static bool VisibleTo(ChangeContext ctx, ICampaignScopedEntity entity) =>
        string.IsNullOrEmpty(ctx.CampaignName) || CampaignEntityVisibility.IsVisibleInCampaign(entity.CampaignName, ctx.CampaignName);
}
