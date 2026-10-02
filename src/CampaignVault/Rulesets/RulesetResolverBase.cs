using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;

namespace CampaignVault.Rulesets;

public abstract class RulesetResolverBase<TStats> : IHostRulesetModule, IActionResolution, ICombatRuleset where TStats : SystemExtension, new()
{
    public abstract string System { get; }
    public IActionResolution Actions => this;
    public ICombatRuleset Combat => this;
    public virtual ICharacterBootstrapPipeline Bootstrap => NullCharacterBootstrapPipeline.Instance;
    public virtual IEnumerable<IRulesetPressureContributor> PressureContributors => [];

    public async Task<ResolverOutput> ResolveAsync(
        IChangeContext context, 
        RulesetAction action, 
        CancellationToken ct = default)
    {
        if (!context.Characters.TryGetValue(action.CharacterId, out var actor))
        {
            return new ResolverOutput
            {
                Result = ResolverResult.Fail("ActorNotFound",
                    $"Error: Character '{action.CharacterId}' not found or not visible in campaign '{context.CampaignName}'.")
            };
        }

        if (!string.IsNullOrEmpty(context.CampaignName)
            && !CampaignEntityVisibility.IsVisibleInCampaign(actor.CampaignName, context.CampaignName))
        {
            CampaignEntityVisibility.TryGetInvisibilityReason(actor, context.CampaignName, out var reason);
            return new ResolverOutput
            {
                Result = ResolverResult.Fail("InvalidInput",
                    $"Error: Character '{action.CharacterId}' is not available in campaign '{context.CampaignName}'. {reason}")
            };
        }

        if (actor.SystemStats is not TStats actorStats)
        {
            return new ResolverOutput { Result = ResolverResult.Fail("IncompatibleRuleset", $"Error: Character uses incompatible ruleset stats for current ActiveSystem.") };
        }

        var mutations = new List<WorldChange>();
        ResolverResult result;

        // Named class features (Second Wind, ...) are actions of any declared type; a ruleset claims them here.
        var featureResult = await TryResolveClassFeatureAsync(action, context, actorStats, mutations, ct);
        if (featureResult is not null)
        {
            return new ResolverOutput
            {
                Mutations = featureResult.Success ? mutations : Array.Empty<WorldChange>(),
                Result = featureResult
            };
        }

        switch (action.ActionType)
        {
            case RulesetActionType.Attack:
                await WeaponParameterResolver.ApplyHeldWeaponDefaultsAsync(action, context, ct);
                result = await ResolveAttackAsync(action, context, actorStats, mutations, ct);
                break;

            case RulesetActionType.SkillCheck:
                result = await ResolveSkillCheckAsync(action, context, actorStats, mutations, ct);
                break;

            case RulesetActionType.ContestedCheck:
                result = await ResolveContestedCheckAsync(action, context, actorStats, mutations, ct);
                break;

            case RulesetActionType.SavingThrow:
                result = await ResolveSavingThrowAsync(action, context, actorStats, mutations, ct);
                break;

            case RulesetActionType.Spell:
                result = await ResolveSpellAsync(action, context, actorStats, mutations, ct);
                break;

            case RulesetActionType.OpposedCheck:
                result = await ResolveContestedCheckAsync(action, context, actorStats, mutations, ct);
                break;

            case RulesetActionType.Recovery:
                result = await ResolveRecoveryAsync(action, context, actorStats, mutations, ct);
                break;

            case RulesetActionType.UseItem:
                result = await ResolveUseItemAsync(action, context, actorStats, mutations, ct);
                break;

            default:
                result = ResolverResult.Fail("NotImplemented", $"{System}: Action type {action.ActionType} not yet fully implemented.");
                break;
        }

        return new ResolverOutput
        {
            Mutations = result.Success ? mutations : Array.Empty<WorldChange>(), // Discard mutations on failure
            Result = result
        };
    }

    /// <summary>Hook for rulesets to resolve a named class feature themselves; null means "not a feature, resolve normally".</summary>
    protected virtual Task<ResolverResult?> TryResolveClassFeatureAsync(
        RulesetAction action,
        IChangeContext context,
        TStats actorStats,
        List<WorldChange> mutations,
        CancellationToken ct) => Task.FromResult<ResolverResult?>(null);

    protected abstract Task<ResolverResult> ResolveAttackAsync(
        RulesetAction action, 
        IChangeContext context, 
        TStats actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct);

    protected abstract Task<ResolverResult> ResolveSkillCheckAsync(
        RulesetAction action, 
        IChangeContext context, 
        TStats actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct);

    protected abstract Task<ResolverResult> ResolveContestedCheckAsync(
        RulesetAction action, 
        IChangeContext context, 
        TStats actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct);

    protected abstract Task<ResolverResult> ResolveSavingThrowAsync(
        RulesetAction action, 
        IChangeContext context, 
        TStats actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct);

    /// <summary>
    /// Spell lookup for the summon path. The base implementation knows no spell
    /// catalog and returns null (summon spells resolve as plain utility narration);
    /// system resolvers with a <see cref="SpellDefinitionProvider"/> override this.
    /// </summary>
    protected virtual SpellDefinition? LookupSummonSpell(RulesetAction action) => null;

    /// <summary>
    /// Handbook creature catalog for resolving a summon's <c>creatures</c> refs.
    /// Null means catalog lookups are skipped: the cast then needs an inline seed
    /// or an explicit <c>maxHp</c> parameter, and says so when neither is present.
    /// </summary>
    protected virtual CreatureDefinitionProvider? SummonCreatureProvider => null;

    /// <summary>
    /// Highest legal <c>slotLevel</c> for a summon cast: 9 for dnd5e spell slots,
    /// 10 for pf2e spell ranks.
    /// </summary>
    protected virtual int MaxSummonSlot => 9;

    /// <summary>
    /// Applies a handbook/seed defense value to fresh minion stats. No-op by
    /// default; systems with a settable armor class override this.
    /// </summary>
    protected virtual void ApplySummonedDefense(TStats stats, int defense)
    {
    }

    protected static string NormalizeSpellSlug(string name) =>
        name.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    protected virtual async Task<ResolverResult> ResolveSpellAsync(
        RulesetAction action,
        IChangeContext context,
        TStats actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        var summonSpell = LookupSummonSpell(action);
        if (summonSpell?.Summon != null
            && !TryGetParameter(action.Parameters, out _, "resolution", "spellResolution"))
        {
            return await ResolveSummonAsync(action, context, actorStats, summonSpell, mutations, ct);
        }

        var mode = SpellResolutionHelper.InferMode(action);

        switch (mode)
        {
            case SpellResolutionMode.Attack:
                return await ResolveAttackAsync(action, context, actorStats, mutations, ct);

            case SpellResolutionMode.Save:
                return await ResolveSpellSaveAsync(action, context, actorStats, mutations, ct);

            case SpellResolutionMode.Check:
                return await ResolveSkillCheckAsync(action, context, actorStats, mutations, ct);

            case SpellResolutionMode.Heal:
                return await ResolveSpellHealAsync(action, context, actorStats, mutations, ct);

            case SpellResolutionMode.Utility:
            default:
                return await ResolveSpellUtilityAsync(action, context, actorStats, mutations, ct);
        }
    }

    /// <summary>
    /// Shared summon-spell resolution (option (a) from the summoning plan): the slot
    /// comes from the action's explicit <c>slotLevel</c> parameter and falls back to
    /// the spell's base slot; the headcount comes from <c>count</c> (validated
    /// against the slot's legal options) and falls back to the strongest option;
    /// the kind comes from <c>creature</c> and falls back to the first catalog ref.
    /// Emits one <c>CharacterCreate</c> per minion plus link updates, so cast-time
    /// creation, both-ways linking, and control-cap enforcement share one
    /// implementation for PC, NPC, and enemy casters alike.
    /// </summary>
    protected virtual async Task<ResolverResult> ResolveSummonAsync(
        RulesetAction action,
        IChangeContext context,
        TStats actorStats,
        SpellDefinition spell,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        var effect = spell.Summon!;
        if (!context.Characters.TryGetValue(action.CharacterId, out var actor))
        {
            return ResolverResult.Fail("ActorNotFound",
                $"Error: Character '{action.CharacterId}' not found.");
        }

        var baseSlot = SummonBaseSlot(spell);
        var slot = baseSlot;
        if (TryGetParameter(action.Parameters, out var slotRaw, "slotLevel"))
        {
            if (!int.TryParse(slotRaw, out slot) || slot < 1 || slot > MaxSummonSlot)
            {
                return ResolverResult.Fail("InvalidParameter",
                    $"Error: '{action.ActionName}' slotLevel must be a spell-slot level 1-{MaxSummonSlot}, got '{slotRaw}'. " +
                    $"Omit it to cast at the base slot ({baseSlot}).");
            }
        }

        var spendError = SpellSlotValidator.ValidateSpend(spell, slot);
        if (spendError != null)
        {
            return ResolverResult.Fail("InvalidParameter",
                $"Error: {spendError} Cast at slot {spell.Level} or higher.");
        }

        if (TryGetParameter(action.Parameters, out var reassertRaw, "reassert")
            && bool.TryParse(reassertRaw, out var reassert) && reassert)
        {
            return await ResolveReassertAsync(action, context, actor, spell, effect, slot, mutations, ct);
        }

        var useChoices = effect.CountChoicesAtSlotLevel is { Count: > 0 };
        var candidates = SummonCandidates(effect, slot);
        var count = candidates[0];
        if (TryGetParameter(action.Parameters, out var countRaw, "count"))
        {
            if (!int.TryParse(countRaw, out count) || count < 1)
            {
                return ResolverResult.Fail("InvalidParameter",
                    $"Error: '{action.ActionName}' count must be a positive headcount, got '{countRaw}'.");
            }

            if (useChoices && !candidates.Contains(count))
            {
                return ResolverResult.Fail("InvalidParameter",
                    $"Error: '{action.ActionName}' at slot {slot} raises {string.Join(", ", candidates)} — got {count}.");
            }

            if (count > candidates.Max())
            {
                return ResolverResult.Fail("InvalidParameter",
                    $"Error: '{action.ActionName}' at slot {slot} raises at most {candidates.Max()} — got {count}.");
            }
        }

        string? chosenName = null;
        if (TryGetParameter(action.Parameters, out var creatureRaw, "creature"))
        {
            chosenName = effect.Creatures.FirstOrDefault(c =>
                string.Equals(c, creatureRaw.Trim(), StringComparison.OrdinalIgnoreCase));
            if (chosenName == null)
            {
                var known = effect.Creatures.Count > 0 ? string.Join(", ", effect.Creatures) : "nothing catalogued";
                return ResolverResult.Fail("InvalidParameter",
                    $"Error: '{action.ActionName}' can raise {known} — got '{creatureRaw}'.");
            }
        }

        chosenName ??= effect.Creatures.FirstOrDefault();

        CreatureDefinition? creatureDef = null;
        if (chosenName != null)
        {
            SummonCreatureProvider?.TryGet(System, chosenName, out creatureDef);
        }

        int? maxHp = null;
        if (TryGetParameter(action.Parameters, out var hpRaw, "maxHp"))
        {
            if (!int.TryParse(hpRaw, out var parsedHp) || parsedHp < 1)
            {
                return ResolverResult.Fail("InvalidParameter",
                    $"Error: '{action.ActionName}' maxHp must be positive hit points, got '{hpRaw}'.");
            }

            maxHp = parsedHp;
        }

        var hp = maxHp ?? creatureDef?.Hp ?? effect.InlineSeed?.Hp;
        if (hp == null || hp < 1)
        {
            return ResolverResult.Fail("MissingStatistics",
                $"Error: no statistics for '{chosenName ?? action.ActionName}' — pass maxHp (hit points) explicitly, " +
                "or add a handbook creature entry / inline seed for this summon.");
        }

        var defense = creatureDef?.Defense ?? effect.InlineSeed?.Defense;
        var display = creatureDef?.Name ?? chosenName ?? "Bound spirit";
        var disposition = effect.Disposition.ToString().ToLowerInvariant();

        int? round = context.ActiveCombat?.IsActive == true ? context.ActiveCombat.Round : null;
        int? day = null;
        if (context.GetCurrentTimeAsync is { } clock)
        {
            day = (await clock()).TotalDaysElapsed;
        }

        var slug = SlugifyMinionOwner(action.CharacterId);
        var next = actor.ControlsMinionIds.Count + 1;
        var createdIds = new List<string>();
        var createdNames = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var id = $"chars/{slug}-minion-{next}";
            while (context.Characters.ContainsKey(id))
            {
                next++;
                id = $"chars/{slug}-minion-{next}";
            }

            createdIds.Add(id);
            createdNames.Add(count == 1 ? display : $"{display} {i + 1}");
            next++;
        }

        var detailLines = new List<string>();
        if (creatureDef?.Abilities is { Count: > 0 } abilities)
        {
            detailLines.Add("Abilities: " + string.Join(" ", abilities));
        }

        if (effect.InlineSeed?.Attacks is { Count: > 0 } attacks)
        {
            detailLines.Add("Attacks: " + string.Join(" ", attacks));
        }

        var details = detailLines.Count > 0 ? " " + string.Join(" ", detailLines) : string.Empty;
        for (var i = 0; i < count; i++)
        {
            var stats = new TStats { StatBlockHp = hp.Value };
            if (defense.HasValue)
            {
                ApplySummonedDefense(stats, defense.Value);
            }

            mutations.Add(new CharacterCreate
            {
                CharacterId = createdIds[i],
                Name = createdNames[i],
                Notes = $"Summoned by {actor.Name} via {action.ActionName} (slot {slot}). " +
                        $"Controlled by {actor.Name} ({disposition}).{details}",
                CurrentLocationId = actor.CurrentLocationId,
                KeepAlive = true,
                MaxHp = hp.Value,
                CurrentHp = hp.Value,
                SystemStats = stats,
                ControlledById = actor.Id,
                MinionBinding = new MinionBinding
                {
                    ControllerId = actor.Id,
                    SpellName = spell.Name,
                    Disposition = effect.Disposition,
                    ConcentrationBound = spell.Concentration == true,
                    DurationRounds = effect.DurationRounds,
                    ExpiresAtRound = round.HasValue && effect.DurationRounds.HasValue
                        ? round.Value + effect.DurationRounds.Value
                        : null,
                    DurationDays = effect.DurationDays,
                    ExpiresAtDay = day.HasValue && effect.DurationDays.HasValue
                        ? day.Value + effect.DurationDays.Value
                        : null,
                },
            });
        }

        var releasedIds = new List<string>();
        var releasedNames = new List<string>();
        var overCapNote = string.Empty;
        if (effect.ControlCap?.MaxCreatures is { } maxCreatures && maxCreatures >= 1)
        {
            var listed = actor.ControlsMinionIds
                .Select(id => context.Characters.TryGetValue(id, out var m) ? m : null)
                .ToList();
            var unloaded = listed.Count(m => m == null);
            var pool = listed.OfType<Character>()
                .OrderBy(m => m.LastUpdated)
                .Select(m => (Id: m.Id, Name: m.Name))
                .Concat(createdIds.Select((id, idx) => (Id: id, Name: createdNames[idx])))
                .ToList();
            var toRelease = unloaded + pool.Count - maxCreatures;
            foreach (var victim in pool.Take(Math.Max(0, toRelease)))
            {
                releasedIds.Add(victim.Id);
                releasedNames.Add(victim.Name);
                mutations.Add(new CharacterUpdate { CharacterId = victim.Id, ClearMinionLink = true });
            }

            if (toRelease > pool.Count)
            {
                overCapNote = $" {unloaded} older minion(s) were retained sight-unseen (not loaded); " +
                              "control exceeds the cap until they are dismissed explicitly.";
            }
        }
        else if (effect.ControlCap is { MaxHitDice: not null } or { HitDicePerCasterLevel: not null })
        {
            context.RecordMessage(
                $"[HINT] '{action.ActionName}' carries a hit-dice control cap, which the engine does not enforce " +
                "(creature hit dice are not in the handbook catalog yet) — enforce it narratively.");
        }

        var keptCreated = createdIds.Except(releasedIds, StringComparer.Ordinal).ToList();
        var releasedExisting = releasedIds.Except(createdIds, StringComparer.Ordinal).ToList();
        if (keptCreated.Count > 0 || releasedExisting.Count > 0)
        {
            mutations.Add(new CharacterUpdate
            {
                CharacterId = actor.Id,
                ControlsMinionIdsAdd = keptCreated.Count > 0 ? keptCreated : null,
                ControlsMinionIdsRemove = releasedExisting.Count > 0 ? releasedExisting : null,
            });
        }

        var duration = SummonDurationClause(effect, day, round);
        var concentration = spell.Concentration == true ? " Control rides on concentration." : string.Empty;
        var released = releasedNames.Count > 0
            ? $" Control cap {effect.ControlCap!.MaxCreatures}: released {string.Join(", ", releasedNames)} (oldest first)."
            : string.Empty;

        return ResolverResult.Ok(
            $"{action.ActionName} (slot {slot}): {actor.Name} binds {count} {display} — " +
            $"controlled by {actor.Name} ({disposition}). {duration}{concentration}{released}{overCapNote}");
    }

    /// <summary>
    /// Recast-to-retain (animate dead): instead of raising new minions, the cast
    /// names already-raised creatures of the same spell via <c>targetIds</c> and
    /// re-binds up to the slot's retain cap; other listed minions of the spell
    /// lapse (their control was not maintained).
    /// </summary>
    protected virtual async Task<ResolverResult> ResolveReassertAsync(
        RulesetAction action,
        IChangeContext context,
        Character actor,
        SpellDefinition spell,
        SummonEffect effect,
        int slot,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        if (effect.RetainCountAtSlotLevel is not { Count: > 0 } retain)
        {
            return ResolverResult.Fail("InvalidParameter",
                $"Error: '{action.ActionName}' does not support recast-to-retain — every cast raises new minions. " +
                "Omit 'reassert' to animate.");
        }

        var keep = retain.TryGetValue(slot, out var k) ? k : retain[retain.Keys.Min()];
        if (action.TargetIds.Count == 0)
        {
            return ResolverResult.Fail("InvalidParameter",
                $"Error: '{action.ActionName}' recast names the raised creatures to retain via targetIds " +
                $"(up to {keep} at slot {slot}).");
        }

        var wanted = action.TargetIds.Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count > keep)
        {
            return ResolverResult.Fail("InvalidParameter",
                $"Error: '{action.ActionName}' at slot {slot} retains at most {keep} — got {wanted.Count}.");
        }

        var targets = new List<Character>();
        foreach (var id in wanted)
        {
            if (!context.Characters.TryGetValue(id, out var minion) || minion == null)
            {
                return ResolverResult.Fail("InvalidTarget",
                    $"Error: '{id}' is not loaded or visible — recast-to-retain names loaded minions only.");
            }

            if (!string.Equals(minion.MinionBinding?.SpellName, spell.Name, StringComparison.OrdinalIgnoreCase))
            {
                return ResolverResult.Fail("InvalidTarget",
                    $"Error: '{minion.Name}' was not raised by '{spell.Name}' — cast without 'reassert' to animate new dead.");
            }

            targets.Add(minion);
        }

        int? round = context.ActiveCombat?.IsActive == true ? context.ActiveCombat.Round : null;
        int? day = null;
        if (context.GetCurrentTimeAsync is { } clock)
        {
            day = (await clock()).TotalDaysElapsed;
        }

        var disposition = effect.Disposition.ToString().ToLowerInvariant();
        foreach (var target in targets)
        {
            mutations.Add(new CharacterUpdate
            {
                CharacterId = target.Id,
                ControlledById = actor.Id,
                MinionBinding = new MinionBinding
                {
                    ControllerId = actor.Id,
                    SpellName = spell.Name,
                    Disposition = effect.Disposition,
                    ConcentrationBound = spell.Concentration == true,
                    DurationRounds = effect.DurationRounds,
                    ExpiresAtRound = round.HasValue && effect.DurationRounds.HasValue
                        ? round.Value + effect.DurationRounds.Value
                        : null,
                    DurationDays = effect.DurationDays,
                    ExpiresAtDay = day.HasValue && effect.DurationDays.HasValue
                        ? day.Value + effect.DurationDays.Value
                        : null,
                },
            });
        }

        var wantedSet = new HashSet<string>(wanted, StringComparer.Ordinal);
        var releasedIds = new List<string>();
        var releasedNames = new List<string>();
        var unloaded = 0;
        foreach (var id in actor.ControlsMinionIds.ToList())
        {
            if (wantedSet.Contains(id))
            {
                continue;
            }

            if (!context.Characters.TryGetValue(id, out var other) || other == null)
            {
                unloaded++;
                continue;
            }

            if (!string.Equals(other.MinionBinding?.SpellName, spell.Name, StringComparison.OrdinalIgnoreCase)
                || other.MinionBinding is not { ControlLapsed: false })
            {
                continue;
            }

            releasedIds.Add(id);
            releasedNames.Add(other.Name);
            mutations.Add(new CharacterUpdate { CharacterId = id, ClearMinionLink = true });
        }

        mutations.Add(new CharacterUpdate
        {
            CharacterId = actor.Id,
            ControlsMinionIdsAdd = wanted,
            ControlsMinionIdsRemove = releasedIds.Count > 0 ? releasedIds : null,
        });

        var duration = SummonDurationClause(effect, day, round);
        var concentration = spell.Concentration == true ? " Control rides on concentration." : string.Empty;
        var released = releasedNames.Count > 0
            ? $" Not maintained: {string.Join(", ", releasedNames)} lapse."
            : string.Empty;
        var unseen = unloaded > 0
            ? $" {unloaded} listed minion(s) not loaded; left untouched."
            : string.Empty;

        return ResolverResult.Ok(
            $"{action.ActionName} (slot {slot}): {actor.Name} reasserts control over " +
            $"{string.Join(", ", targets.Select(t => t.Name))} — " +
            $"controlled by {actor.Name} ({disposition}). {duration}{concentration}{released}{unseen}");
    }

    private static string SummonDurationClause(SummonEffect effect, int? day, int? round) =>
        effect.DurationDays.HasValue
            ? $"Control lasts {effect.DurationDays} day(s)" +
              (day.HasValue ? "; recast to retain." : " (unanchored: no campaign clock).")
            : effect.DurationRounds.HasValue
                ? $"Control lasts {effect.DurationRounds} round(s)" +
                  (round.HasValue ? "." : " (unanchored: no active combat).")
                : "Bound until dispelled or destroyed.";

    private static int SummonBaseSlot(SpellDefinition spell)
    {
        if (spell.Level.HasValue)
        {
            return spell.Level.Value;
        }

        var keys = new List<int>();
        if (spell.Summon?.CountAtSlotLevel is { } counts)
        {
            keys.AddRange(counts.Keys);
        }

        if (spell.Summon?.CountChoicesAtSlotLevel is { } choices)
        {
            keys.AddRange(choices.Keys);
        }

        if (spell.Summon?.RetainCountAtSlotLevel is { } retain)
        {
            keys.AddRange(retain.Keys);
        }

        return keys.Count > 0 ? keys.Min() : 1;
    }

    private static List<int> SummonCandidates(SummonEffect effect, int slot)
    {
        if (effect.CountChoicesAtSlotLevel is { Count: > 0 } choices)
        {
            return choices.TryGetValue(slot, out var picked) ? [.. picked] : [.. choices[choices.Keys.Min()]];
        }

        if (effect.CountAtSlotLevel is { Count: > 0 } counts)
        {
            return [counts.TryGetValue(slot, out var n) ? n : counts[counts.Keys.Min()]];
        }

        return [1];
    }

    private static string SlugifyMinionOwner(string characterId)
    {
        var tail = characterId.Split('/').LastOrDefault() ?? string.Empty;
        var slug = new string([.. tail.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_')]).Trim('_');
        return string.IsNullOrEmpty(slug) ? "caster" : slug;
    }

    protected virtual Task<ResolverResult> ResolveSpellSaveAsync(
        RulesetAction action,
        IChangeContext context,
        TStats actorStats,
        List<WorldChange> mutations,
        CancellationToken ct) =>
        Task.FromResult(ResolverResult.Fail(
            "NotImplemented",
            $"{System}: Spell save resolution requires a ruleset-specific implementation."));

    protected virtual async Task<ResolverResult> ResolveSpellUtilityAsync(
        RulesetAction action,
        IChangeContext context,
        TStats actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        if (action.Parameters.ContainsKey("dc"))
        {
            return await ResolveSkillCheckAsync(action, context, actorStats, mutations, ct);
        }

        return ResolverResult.Ok(
            $"{action.ActionName}: Non-combat utility spell — no DC supplied. Narrate the outcome; commit status/effects separately if needed.");
    }

    protected virtual async Task<ResolverResult> ResolveSpellHealAsync(
        RulesetAction action,
        IChangeContext context,
        TStats actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        var targets = action.TargetIds.Count > 0 ? action.TargetIds : [action.CharacterId];
        var hasHealDice = TryGetParameter(action.Parameters, out var healDice, "healDice", "damageDice");
        var hasHealAmountKey = TryGetParameter(action.Parameters, out var healAmountRaw, "healAmount");
        var parsedHealAmount = 0;
        if (hasHealAmountKey && !int.TryParse(healAmountRaw, out parsedHealAmount))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid healAmount value '{healAmountRaw}'.");
        }

        if (!hasHealDice && !hasHealAmountKey)
        {
            return ResolverResult.Fail("InvalidParameter",
                $"Error: {action.ActionType} requires healDice or healAmount.");
        }

        var healBonus = 0;
        if (action.Parameters.TryGetValue("healBonus", out var hb) && !int.TryParse(hb, out healBonus))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid healBonus value '{hb}'.");
        }

        var narratives = new List<string>();
        foreach (var targetId in targets)
        {
            if (!context.Characters.TryGetValue(targetId, out var target))
            {
                return ResolverResult.Fail("InvalidTarget", $"Error: Target '{targetId}' not found for healing spell.");
            }

            var healRoll = hasHealAmountKey
                ? parsedHealAmount + healBonus
                : await RollHealAmountAsync(healDice, healBonus, ct);
            mutations.Add(new HpChange { CharacterId = targetId, Delta = healRoll });
            narratives.Add($"{action.ActionName} heals {target.Name} for {healRoll} HP.");
        }

        return ResolverResult.Ok(string.Join(" | ", narratives));
    }

    protected virtual async Task<ResolverResult> ResolveRecoveryAsync(
        RulesetAction action,
        IChangeContext context,
        TStats actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        action.ActionCategory = action.ActionCategory == default ? ActionCategory.Survival : action.ActionCategory;
        return await ResolveSpellHealAsync(action, context, actorStats, mutations, ct);
    }

    protected virtual async Task<ResolverResult> ResolveUseItemAsync(
        RulesetAction action,
        IChangeContext context,
        TStats actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        action.ActionCategory = action.ActionCategory == default ? ActionCategory.Survival : action.ActionCategory;
        var hasHealDice = TryGetParameter(action.Parameters, out _, "healDice", "damageDice");
        var hasHealAmount = TryGetParameter(action.Parameters, out _, "healAmount");
        if (!hasHealDice && !hasHealAmount)
        {
            return ResolverResult.Ok($"{action.ActionName} used; no HP change (no healDice/healAmount).");
        }

        return await ResolveSpellHealAsync(action, context, actorStats, mutations, ct);
    }

    protected virtual async Task<int> RollHealAmountAsync(string healDice, int healBonus, CancellationToken ct)
    {
        var rollService = GetRollService();
        if (rollService is null)
        {
            return Math.Max(1, healBonus);
        }

        var outcome = await rollService.RollAsync(new RollRequest
        {
            Tag = "heal",
            Expression = healDice,
            Bonus = healBonus,
            Mechanic = DiceMechanic.Standard,
        }, ct);
        return Math.Max(0, outcome.Result);
    }

    protected virtual IRollService? GetRollService() => null;

    /// <summary>
    /// Session-based initiative roll for direct tool use. 
    /// For combat flows, the preferred path is the Character overload (pre-loaded context).
    /// </summary>
    public async Task<float> RollInitiativeAsync(
        Raven.Client.Documents.Session.IAsyncDocumentSession session, 
        string characterId, 
        CancellationToken ct = default)
    {
        var character = await session.LoadAsync<Character>(characterId, ct);
        if (character == null)
        {
            return 0f;
        }

        return await RollInitiativeAsync(character, ct);
    }

    public abstract Task<float> RollInitiativeAsync(
        Character character, 
        CancellationToken ct = default);

    protected DiceMechanic GetMechanicFromAction(RulesetAction action)
    {
        // 1. Check explicit AdvantageState first (the modern way)
        if (action.AdvantageState == AdvantageState.Advantage) return DiceMechanic.Advantage;
        if (action.AdvantageState == AdvantageState.Disadvantage) return DiceMechanic.Disadvantage;

        // 2. Fall back to legacy Parameters for backward compatibility
        return GetMechanicFromParams(action.Parameters);
    }

    protected static bool TryGetParameter(
        Dictionary<string, string> parameters,
        out string value,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (parameters.TryGetValue(key, out value!))
            {
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    protected DiceMechanic GetMechanicFromParams(Dictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("advantage", out var adv) && bool.TryParse(adv, out var isAdv) && isAdv)
        {
            return DiceMechanic.Advantage;
        }

        if (parameters.TryGetValue("disadvantage", out var dis) && bool.TryParse(dis, out var isDis) && isDis)
        {
            return DiceMechanic.Disadvantage;
        }

        return DiceMechanic.Standard;
    }

    /// <summary>Every roll goes through this; plugins add providers to it. Resolvers built without the container get core's own.</summary>
    protected RollModifierPipeline Pipeline { get; set; } = RollModifierPipeline.BuiltIn;

    /// <summary>
    /// Folds the status-effect modifiers matching the legacy tags into a base value. Kept for callers without a context; the
    /// pipeline (<see cref="FoldAsync"/>) is the way for anything that also wants providers or advantage.
    /// </summary>
    protected int ApplyAllModifiers(TStats stats, int baseValue, params string[] modifierTags)
    {
        var first = modifierTags.Length > 0 ? modifierTags[0] : "";
        var subject = modifierTags.Length > 1 ? modifierTags[1] : null;
        var kind = first switch
        {
            "AC" or "Defense" => RollKinds.ArmorClass,
            "AttackRoll" => RollKinds.Attack,
            "DamageRoll" => RollKinds.Damage,
            "SkillCheck" => RollKinds.Check,
            "SavingThrow" => RollKinds.Save,
            "Initiative" => RollKinds.Initiative,
            "Speed" => RollKinds.Speed,
            _ => RollKinds.Attack,
        };
        return baseValue + StatusEffectModifierProvider.Sum(stats.StatusEffects, kind, subject);
    }

    /// <summary>
    /// Runs a roll through the modifier pipeline: status effects, willpower and every plugin provider. Returns the final bonus,
    /// the net advantage (the caller's explicit mechanic counts as one source) and the reasons to show the player.
    /// <paramref name="tags"/> say what the roll is against (charm, fear...); the action's <c>saveTags</c> parameter adds to them.
    /// </summary>
    protected async Task<RollFold> FoldAsync(
        IChangeContext context,
        Character actor,
        string kind,
        string? subject,
        int baseBonus,
        RulesetAction? action = null,
        DiceMechanic explicitMechanic = DiceMechanic.Standard,
        Character? other = null,
        IEnumerable<string>? tags = null)
    {
        IReadOnlyDictionary<string, string> options = new Dictionary<string, string>();
        if (context.GetSystemOptionsAsync is { } load)
            options = await load().ConfigureAwait(false);

        var allTags = new List<string>(tags ?? []);
        if (action is not null && action.Parameters.TryGetValue("saveTags", out var raw))
            allTags.AddRange(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        // A Wisdom, Intelligence or Charisma save is a mental one unless it says otherwise.
        if (kind == RollKinds.Save && StatusEffectModifierProvider.Normalize(subject) is "wisdom" or "wis" or "intelligence" or "int" or "charisma" or "cha")
            allTags.Add("mental");

        var query = new RollQuery(kind, subject, allTags, actor, other, System, options);
        var explicitAdvantage = explicitMechanic switch
        {
            DiceMechanic.Advantage => AdvantageEffect.Advantage,
            DiceMechanic.Disadvantage => AdvantageEffect.Disadvantage,
            _ => AdvantageEffect.None,
        };
        var feat = action is null
            ? FeatEffectFold.None
            : FeatEffectRules.Fold(kind, subject, action, action.FeatEffects.GetValueOrDefault(actor.Id) ?? [], System, isActor: actor.Id == action.CharacterId);
        var resolved = Pipeline.Resolve(query, baseBonus, explicitAdvantage, feat.Advantage, feat.Disadvantage);
        if (action is not null)
            resolved = resolved with { Bonus = resolved.Bonus + feat.Bonus, Notes = [.. resolved.Notes, .. feat.Notes] };
        var mechanic = resolved.Advantage switch
        {
            AdvantageEffect.Advantage => DiceMechanic.Advantage,
            AdvantageEffect.Disadvantage => DiceMechanic.Disadvantage,
            _ => explicitMechanic is DiceMechanic.Advantage or DiceMechanic.Disadvantage ? DiceMechanic.Standard : explicitMechanic,
        };
        return new RollFold(resolved.Bonus, mechanic, resolved.Notes);
    }

    public virtual IReadOnlyDictionary<string, int> GetTurnActionBudget(Character character)
    {
        // "reaction" is deliberately not a budget key: reaction gating is handled entirely via
        // CombatantState.ReactionAvailable (see TryConsumeActionSlot's IsReaction early-return below).
        return new Dictionary<string, int>
        {
            { "action", 1 },
            { "bonus", 1 }
        };
    }

    public virtual bool TryConsumeActionSlot(CombatantState state, RulesetAction action, out string? errorReason)
    {
        errorReason = null;

        if (action.IsReaction)
        {
            return true;
        }

        if (state.ActionBudget.Count == 0)
        {
            return true;
        }

        var slot = action.Parameters.TryGetValue("bonusAction", out var bonusStr) && bool.TryParse(bonusStr, out var isBonus) && isBonus
            ? "bonus"
            : "action";

        if (!state.ActionBudget.TryGetValue(slot, out var remaining) || remaining <= 0)
        {
            errorReason = $"No {slot} remaining this turn.";
            return false;
        }

        state.ActionBudget[slot]--;
        return true;
    }

    public virtual bool EnforcesRange => true;
}

/// <summary>A roll's final bonus and dice mechanic after the modifier pipeline, with the reasons to show.</summary>
public sealed record RollFold(int Bonus, DiceMechanic Mechanic, IReadOnlyList<string> Notes)
{
    /// <summary>" [Willpower 20: −2 vs fear; …]" or empty.</summary>
    public string Suffix => Notes.Count == 0 ? "" : $" [{string.Join("; ", Notes)}]";
}
