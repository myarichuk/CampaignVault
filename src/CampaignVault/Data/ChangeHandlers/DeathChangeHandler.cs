using CampaignVault.Events;
using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Records a character's death, or undoes it (<c>revive</c>), for <see cref="DeathChange"/>. Death is explicit:
/// 0 HP alone is downed, never dead. Publishes <see cref="CoreEvents.CharacterDied"/> once per death.
/// </summary>
public sealed class DeathChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is DeathChange;

    public bool ExtractInvolvedEntities(
        WorldChange change,
        HashSet<string>? characterIds = null,
        HashSet<string>? locationIds = null,
        HashSet<string>? factionIds = null,
        HashSet<string>? questIds = null,
        HashSet<string>? itemIds = null,
        HashSet<string>? allInvolvedIds = null)
    {
        if (change is not DeathChange dc)
            return false;

        foreach (var id in new[] { dc.CharacterId, dc.KillerId })
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (id.StartsWith("chars/", StringComparison.Ordinal))
                characterIds?.Add(id);
            allInvolvedIds?.Add(id);
        }

        if (!string.IsNullOrWhiteSpace(dc.NewLocationId))
        {
            locationIds?.Add(dc.NewLocationId);
            allInvolvedIds?.Add(dc.NewLocationId);
        }

        return true;
    }

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var req = (DeathChange)change;

        if (string.IsNullOrWhiteSpace(req.CharacterId))
            return ChangeHandlerResult.Failure("characterId is required.");

        var characterId = req.CharacterId.Trim();
        var character = await ResolveCharacterAsync(ctx, characterId, ct);
        if (character is null)
            return ChangeHandlerResult.Failure($"{characterId} is not a known character.");

        return req.Revive
            ? Revive(req, character, ctx)
            : await DieAsync(req, character, ctx, ct);
    }

    internal static async Task<ChangeHandlerResult> DieAsync(
        DeathChange req, Character character, ChangeContext ctx, CancellationToken ct)
    {
        if (character.IsDead)
        {
            ctx.RecordMessage($"{character.Id} is already dead (day {character.Death!.Day}); death not recorded twice.");
            return ChangeHandlerResult.Ok;
        }

        var day = (int)(await ctx.GetCurrentTimeAsync()).TotalDaysElapsed;
        var killerId = string.IsNullOrWhiteSpace(req.KillerId) ? null : req.KillerId.Trim();
        character.Death = new DeathRecord
        {
            Day = day,
            Cause = string.IsNullOrWhiteSpace(req.Cause) ? null : req.Cause.Trim(),
            KillerId = killerId,
            BodyLocationId = character.CurrentLocationId,
        };

        character.CurrentHp = 0;
        character.DeathSaves = null;

        // A corpse is not "present": clearing the live location drops it from location queries, and with no
        // schedule the simulation never routes it anywhere again. The body is found via Death.BodyLocationId.
        // A dead PC keeps its location and flags: the party's own position is derived from its PCs, and the
        // player, not the engine, decides what a dead PC's death means for the run.
        character.CurrentActivity = null;
        character.Schedule = null;
        character.IdleSceneBeats = 0;
        character.IdleSceneLocationId = null;
        if (!character.IsPc)
        {
            character.CurrentLocationId = null;
            character.KeepAlive = false;
            character.IsPartyCompanion = false;
        }

        // Whatever they were bound to or summoning ends with them.
        var lapseMessages = new List<string>();
        await MinionLapse.LapseCasterMinionsAsync(
            ctx.Session, ctx.Characters, character, concentrationOnly: false, lapseMessages, ct);
        foreach (var message in lapseMessages)
            ctx.RecordMessage(message);

        ctx.RecordMessage(
            $"{character.Name} ({character.Id}) died"
            + (character.Death.Cause is { } cause ? $": {cause}" : "")
            + (character.Death.BodyLocationId is { } body ? $". Body at {body}." : ".")
            + (character.IsPc ? " This is a player character: the player decides what happens next (new character, revival, end of run)." : ""));

        ctx.Publish(CoreEvents.CharacterDied, new Dictionary<string, object?>
        {
            [CoreEvents.Fields.CharacterId] = character.Id,
            [CoreEvents.Fields.Cause] = character.Death.Cause,
            [CoreEvents.Fields.KillerId] = killerId,
            [CoreEvents.Fields.BodyLocationId] = character.Death.BodyLocationId,
            [CoreEvents.Fields.Day] = day,
        });

        return ChangeHandlerResult.Ok;
    }

    private static ChangeHandlerResult Revive(DeathChange req, Character character, ChangeContext ctx)
    {
        if (!character.IsDead)
            return ChangeHandlerResult.Failure($"{character.Id} is not dead; nothing to revive.");

        var bodyLocation = character.Death!.BodyLocationId;
        character.Death = null;
        character.CurrentHp = character.MaxHp > 0
            ? Math.Clamp(req.Hp ?? 1, 1, character.MaxHp)
            : Math.Max(req.Hp ?? 1, 1);
        character.CurrentLocationId = string.IsNullOrWhiteSpace(req.NewLocationId)
            ? bodyLocation ?? character.CurrentLocationId
            : req.NewLocationId.Trim();

        ctx.RecordMessage(
            $"{character.Name} ({character.Id}) revived at {character.CurrentHp}/{character.MaxHp} HP"
            + (character.CurrentLocationId is { } loc ? $" at {loc}." : "."));
        return ChangeHandlerResult.Ok;
    }

    private static async Task<Character?> ResolveCharacterAsync(
        ChangeContext ctx, string id, CancellationToken ct)
    {
        if (ctx.Characters.TryGetValue(id, out var c))
            return c;
        if (ctx.Session is null || !id.StartsWith("chars/", StringComparison.Ordinal))
            return null;
        var loaded = await ctx.Session.LoadAsync<Character>(id, ct);
        if (loaded is null)
            return null;
        if (!string.IsNullOrEmpty(ctx.CampaignName) &&
            !CampaignEntityVisibility.IsVisibleInCampaign(loaded.CampaignName, ctx.CampaignName))
            return null;
        ctx.RegisterNewCharacter(loaded);
        return loaded;
    }
}
