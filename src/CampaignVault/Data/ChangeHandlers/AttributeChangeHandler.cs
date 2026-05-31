using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class AttributeChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is AttributeChange;

    public Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var attr = (AttributeChange)change;

        if (!context.Characters.TryGetValue(attr.CharacterId, out var character) || character?.Mind is null)
        {
            context.RecordMessage($"WARNING: Character {attr.CharacterId} not found or has no Mind during AttributeChange.");
            context.RecordFailure();
            return Task.FromResult(ChangeHandlerResult.Failure());
        }

        var key = attr.Attribute.ToLowerInvariant();

        switch (key)
        {
            case "willpower":
                character.Mind.Willpower = Math.Clamp(
                    attr.IsDelta ? character.Mind.Willpower + attr.Value : attr.Value, 0f, 100f);
                break;

            case "temperature":
                character.Mind.Temperature = Math.Clamp(
                    attr.IsDelta ? character.Mind.Temperature + attr.Value : attr.Value, -50f, 100f);
                break;

            case "morale":
                character.Mind.Morale = Math.Clamp(
                    attr.IsDelta ? character.Mind.Morale + attr.Value : attr.Value, 0f, 100f);
                break;

            default:
                var current = character.Mind.Attributes.GetValueOrDefault(key, 0f);
                character.Mind.Attributes[key] = Math.Clamp(
                    attr.IsDelta ? current + attr.Value : attr.Value, 0f, 100f);
                break;
        }

        context.RecordMessage($"Attribute '{attr.Attribute}' set for {attr.CharacterId}");

        return Task.FromResult(ChangeHandlerResult.Ok);
    }
}