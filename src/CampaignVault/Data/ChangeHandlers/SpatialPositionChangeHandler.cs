using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class SpatialPositionChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is SpatialPositionChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var src = (SpatialPositionChange)change;

        if (!ctx.Characters.TryGetValue(src.CharacterId, out var character))
        {
            character = await ctx.Session.LoadAsync<Character>(src.CharacterId, ct);
            if (character == null) return ChangeHandlerResult.Failure($"Character {src.CharacterId} not found.");
            ctx.RegisterNewCharacter(character);
        }

        character.SystemStats ??= new SystemExtension();
        character.SystemStats.SpatialPositions ??= [];

        if (string.IsNullOrEmpty(src.DistanceBand))
        {
            character.SystemStats.SpatialPositions.RemoveAll(p => p.TargetId == src.TargetId);
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
        }

        return ChangeHandlerResult.Ok;
    }
}