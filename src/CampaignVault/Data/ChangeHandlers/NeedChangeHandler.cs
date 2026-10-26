using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class NeedChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is NeedChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var nc = (NeedChange)change;

        if (!ctx.Characters.TryGetValue(nc.CharacterId, out var character))
        {
            character = await ctx.Session.LoadAsync<Character>(nc.CharacterId, ct);
            if (character == null)
            {
                var hints = await ctx.SuggestCharacterMatchAsync(nc.CharacterId);
                var msg = $"Character {nc.CharacterId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                ctx.RecordMessage($"WARNING: {msg}");
                ctx.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            ctx.RegisterNewCharacter(character);
        }

        if (character.Needs == null)
        {
            ctx.RecordMessage($"WARNING: Character {nc.CharacterId} has no NeedsProfile during NeedChange.");
            ctx.RecordFailure();
            return ChangeHandlerResult.Failure();
        }

        if (string.IsNullOrWhiteSpace(nc.Need))
        {
            return ChangeHandlerResult.Failure("need is required (e.g. hunger, thirst, tiredness, or a custom need).");
        }

        // Need keys are ordinal dictionary keys read as lower-case everywhere else (the accumulation rule, mood
        // thresholds, the deprivation rule all ask for "hunger"); a model that sends "Hunger" must land on the same
        // key, not fork a second need the simulation never reads.
        var needKey = ResolveNeedKey(character.Needs, nc.Need);
        nc.Need = needKey;

        var current = character.Needs.ActiveNeeds.GetValueOrDefault(needKey, 0f);
        var newValue = Math.Clamp(current + nc.Delta, 0f, 100f);

        // Replace the entire ActiveNeeds dictionary to ensure RavenDB tracks the change.
        // Modifying a nested dictionary in-place doesn't trigger change detection in RavenDB.
        var updatedNeeds = new Dictionary<string, float>(character.Needs.ActiveNeeds)
        {
            [needKey] = newValue
        };
        character.Needs.ActiveNeeds = updatedNeeds;

        // Optional rate-setting: independent of the immediate delta push above. Omit leaves the
        // current rate unchanged; 0 stops passive drift without deleting history. (AccumulationRate
        // is not a C# reserved keyword and does not shadow any existing NeedChange member.)
        if (nc.AccumulationRate.HasValue)
        {
            character.Needs.AccumulationRates[needKey] = nc.AccumulationRate.Value;
        }

        // No success message: the caller specified the need/delta itself (nothing computed to echo
        // back), and background accumulation ticks were already silent before this.

        return ChangeHandlerResult.Ok;
    }

    /// <summary>
    /// The key to write: an existing key matching case-insensitively (either dictionary), else the
    /// trimmed lower-case name, which is the convention every reader of the needs dictionaries uses.
    /// </summary>
    internal static string ResolveNeedKey(NeedsProfile needs, string requested)
    {
        var trimmed = requested.Trim();
        return needs.ActiveNeeds.Keys.FirstOrDefault(k => string.Equals(k, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? needs.AccumulationRates.Keys.FirstOrDefault(k => string.Equals(k, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? trimmed.ToLowerInvariant();
    }
}
