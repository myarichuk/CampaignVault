using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>Reads a character's tethers, dropping any whose exit condition has been met.</summary>
public static class TetherState
{
    public const string FixturePrefix = "fixture:";
    public const int MaxPerSubject = 4;

    public static bool IsFixture(string id) => id.StartsWith(FixturePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The tethers still holding <paramref name="subject"/>. A tether whose anchor item is gone or archived, whose anchor
    /// character is gone, or whose holder is incapacitated is removed here and reported once, so a tether can never
    /// outlive its exit conditions.
    /// </summary>
    public static async Task<List<Tether>> LiveAsync(ChangeContext ctx, Character subject, CancellationToken ct = default)
    {
        if (subject.SystemStats?.Tethers is not { Count: > 0 } tethers)
            return [];

        var time = await ctx.GetCurrentTimeAsync();
        var nowDays = time.TotalDaysElapsed + time.Hour / 24.0;
        var live = new List<Tether>();
        foreach (var tether in tethers.ToList())
        {
            var why = await ReleasedReasonAsync(ctx, tether, nowDays, ct);
            if (why is null)
            {
                live.Add(tether);
                continue;
            }

            tethers.Remove(tether);
            ctx.RecordMessage($"{subject.Name} is no longer tethered to {tether.AnchorId}: {why}.");
        }

        return live;
    }

    private static async Task<string?> ReleasedReasonAsync(ChangeContext ctx, Tether tether, double nowDays, CancellationToken ct)
    {
        if (tether.HolderId is { Length: > 0 } holderId)
        {
            var holder = await LoadCharacterAsync(ctx, holderId, ct);
            if (holder is null)
                return "its holder is gone";
            if (ActionBlock.IsBlocked(holder, out _, nowDays))
                return $"{holder.Name} can no longer hold it";
        }

        if (IsFixture(tether.AnchorId))
            return null;

        if (ctx.Items.TryGetValue(tether.AnchorId, out var item) ||
            (ctx.Session is not null && (item = await ctx.Session.LoadAsync<Item>(tether.AnchorId, ct)) is not null))
            return item.IsArchived ? "the anchor is destroyed" : null;

        return await LoadCharacterAsync(ctx, tether.AnchorId, ct) is null ? "the anchor is gone" : null;
    }

    private static async Task<Character?> LoadCharacterAsync(ChangeContext ctx, string id, CancellationToken ct)
    {
        if (ctx.Characters.TryGetValue(id, out var known))
            return known;
        return ctx.Session is null ? null : await ctx.Session.LoadAsync<Character>(id, ct);
    }

    /// <summary>True if the anchor (or whoever holds its end) is a character travelling to the same place in this batch.</summary>
    public static bool AnchorTravelsAlong(ChangeContext ctx, Tether tether, string destinationLocationId)
    {
        bool CoTravels(string? id) =>
            !string.IsNullOrEmpty(id) &&
            ctx.Batch?.OfType<TravelChange>().Any(t =>
                string.Equals(t.CharacterId, id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.DestinationLocationId, destinationLocationId, StringComparison.OrdinalIgnoreCase)) == true;

        if (CoTravels(tether.HolderId) || CoTravels(tether.AnchorId))
            return true;
        // A carried anchor item (a rope end, a leash) goes wherever whoever carries it goes.
        return ctx.Items.TryGetValue(tether.AnchorId, out var item) && CoTravels(item.HolderId);
    }
}
