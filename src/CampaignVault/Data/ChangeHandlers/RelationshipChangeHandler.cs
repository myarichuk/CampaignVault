using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class RelationshipChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is RelationshipChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var rel = (RelationshipChange)change;

        if (!ctx.Characters.TryGetValue(rel.CharacterId, out var source))
        {
            source = await ctx.Session.LoadAsync<Character>(rel.CharacterId, ct);
            if (source == null)
            {
                var hints = await ctx.SuggestCharacterMatchAsync(rel.CharacterId);
                var msg = $"Character {rel.CharacterId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                ctx.RecordMessage($"WARNING: {msg}");
                ctx.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            ctx.RegisterNewCharacter(source);
        }

        if (!ctx.Characters.ContainsKey(rel.TargetId))
        {
            var target = await ctx.Session.LoadAsync<Character>(rel.TargetId, ct);
            if (target == null)
            {
                var hints = await ctx.SuggestCharacterMatchAsync(rel.TargetId);
                var msg = $"Relationship target character {rel.TargetId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                ctx.RecordMessage($"WARNING: {msg}");
                ctx.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            ctx.RegisterNewCharacter(target);
        }

        source.Social ??= new SocialProfile();
        source.Social.Relationships ??= new Dictionary<string, int>();

        // Document ids are case-insensitive in RavenDB but this dictionary is ordinal: "chars/Grog" and
        // "chars/grog" must update the one stored edge, not fork a second one the modifier lookup never sees.
        var targetKey = source.Social.Relationships.Keys
            .FirstOrDefault(k => string.Equals(k, rel.TargetId, StringComparison.OrdinalIgnoreCase)) ?? rel.TargetId;
        var currentVal = source.Social.Relationships.GetValueOrDefault(targetKey, 0);
        var requested = currentVal + rel.Delta;
        var stored = Math.Clamp(requested, -100, 100);
        source.Social.Relationships[targetKey] = stored;

        // The range note only earns its tokens when the clamp actually changed the outcome.
        var clampNote = stored != requested ? $" (clamped from {requested}; range -100..100)" : "";
        ctx.RecordMessage($"Relationship from {rel.CharacterId} to {rel.TargetId} is now {stored}.{clampNote}");

        return ChangeHandlerResult.Ok;
    }
}