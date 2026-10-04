using System.Linq;
using CampaignVault.Models;
using CampaignVault.Data.Templates;
using CampaignVault.Rulesets;
using CampaignVault.Services;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles both adding and removing character status effects.
///
/// Design decisions (Phase 1, June 2026):
/// - StatusEffect is an LLM-authored structured object (name, category, affectedPart,
///   statModifiers, expiration, recoveryHint). Replaces the old flat List&lt;string&gt;.
/// - Add (StatusChange.StatusEffect): appends a new StatusEffect to SystemStats.StatusEffects.
///   Duplicates (same Name) are allowed — the LLM may stack identical condition names (e.g. two
///   separate Frightened sources). De-duplication is the LLM DM's responsibility.
/// - Remove (StatusRemove.Status): removes ALL StatusEffects whose Name matches case-insensitively.
/// - Legacy path: StatusChange.Status (plain string) is still accepted for backward compatibility
///   and creates a minimal StatusEffect with just a Name and Category="Condition" (AppliedBy marks the path).
/// - Auto-expiry (ExpiresAtDay / ExpiresAtRound) is enforced by AdvanceWorldAsync / CombatEncounter
///   advancement, not here.
/// </summary>
public sealed class StatusChangeHandler : IWorldChangeHandler
{
    private readonly ConditionDefinitionProvider _conditionProvider;

    public StatusChangeHandler(ConditionDefinitionProvider conditionProvider)
    {
        _conditionProvider = conditionProvider ?? throw new ArgumentNullException(nameof(conditionProvider));
    }

    public bool ShouldHandle(WorldChange change)
        => change is StatusChange or StatusRemove;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        return change switch
        {
            StatusChange add    => await HandleAdd(add, ctx, ct),
            StatusRemove remove => await HandleRemove(remove, ctx, ct),
            _                  => ChangeHandlerResult.Failure("StatusChangeHandler received unexpected change type")
        };
    }

    // ── Add ───────────────────────────────────────────────────────────────────

    private async Task<ChangeHandlerResult> HandleAdd(StatusChange add, IChangeContext context, CancellationToken ct)
    {
        var ctx = (ChangeContext)context;
        if (!context.Characters.TryGetValue(add.CharacterId, out var character))
        {
            character = await ctx.Session.LoadAsync<Character>(add.CharacterId, ct);
            if (character == null)
            {
                var hints = await context.SuggestCharacterMatchAsync(add.CharacterId);
                var msg = $"Character {add.CharacterId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                context.RecordMessage($"WARNING: {msg}");
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewCharacter(character);
        }

        character.SystemStats ??= new SystemExtension();
        character.SystemStats.StatusEffects ??= [];

        StatusEffect effect;

        if (add.Effect is not null)
        {
            // Preferred path: fully structured StatusEffect from LLM DM
            effect = add.Effect;
            RecordConditionValidationWarning(context, character.SystemStats, effect);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(add.Status))
            {
                return ChangeHandlerResult.Failure(
                    "Neither 'effect' nor 'status' was provided — a status change needs one of the two.");
            }

            // Legacy fallback: plain string Status → minimal StatusEffect
            effect = new StatusEffect
            {
                Name     = add.Status,
                // Plain-string statuses are conditions ("Poisoned"); this text reaches the story log.
                Category = "Condition",
                AppliedBy = "legacy-status-change"
            };
        }

        if (ImmuneBy(character.SystemStats, effect) is { } blocker)
        {
            var refusal = $"{character.Name} is immune to '{effect.Name}' (from {blocker}); not applied.";
            context.RecordMessage($"WARNING: {refusal}");
            context.RecordFailure();
            return ChangeHandlerResult.Failure(refusal);
        }

        // Same effect from both origins in one batch (the LLM restates the status a spell's ruleset_action
        // already auto-applied, or vice versa) collapses to one effect instead of stacking or failing the
        // batch. The engine's copy wins: it carries the mechanics. Deliberate stacking from a single origin
        // (two separate Bleeding wounds) is untouched. Checked before the concentration break so a collapse
        // never reports the effect as "broken" by itself.
        var isAuto = ctx.AutoApplyDepth > 0;
        var originKey = $"{add.CharacterId}|{effect.Name.Trim()}";
        if (ctx.BatchStatusOrigins.TryGetValue(originKey, out var firstWasAuto) && firstWasAuto != isAuto)
        {
            var existingIndex = character.SystemStats.StatusEffects.FindLastIndex(e =>
                string.Equals(e.Name?.Trim(), effect.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                if (isAuto)
                {
                    character.SystemStats.StatusEffects[existingIndex] = effect;
                }

                context.RecordMessage($"'{effect.Name}' on {add.CharacterId} is already applied by the ruleset action in this batch; kept one copy.");
                return ChangeHandlerResult.Ok;
            }
        }

        ctx.BatchStatusOrigins.TryAdd(originKey, isAuto);

        // A character can only concentrate on one effect at a time — casting a new
        // concentration effect breaks whatever it was previously concentrating on.
        if (ConcentrationRules.IsConcentration(effect))
        {
            var broken = character.SystemStats.StatusEffects
                .Where(ConcentrationRules.IsConcentration)
                .ToList();
            foreach (var priorEffect in broken)
            {
                character.SystemStats.StatusEffects.Remove(priorEffect);
                context.RecordMessage($"Concentration on '{priorEffect.Name}' broken for {add.CharacterId} by casting '{effect.Name}'.");
            }
        }

        character.SystemStats.StatusEffects.Add(effect);
        context.RecordPhysicalStateNudge($"{character.Name} is now affected by '{effect.Name}'.");

        var category = string.IsNullOrWhiteSpace(effect.Category) ? string.Empty : $" ({effect.Category})";
        await LogStatusEventAsync(context, character, $"{character.Name} gained status '{effect.Name}'{category}.");

        await ApplyConditionConsequencesAsync(ctx, character, effect, ct);

        return ChangeHandlerResult.Ok;
    }

    private static string SystemOf(Character character) =>
        character.SystemStats is not null && RulesetSystemResolver.TryFromStats(character.SystemStats, out var system) ? system! : "";

    /// <summary>
    /// What a condition does to the bearer the moment it lands: a lowered hit point maximum pulls current HP down to it
    /// (exhaustion 4), and a <c>death</c> effect at its level kills (exhaustion 6), whichever way the level got there.
    /// </summary>
    private async Task ApplyConditionConsequencesAsync(ChangeContext ctx, Character character, StatusEffect effect, CancellationToken ct)
    {
        var cap = ConditionMechanics.EffectiveMaxHp(character, _conditionProvider);
        if (character.MaxHp > 0 && character.CurrentHp > cap)
        {
            character.CurrentHp = cap;
            ctx.RecordMessage($"{character.Name}'s hit point maximum is now {cap} ({effect.Name}); current HP lowered to match.");
        }

        // A grappler who can no longer act lets go.
        if (ConditionMechanics.TryResolve(_conditionProvider.GetConditionsForSystem(SystemOf(character)), effect, out var def, out var level)
            && def.EffectsAt(level).Any(e => e.Kind == ConditionEffectKinds.BlocksActions))
        {
            var found = new Dictionary<string, Character>(ctx.Characters);
            foreach (var relation in character.SystemStats!.EngagementRelations
                         .Where(r => string.Equals(r.Verb, EngagementMutationHelper.GrapplingVerb, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (!found.ContainsKey(relation.TargetId) && ctx.Session is not null)
                {
                    var loaded = await ctx.Session.LoadAsync<Character>(relation.TargetId, ct);
                    if (loaded is not null)
                    {
                        ctx.RegisterNewCharacter(loaded);
                        found[loaded.Id] = loaded;
                    }
                }
            }

            var released = EngagementMutationHelper.ReleaseHeldBy(character, id => found.GetValueOrDefault(id));
            if (released.Count > 0)
                ctx.RecordMessage($"{character.Name} is {effect.Name} and lets go of {string.Join(", ", released)}.");
        }

        if (character.IsDead || ConditionMechanics.Fatal(character, _conditionProvider) is not { } fatal)
            return;

        await DeathChangeHandler.DieAsync(
            new DeathChange { CharacterId = character.Id, Cause = fatal.Definition.Name }, character, ctx, ct);
    }

    /// <summary>
    /// The name of an active condition that makes the character immune to <paramref name="incoming"/> (its definition's
    /// <c>immunities</c>), or null. Matching ignores case, spaces and punctuation, against the incoming condition's definition
    /// name and its own name.
    /// </summary>
    private string? ImmuneBy(SystemExtension stats, StatusEffect incoming)
    {
        if (stats.StatusEffects.Count == 0 || !RulesetSystemResolver.TryFromStats(stats, out var system))
            return null;

        var holder = new Character { SystemStats = stats };
        var names = new HashSet<string> { StatusEffectModifierProvider.Normalize(incoming.Name) };
        if (!string.IsNullOrWhiteSpace(incoming.ConditionName))
            names.Add(StatusEffectModifierProvider.Normalize(incoming.ConditionName));
        if (ConditionMechanics.TryResolve(_conditionProvider.GetConditionsForSystem(system!), incoming, out var def, out _))
            names.Add(StatusEffectModifierProvider.Normalize(def.Name));

        foreach (var active in ConditionMechanics.Active(holder, _conditionProvider, system))
        {
            if (active.Definition.Immunities.Any(i => names.Contains(StatusEffectModifierProvider.Normalize(i))))
                return active.Definition.Name;
        }

        return null;
    }

    // Status effects (restrained, poisoned, etc.) often gate what actions are legal, so their history
    // is Important, not Trivial. Auto-logged so recall_history/NpcRecentEvents can surface it without a
    // second, separate `event` commit for the same narrative beat.
    private static async Task LogStatusEventAsync(IChangeContext context, Character character, string summary)
    {
        var ctx = (ChangeContext)context;
        await context.LogEventAsync(new Event
        {
            Id = "events/" + Guid.NewGuid(),
            Summary = summary,
            Category = EventCategory.Interaction,
            Importance = MemoryImportance.Important,
            Involved = [character.Id],
            LocationId = character.CurrentLocationId,
            DayLogged = (await context.GetCurrentTimeAsync()).TotalDaysElapsed,
            CampaignName = context.CampaignName,
        });
    }

    private void RecordConditionValidationWarning(
        IChangeContext context,
        SystemExtension stats,
        StatusEffect effect)
    {
        var ctx = (ChangeContext)context;
        if (string.IsNullOrWhiteSpace(effect.ConditionName))
            return;

        if (!RulesetSystemResolver.TryFromStats(stats, out var system))
        {
            context.RecordMessage(
                $"[WARNING] conditionName '{effect.ConditionName}' could not be validated — unknown system stats type. " +
                "Effect was applied with expiresAtDay/expiresAtRound heuristics only.");
            return;
        }

        if (_conditionProvider.TryGet(system!, effect.ConditionName, out _))
            return;

        const int maxSample = 8;
        var knownNames = _conditionProvider.GetConditionsForSystem(system!).Keys
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sample = string.Join(", ", knownNames.Take(maxSample));
        var overflow = knownNames.Count > maxSample
            ? $" (+{knownNames.Count - maxSample} more via get_system_handbook)"
            : string.Empty;

        context.RecordMessage(
            $"[WARNING] conditionName '{effect.ConditionName}' did not match any known {system} condition definition. " +
            $"Sample conditions: {sample}{overflow}. " +
            "Effect was applied, but expiry will fall back to expiresAtDay/expiresAtRound heuristics.");
    }

    // ── Remove ────────────────────────────────────────────────────────────────

    private async Task<ChangeHandlerResult> HandleRemove(StatusRemove remove, IChangeContext context, CancellationToken ct)
    {
        var ctx = (ChangeContext)context;
        if (!context.Characters.TryGetValue(remove.CharacterId, out var character))
        {
            character = await ctx.Session.LoadAsync<Character>(remove.CharacterId, ct);
            if (character == null)
            {
                var hints = await context.SuggestCharacterMatchAsync(remove.CharacterId);
                var msg = $"Character {remove.CharacterId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                context.RecordMessage($"WARNING: {msg}");
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewCharacter(character);
        }

        character.SystemStats ??= new SystemExtension();
        character.SystemStats.StatusEffects ??= [];

        var toRemove = remove.Status;
        var originalCount = character.SystemStats.StatusEffects.Count;

        // Case-insensitive removal of all matching status effects
        character.SystemStats.StatusEffects.RemoveAll(e =>
            string.Equals(e.Name, toRemove, StringComparison.OrdinalIgnoreCase));

        var removedCount = originalCount - character.SystemStats.StatusEffects.Count;

        if (removedCount > 0)
        {
            context.RecordPhysicalStateNudge($"{character.Name} is no longer affected by '{remove.Status}'.");
            await LogStatusEventAsync(context, character, $"{character.Name} lost status '{remove.Status}'.");
        }
        else
        {
            context.RecordMessage($"StatusRemove: '{remove.Status}' was not present on {remove.CharacterId} (no-op)");
        }

        // Removing a non-existent status is harmless (idempotent)
        return ChangeHandlerResult.Ok;
    }
}