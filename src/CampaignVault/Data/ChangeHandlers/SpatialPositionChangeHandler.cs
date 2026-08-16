using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class SpatialPositionChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is SpatialPositionChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var src = (SpatialPositionChange)change;

        if (!context.Characters.TryGetValue(src.CharacterId, out var character))
        {
            character = await context.Session.LoadAsync<Character>(src.CharacterId, ct);
            if (character == null) return ChangeHandlerResult.Failure($"Character {src.CharacterId} not found.");
            context.RegisterNewCharacter(character);
        }

        character.SystemStats ??= new SystemExtension();
        character.SystemStats.SpatialPositions ??= [];

        if (string.IsNullOrEmpty(src.DistanceBand))
        {
            character.SystemStats.SpatialPositions.RemoveAll(p => p.TargetId == src.TargetId);
            context.RecordMessage($"SpatialPosition removed for {src.CharacterId} relative to {src.TargetId}.");
        }
        else
        {
            character.SystemStats.SpatialPositions.RemoveAll(p => p.TargetId == src.TargetId);
            character.SystemStats.SpatialPositions.Add(new SpatialPosition
            {
                TargetId = src.TargetId,
                DistanceBand = src.DistanceBand,
                Bearing = src.Bearing,
                Zone = src.Zone
            });
            context.RecordMessage($"SpatialPosition set: {src.CharacterId} is {src.DistanceBand} from {src.TargetId}.");
        }

        return ChangeHandlerResult.Ok;
    }
}