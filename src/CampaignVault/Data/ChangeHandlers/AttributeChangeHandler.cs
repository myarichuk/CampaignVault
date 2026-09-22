using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class AttributeChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is AttributeChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var attr = (AttributeChange)change;

        if (!ctx.Characters.TryGetValue(attr.CharacterId, out var character))
        {
            character = await ctx.Session.LoadAsync<Character>(attr.CharacterId, ct);
            if (character == null)
            {
                var hints = await ctx.SuggestCharacterMatchAsync(attr.CharacterId);
                var msg = $"Character {attr.CharacterId} not found.";
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

        if (character.SystemStats == null)
        {
            ctx.RecordMessage($"WARNING: Character {attr.CharacterId} has no SystemStats during AttributeChange.");
            ctx.RecordFailure();
            return ChangeHandlerResult.Failure();
        }

        character.SystemStats.Attributes ??= [];

        var key = attr.Attribute.ToLowerInvariant();

        switch (key)
        {
            case "willpower":
                character.SystemStats.Willpower = Math.Clamp(
                    attr.IsDelta ? character.SystemStats.Willpower + attr.Value : attr.Value, 0f, 100f);
                break;

            case "temperature":
                character.SystemStats.Temperature = Math.Clamp(
                    attr.IsDelta ? character.SystemStats.Temperature + attr.Value : attr.Value, -50f, 100f);
                break;

            case "morale":
                character.SystemStats.Morale = Math.Clamp(
                    attr.IsDelta ? character.SystemStats.Morale + attr.Value : attr.Value, 0f, 100f);
                break;

            default:
                var current = character.SystemStats.Attributes.GetValueOrDefault(key, 0f);
                character.SystemStats.Attributes[key] = Math.Clamp(
                    attr.IsDelta ? current + attr.Value : attr.Value, 0f, 100f);
                break;
        }

        // No success message: the caller already knows the attribute/value it just set, and unlike
        // HpChange this doesn't reveal a clamped/derived total worth echoing back.
        return ChangeHandlerResult.Ok;
    }
}