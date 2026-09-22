using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class RestRecoveryAckHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is RestRecoveryAck;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var ack = (RestRecoveryAck)change;

        if (string.IsNullOrWhiteSpace(ack.CharacterId))
        {
            return ChangeHandlerResult.Failure("characterId is required.");
        }

        if (!ctx.Characters.TryGetValue(ack.CharacterId, out var character))
        {
            character = ctx.Session != null
                ? await ctx.Session.LoadAsync<Character>(ack.CharacterId, ct)
                : null;

            if (character == null)
            {
                return ChangeHandlerResult.Failure($"Character '{ack.CharacterId}' not found.");
            }

            ctx.RegisterNewCharacter(character);
        }

        character.LastRestRecoveredDay = ack.RestDay;
        character.LastRecoveredRestSequence = ack.RestSequence;
        return ChangeHandlerResult.Ok;
    }
}