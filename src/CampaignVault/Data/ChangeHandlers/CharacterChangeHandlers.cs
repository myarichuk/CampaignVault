using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class CharacterCreateHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is CharacterCreate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var cc = (CharacterCreate)change;
        if (string.IsNullOrWhiteSpace(cc.CharacterId))
            return ChangeHandlerResult.Failure("characterId is required.");

        var existing = await context.Session.LoadAsync<Character>(cc.CharacterId, ct);
        if (existing != null)
            return ChangeHandlerResult.Failure($"Character {cc.CharacterId} already exists.");

        var newChar = new Character
        {
            Id = cc.CharacterId,
            Name = cc.Name ?? "Unnamed",
            Notes = cc.Notes,
            CurrentLocationId = cc.CurrentLocationId,
            CurrentActivity = cc.CurrentActivity,
            KeepAlive = cc.KeepAlive,
            Schedule = cc.Schedule,
            Psychology = cc.Psychology ?? new PsychologyProfile()
        };

        if (string.IsNullOrEmpty(newChar.CampaignName))
            newChar.CampaignName = context.CampaignName;

        await context.Session.StoreAsync(newChar, ct);
        context.RegisterNewCharacter(newChar);

        return ChangeHandlerResult.Ok;
    }
}

public class ScheduleChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ScheduleChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var sc = (ScheduleChange)change;
        if (!context.Characters.TryGetValue(sc.CharacterId, out var c))
        {
            c = await context.Session.LoadAsync<Character>(sc.CharacterId, ct);
            if (c == null) return ChangeHandlerResult.Failure($"Character {sc.CharacterId} not found.");
            context.RegisterNewCharacter(c);
        }

        c.Schedule = sc.Schedule;

        return ChangeHandlerResult.Ok;
    }
}
