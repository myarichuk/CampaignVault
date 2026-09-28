using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>Attach, detach and strain for <see cref="TetherChange"/>.</summary>
public sealed class TetherChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is TetherChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var req = (TetherChange)change;
        var action = (req.Action ?? "attach").Trim().ToLowerInvariant();
        if (action is not ("attach" or "detach" or "strain"))
            return ChangeHandlerResult.Failure("action must be attach, detach or strain.");
        if (string.IsNullOrWhiteSpace(req.SubjectId))
            return ChangeHandlerResult.Failure("subjectId is required.");

        var subject = await Load<Character>(ctx, req.SubjectId, ct);
        if (subject is null)
            return ChangeHandlerResult.Failure($"Character {req.SubjectId} not found.");
        subject.SystemStats ??= new SystemExtension();
        var tethers = subject.SystemStats.Tethers ??= [];

        return action switch
        {
            "attach" => await Attach(ctx, req, subject, tethers, ct),
            "detach" => Detach(ctx, req, subject, tethers),
            _ => await Strain(ctx, req, subject, ct),
        };
    }

    private static async Task<ChangeHandlerResult> Attach(
        ChangeContext ctx, TetherChange req, Character subject, List<Tether> tethers, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.AnchorId))
            return ChangeHandlerResult.Failure("attach requires anchorId (a character, an item, or 'fixture:<name>').");
        var anchorId = req.AnchorId.Trim();
        if (string.Equals(anchorId, subject.Id, StringComparison.OrdinalIgnoreCase))
            return ChangeHandlerResult.Failure("A subject cannot be tethered to itself.");
        if (!TetherState.IsFixture(anchorId) &&
            await Load<Character>(ctx, anchorId, ct) is null && await Load<Item>(ctx, anchorId, ct) is null)
            return ChangeHandlerResult.Failure($"Anchor {anchorId} is neither a character nor an item. Use 'fixture:<name>' for scenery.");
        if (req.HolderId is { Length: > 0 } holder && await Load<Character>(ctx, holder, ct) is null)
            return ChangeHandlerResult.Failure($"holder {holder} not found.");
        if (req.BreakDc is < 1 or > 40)
            return ChangeHandlerResult.Failure("breakDc must be 1–40.");
        if (req.SlackFeet is < 0)
            return ChangeHandlerResult.Failure("slackFeet cannot be negative.");

        var replaced = tethers.RemoveAll(t => string.Equals(t.AnchorId, anchorId, StringComparison.OrdinalIgnoreCase));
        if (replaced == 0 && tethers.Count >= TetherState.MaxPerSubject)
            return ChangeHandlerResult.Failure($"{subject.Name} already has {TetherState.MaxPerSubject} tethers; detach one first.");

        tethers.Add(new Tether
        {
            AnchorId = anchorId,
            BreakDc = req.BreakDc ?? 15,
            SlackFeet = req.SlackFeet,
            HolderId = string.IsNullOrWhiteSpace(req.HolderId) ? null : req.HolderId.Trim(),
            Label = req.Label,
            AttachedBy = ctx.EventSource,
        });
        var slack = req.SlackFeet is { } s ? $"{s} ft of slack" : "held tight";
        ctx.RecordMessage($"{subject.Name} is tethered to {anchorId}{(req.Label is null ? "" : $" ({req.Label})")}: {slack}, break DC {req.BreakDc ?? 15}.");
        ctx.RecordPhysicalStateNudge($"{subject.Name} is tied to {anchorId}.");
        return ChangeHandlerResult.Ok;
    }

    private static ChangeHandlerResult Detach(ChangeContext ctx, TetherChange req, Character subject, List<Tether> tethers)
    {
        var removed = string.IsNullOrWhiteSpace(req.AnchorId)
            ? tethers.RemoveAll(_ => true)
            : tethers.RemoveAll(t => string.Equals(t.AnchorId, req.AnchorId.Trim(), StringComparison.OrdinalIgnoreCase));
        ctx.RecordMessage(removed > 0
            ? $"{subject.Name} is released from {removed} tether(s)."
            : $"tether detach: {subject.Name} had no matching tether (no-op).");
        return ChangeHandlerResult.Ok;
    }

    private static async Task<ChangeHandlerResult> Strain(ChangeContext ctx, TetherChange req, Character subject, CancellationToken ct)
    {
        var live = await TetherState.LiveAsync(ctx, subject, ct);
        var target = string.IsNullOrWhiteSpace(req.AnchorId)
            ? live.FirstOrDefault()
            : live.FirstOrDefault(t => string.Equals(t.AnchorId, req.AnchorId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return ChangeHandlerResult.Failure($"{subject.Name} has no matching tether to strain against.");

        int d20;
        if (req.D20 is { } given)
        {
            if (given is < 1 or > 20)
                return ChangeHandlerResult.Failure("d20 must be 1–20.");
            d20 = given;
        }
        else
        {
            if (ctx.Rolls is null)
                return ChangeHandlerResult.Failure("No roll service available; pass d20.");
            var roll = await ctx.Rolls.RollAsync(new RollRequest { Tag = "tether_strain", Expression = "1d20" }, ct);
            d20 = roll.IndividualDice.FirstOrDefault(roll.Result);
        }

        var total = d20 + (req.CheckBonus ?? 0);
        var broke = total >= target.BreakDc;
        if (broke)
        {
            subject.SystemStats!.Tethers.Remove(target);
            ctx.RecordPhysicalStateNudge($"{subject.Name} breaks free of {target.AnchorId}.");
        }

        ctx.RecordMessage(
            $"{subject.Name} strains against {target.AnchorId}: {d20}{(req.CheckBonus is { } b and not 0 ? $"{b:+0;-0}" : "")} = {total} vs DC {target.BreakDc} → " +
            (broke ? "breaks free." : "holds."));
        return ChangeHandlerResult.Ok;
    }

    private static async Task<T?> Load<T>(ChangeContext ctx, string id, CancellationToken ct) where T : class
    {
        if (typeof(T) == typeof(Character) && ctx.Characters.TryGetValue(id, out var c))
            return (T)(object)c;
        if (typeof(T) == typeof(Item) && ctx.Items.TryGetValue(id, out var i))
            return (T)(object)i;
        return ctx.Session is null ? null : await ctx.Session.LoadAsync<T>(id, ct);
    }
}
