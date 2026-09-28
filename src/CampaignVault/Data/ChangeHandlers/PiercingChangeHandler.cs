using CampaignVault.Events;
using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Add, update, remove and clear body piercings for <see cref="PiercingChange"/> on a character.
/// Publishes <see cref="CoreEvents.Pierced"/> for every mark that changed.
/// </summary>
public sealed class PiercingChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is PiercingChange;

    public bool ExtractInvolvedEntities(
        WorldChange change,
        HashSet<string>? characterIds = null,
        HashSet<string>? locationIds = null,
        HashSet<string>? factionIds = null,
        HashSet<string>? questIds = null,
        HashSet<string>? itemIds = null,
        HashSet<string>? allInvolvedIds = null)
    {
        if (change is not PiercingChange { CharacterId: { Length: > 0 } id })
            return change is PiercingChange;

        if (id.StartsWith("chars/", StringComparison.Ordinal))
            characterIds?.Add(id);
        allInvolvedIds?.Add(id);
        return true;
    }

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var req = (PiercingChange)change;
        var action = (req.Action ?? "add").Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(req.CharacterId))
            return ChangeHandlerResult.Failure("characterId is required.");

        var characterId = req.CharacterId.Trim();
        var character = await ResolveCharacterAsync(ctx, characterId, ct);
        if (character is null)
            return ChangeHandlerResult.Failure($"{characterId} is not a known character.");

        character.Piercings ??= [];
        EnsureIds(character.Piercings);
        var day = (int)(await ctx.GetCurrentTimeAsync()).TotalDaysElapsed;

        return action switch
        {
            "add" => Add(req, character, characterId, day, ctx),
            "update" => Update(req, character, characterId, day, ctx),
            "remove" => Remove(req, character, characterId, ctx),
            "clear_site" => ClearSite(req, character, characterId, ctx),
            "clear_all" => ClearAll(character, characterId, ctx),
            _ => ChangeHandlerResult.Failure(
                "action must be add, update, remove, clear_site, or clear_all."),
        };
    }

    /// <summary>Assigns numeric ids to legacy marks that predate stacking.</summary>
    private static void EnsureIds(List<PiercingMark> piercings)
    {
        foreach (var mark in piercings.Where(p => string.IsNullOrWhiteSpace(p.Id)))
            mark.Id = PiercingHelpers.NextId(piercings);
    }

    private static ChangeHandlerResult Add(
        PiercingChange req, Character character, string characterId, int day, ChangeContext ctx)
    {
        var site = PiercingHelpers.NormalizeSite(req.Site);
        var kind = PiercingHelpers.NormalizeKind(req.Kind);
        if (site is null)
            return ChangeHandlerResult.Failure("site is required for action=add.");
        if (kind is null)
            return ChangeHandlerResult.Failure("kind is required for action=add.");
        if (site.Length > PiercingHelpers.MaxSiteLength)
            return ChangeHandlerResult.Failure($"site must be at most {PiercingHelpers.MaxSiteLength} characters.");
        if (kind.Length > PiercingHelpers.MaxKindLength)
            return ChangeHandlerResult.Failure($"kind must be at most {PiercingHelpers.MaxKindLength} characters.");

        var outcome = PiercingHelpers.Add(
            character.Piercings, site, kind, day, req.Material, req.Load, req.Tags, req.Note, req.AppliedBy,
            id: req.Id, replace: req.Replace);
        if (outcome.Error is not null)
            return ChangeHandlerResult.Failure(outcome.Error);
        Publish(ctx, characterId, outcome.Action, outcome.Mark!);
        var evicted = outcome.Evicted is null ? "" : $" ({PiercingHelpers.Phrase(outcome.Evicted)} faded to make room)";
        var idNote = string.IsNullOrWhiteSpace(outcome.Mark!.Id) ? "" : $" id={outcome.Mark.Id}";
        ctx.RecordMessage(
            $"{character.Name}: {outcome.Action} {PiercingHelpers.Phrase(outcome.Mark)}{idNote}; now {PiercingHelpers.Summarize(character.Piercings)}.{evicted}");
        return ChangeHandlerResult.Ok;
    }

    private static ChangeHandlerResult Update(
        PiercingChange req, Character character, string characterId, int day, ChangeContext ctx)
    {
        var site = PiercingHelpers.NormalizeSite(req.Site);
        var kind = PiercingHelpers.NormalizeKind(req.Kind);
        if (string.IsNullOrWhiteSpace(req.Id) && (site is null || kind is null))
            return ChangeHandlerResult.Failure("update needs id, or both site and kind.");

        PiercingOutcome outcome;
        try
        {
            outcome = PiercingHelpers.Update(
                character.Piercings, site, kind, day, req.Material, req.Load, req.Tags, req.Note, req.AppliedBy,
                req.ReplaceTags, id: req.Id);
        }
        catch (ArgumentException ex)
        {
            return ChangeHandlerResult.Failure(ex.Message);
        }

        if (outcome.Error is not null)
            return ChangeHandlerResult.Failure(outcome.Error);
        if (outcome.Mark is null)
            return ChangeHandlerResult.Failure(
                string.IsNullOrWhiteSpace(req.Id)
                    ? $"{character.Name} has no {kind} at {site}."
                    : $"{character.Name} has no piercing id '{req.Id}'.");

        Publish(ctx, characterId, outcome.Action, outcome.Mark);
        ctx.RecordMessage(
            $"{character.Name}: updated {PiercingHelpers.Phrase(outcome.Mark)} id={outcome.Mark.Id}; now {PiercingHelpers.Summarize(character.Piercings)}.");
        return ChangeHandlerResult.Ok;
    }

    private static ChangeHandlerResult Remove(
        PiercingChange req, Character character, string characterId, ChangeContext ctx)
    {
        var site = PiercingHelpers.NormalizeSite(req.Site);
        var kind = PiercingHelpers.NormalizeKind(req.Kind);
        if (string.IsNullOrWhiteSpace(req.Id) && site is null && kind is null)
            return ChangeHandlerResult.Failure(
                "remove needs id, and/or site and/or kind (or use clear_all). Locked piercings need force:true.");

        var (removed, locked) = PiercingHelpers.Remove(character.Piercings, site, kind, req.Force, id: req.Id);
        if (removed.Count == 0 && locked.Count == 0)
        {
            ctx.RecordMessage($"piercing: {character.Name} had no matching piercing (no-op).");
            return ChangeHandlerResult.Ok;
        }

        if (removed.Count == 0 && locked.Count > 0)
            return ChangeHandlerResult.Failure(
                $"{character.Name}: {locked.Count} locked piercing(s) blocked remove — pass force:true (key/smith/story).");

        foreach (var mark in removed)
            Publish(ctx, characterId, "removed", mark);

        var lockedNote = locked.Count == 0
            ? ""
            : $" ({locked.Count} locked left in place — force:true to remove)";
        ctx.RecordMessage(
            $"{character.Name}: removed {removed.Count} piercing(s); now {PiercingHelpers.Summarize(character.Piercings) ?? "none"}.{lockedNote}");
        return ChangeHandlerResult.Ok;
    }

    private static ChangeHandlerResult ClearSite(
        PiercingChange req, Character character, string characterId, ChangeContext ctx)
    {
        var site = PiercingHelpers.NormalizeSite(req.Site);
        if (site is null)
            return ChangeHandlerResult.Failure("site is required for action=clear_site.");

        var (removed, locked) = PiercingHelpers.Remove(character.Piercings, site, kind: null, req.Force);
        if (removed.Count == 0 && locked.Count == 0)
        {
            ctx.RecordMessage($"piercing: {character.Name} had nothing at {site} (no-op).");
            return ChangeHandlerResult.Ok;
        }

        if (removed.Count == 0 && locked.Count > 0)
            return ChangeHandlerResult.Failure(
                $"{character.Name}: locked piercing(s) at {site} — pass force:true to clear.");

        foreach (var mark in removed)
            Publish(ctx, characterId, "removed", mark);

        ctx.RecordMessage(
            $"{character.Name}: cleared site {site} ({removed.Count}); now {PiercingHelpers.Summarize(character.Piercings) ?? "none"}.");
        return ChangeHandlerResult.Ok;
    }

    private static ChangeHandlerResult ClearAll(Character character, string characterId, ChangeContext ctx)
    {
        var (removed, locked) = PiercingHelpers.Remove(character.Piercings, site: null, kind: null, force: true);
        foreach (var mark in removed)
            Publish(ctx, characterId, "removed", mark);
        // force:true already empties locked too; locked list will be empty.
        _ = locked;
        ctx.RecordMessage($"{character.Name}: cleared all piercings ({removed.Count}).");
        return ChangeHandlerResult.Ok;
    }

    private static void Publish(ChangeContext ctx, string characterId, string action, PiercingMark mark) =>
        ctx.Publish(CoreEvents.Pierced, new Dictionary<string, object?>
        {
            [CoreEvents.Fields.CharacterId] = characterId,
            [CoreEvents.Fields.Action] = action,
            [CoreEvents.Fields.PiercingId] = mark.Id,
            [CoreEvents.Fields.Site] = mark.Site,
            [CoreEvents.Fields.Kind] = mark.Kind,
            [CoreEvents.Fields.Load] = mark.Load,
            [CoreEvents.Fields.Tags] = mark.Tags,
            [CoreEvents.Fields.AppliedBy] = mark.AppliedBy,
        });

    private static async Task<Character?> ResolveCharacterAsync(
        ChangeContext ctx, string id, CancellationToken ct)
    {
        if (ctx.Characters.TryGetValue(id, out var c))
            return c;
        if (ctx.Session is null)
            return null;
        if (!id.StartsWith("chars/", StringComparison.Ordinal))
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
