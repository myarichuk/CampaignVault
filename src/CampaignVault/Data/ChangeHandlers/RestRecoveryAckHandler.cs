using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class RestRecoveryAckHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is RestRecoveryAck;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context,
        CancellationToken ct = default)
    {
        var ack = (RestRecoveryAck)change;

        if (string.IsNullOrWhiteSpace(ack.CharacterId))
        {
            return ChangeHandlerResult.Failure("characterId is required.");
        }

        if (!context.Characters.TryGetValue(ack.CharacterId, out var character))
        {
            character = context.Session != null
                ? await context.Session.LoadAsync<Character>(ack.CharacterId, ct)
                : null;

            if (character == null)
            {
                return ChangeHandlerResult.Failure($"Character '{ack.CharacterId}' not found.");
            }

            context.RegisterNewCharacter(character);
        }

        character.LastRestRecoveredDay = ack.RestDay;
        character.LastRecoveredRestSequence = ack.RestSequence;
        return ChangeHandlerResult.Ok;
    }
}