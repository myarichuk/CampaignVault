using System.Linq;
using CampaignVault.Events;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles HpChange using the pre-loaded character (safe pattern).
/// </summary>
public sealed class HpChangeHandler(IRollService rollService, ConditionDefinitionProvider? conditions = null) : IWorldChangeHandler
{
    private readonly IRollService _rollService = rollService ?? throw new ArgumentNullException(nameof(rollService));

    public bool ShouldHandle(WorldChange change) => change is HpChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var hp = (HpChange)change;

        if (!ctx.Characters.TryGetValue(hp.CharacterId, out var character))
        {
            character = await ctx.Session.LoadAsync<Character>(hp.CharacterId, ct);
            if (character == null)
            {
                var hints = await ctx.SuggestCharacterMatchAsync(hp.CharacterId);
                var msg = $"Character {hp.CharacterId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                ctx.RecordMessage($"WARNING: {msg}");
                ctx.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            ctx.RegisterNewCharacter(character);
        }

        if (character.MaxHp <= 0)
        {
            ctx.Logger.LogWarning("HpChange skipped for {CharacterId}: MaxHp is {MaxHp} (not a combatant?)", hp.CharacterId, character.MaxHp);
            ctx.RecordMessage($"WARNING: HpChange skipped for {hp.CharacterId} — MaxHp is {character.MaxHp}. Set MaxHp > 0 to enable HP tracking.");
            ctx.RecordFailure();
            return ChangeHandlerResult.Failure();
        }

        if (character.IsDead && hp.Delta > 0)
        {
            var deadMsg = $"{hp.CharacterId} is dead (day {character.Death!.Day}); healing can't reach them. " +
                "To bring them back commit {\"$type\": \"death\", \"characterId\": \"" + hp.CharacterId + "\", \"revive\": true}.";
            ctx.RecordMessage($"WARNING: {deadMsg}");
            ctx.RecordFailure();
            return ChangeHandlerResult.Failure(deadMsg);
        }

        var attackerId = FindAttackerId(ctx, character.Id);
        if (hp.Delta < 0)
        {
            await DamageApplication.ApplyAsync(
                character, -hp.Delta, hp.Critical, attackerId, new ContextDamageSink(ctx), _rollService, announceHp: true, ct);
            return ChangeHandlerResult.Ok;
        }

        // Healing (or a zero delta): temporary hit points are not restored, and rising above 0 ends the death saves.
        var hadTally = character.DeathSaves != null;
        var transition = HitPointState.Set(character, character.CurrentHp + hp.Delta, ConditionMechanics.EffectiveMaxHp(character, conditions));
        ctx.RecordMessage($"HP adjusted for {hp.CharacterId} by {hp.Delta} (now {character.CurrentHp}/{character.MaxHp})");
        if (transition.Wounded is { } wounded)
        {
            ctx.RecordMessage($"{character.Name} is conscious again; dying ends and they are now wounded {wounded}.");
        }

        if (hp.Delta > 0 && hadTally && character.CurrentHp > 0 && HitPointState.UsesDeathSaves(character))
        {
            ctx.RecordMessage($"{character.Name} is conscious again; death saves cleared.");
        }

        return ChangeHandlerResult.Ok;
    }

    /// <summary>The handler's context as a <see cref="DamageApplication"/> sink.</summary>
    private sealed class ContextDamageSink(ChangeContext ctx) : IDamageSink
    {
        public void Message(string text) => ctx.RecordMessage(text);

        public void Publish(string topic, Dictionary<string, object?> data) => ctx.Publish(topic, data);

        public Task LapseMinionsAsync(Character caster, bool boundOnly, CancellationToken ct) =>
            HpChangeHandler.LapseMinionsAsync(ctx, caster, boundOnly, ct);

        public async Task<RollResolution> ResolveRollAsync(RollQuery query, int baseBonus, CancellationToken ct) =>
            ctx.ResolveRollModifiers(query with { Options = await ctx.GetSystemOptionsAsync() }, baseBonus);

        public Task DieAsync(Character character, string cause, string? killerId, CancellationToken ct) =>
            DeathChangeHandler.DieAsync(
                new DeathChange { CharacterId = character.Id, Cause = cause, KillerId = killerId }, character, ctx, ct);
    }

    /// <summary>
    /// The actor of the top-level ruleset_action that targets this character, if that is what we are inside.
    /// Only at event depth 0: during event delivery BatchIndex still points at the original change, so a
    /// plugin's reaction damage would otherwise be credited to the original attacker.
    /// </summary>
    private static string? FindAttackerId(ChangeContext ctx, string characterId)
    {
        if (ctx.EventDepth != 0 || ctx.Batch is not { } batch || ctx.BatchIndex < 0 || ctx.BatchIndex >= batch.Count)
        {
            return null;
        }

        return batch[ctx.BatchIndex] is RulesetAction ra && ra.TargetIds.Contains(characterId, StringComparer.OrdinalIgnoreCase)
            ? ra.CharacterId
            : null;
    }

    private static async Task LapseMinionsAsync(
        ChangeContext ctx, Character caster, bool boundOnly, CancellationToken ct)
    {
        var messages = new List<string>();
        await MinionLapse.LapseCasterMinionsAsync(
            ctx.Session, ctx.Characters, caster, boundOnly, messages, ct);
        foreach (var message in messages)
        {
            ctx.RecordMessage(message);
        }
    }

    /// <summary>Shared with DelayedTickProcessor so turn-start tick damage breaks concentration identically.</summary>
    internal static (int Modifier, string SaveLabel) GetConcentrationSaveModifier(SystemExtension stats)
    {
        if (stats is Dnd5eExtension dnd5e)
        {
            var matchedKey = dnd5e.SavingThrowModifiers.Keys
                .FirstOrDefault(k => string.Equals(k, "Constitution", StringComparison.OrdinalIgnoreCase));
            if (matchedKey != null && dnd5e.SavingThrowModifiers.TryGetValue(matchedKey, out var saveMod))
            {
                return (saveMod, "CON");
            }

            return (dnd5e.GetAbilityModifier(dnd5e.Constitution), "CON");
        }

        if (stats is Pf2eExtension pf2e)
        {
            var matchedKey = pf2e.SavingThrowModifiers.Keys
                .FirstOrDefault(k => string.Equals(k, "Fortitude", StringComparison.OrdinalIgnoreCase));
            if (matchedKey != null && pf2e.SavingThrowModifiers.TryGetValue(matchedKey, out var saveMod))
            {
                return (saveMod, "Fortitude");
            }

            return (pf2e.ConstitutionMod, "Fortitude");
        }

        // Ruleset not yet wired for concentration saves (e.g. Narrative) — no modifier to apply.
        return (0, "CON");
    }
}