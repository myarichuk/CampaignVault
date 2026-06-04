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

        var existing = context.Session != null ? await context.Session.LoadAsync<Character>(cc.CharacterId, ct) : null;
        if (existing != null)
        {
            existing.Name = cc.Name ?? existing.Name;
            if (cc.Notes != null) existing.Notes = cc.Notes;
            if (cc.CurrentLocationId != null) existing.CurrentLocationId = cc.CurrentLocationId;
            if (cc.CurrentActivity != null) existing.CurrentActivity = cc.CurrentActivity;
            if (cc.KeepAlive) existing.KeepAlive = cc.KeepAlive;
            if (cc.Schedule != null) existing.Schedule = cc.Schedule;
            if (cc.Psychology != null) existing.Psychology = cc.Psychology;
            
            context.RecordMessage($"Warning: Character {cc.CharacterId} already exists. Updated existing character fields.");
            return ChangeHandlerResult.Ok;
        }

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
            c = context.Session != null ? await context.Session.LoadAsync<Character>(sc.CharacterId, ct) : null;
            if (c == null)
            {
                var hints = await context.SuggestCharacterMatchAsync(sc.CharacterId);
                var msg = $"Character {sc.CharacterId} not found.";
                if (hints != null) msg += $" Did you mean: {hints}?";
                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewCharacter(c);
        }

        c.Schedule = sc.Schedule;

        return ChangeHandlerResult.Ok;
    }
}
