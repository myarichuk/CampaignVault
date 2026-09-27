using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Rulesets.Contributors;
using CampaignVault.Services;
using RegularExpressions = System.Text.RegularExpressions;

namespace CampaignVault.Rulesets;

public class Dnd5eRulesetResolver : RulesetResolverBase<Dnd5eExtension>
{
    private readonly IRollService _rollService;
    private readonly ICharacterBootstrapPipeline _bootstrap;
    private readonly SpellDefinitionProvider? _spellDefinitionProvider;

    public Dnd5eRulesetResolver(
        IRollService rollService,
        RaceDefinitionProvider? raceProvider = null,
        ClassDefinitionProvider? classProvider = null,
        BackgroundDefinitionProvider? backgroundProvider = null,
        SpellDefinitionProvider? spellDefinitionProvider = null)
    {
        _rollService = rollService ?? throw new ArgumentNullException(nameof(rollService));
        _spellDefinitionProvider = spellDefinitionProvider;
        var hpStep = new Dnd5eDeriveHitPointsStep(_rollService);
        var profStep = new Dnd5eDeriveProficiencyStep(classProvider, backgroundProvider);
        var passiveStep = new Dnd5eDerivePassivePerceptionStep();
        var spellStep = new Dnd5eDeriveSpellcastingStep();
        List<IBootstrapStep> steps = raceProvider != null ? [new Dnd5eDeriveRaceStep(raceProvider)] : [];
        steps.AddRange([hpStep, new Dnd5eDeriveDefenseStep(), profStep, passiveStep, spellStep]);
        _bootstrap = new CharacterBootstrapPipeline(
            steps,
            [hpStep, profStep, passiveStep, spellStep]);
    }

    public override string System => RulesetSystem.Dnd5e;

    public override ICharacterBootstrapPipeline Bootstrap => _bootstrap;

    public override IEnumerable<IRulesetPressureContributor> PressureContributors =>
        [new Dnd5eExhaustionPressureContributor()];

    protected override IRollService? GetRollService() => _rollService;

    private int GetSkillOrAbilityBonus(Dnd5eExtension stats, string name)
    {
        var matchedKey = stats.SkillModifiers.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (matchedKey != null && stats.SkillModifiers.TryGetValue(matchedKey, out var skillMod))
        {
            return skillMod;
        }

        return name.ToLower() switch
        {
            "strength" => stats.GetAbilityModifier(stats.Strength),
            "dexterity" => stats.GetAbilityModifier(stats.Dexterity),
            "constitution" => stats.GetAbilityModifier(stats.Constitution),
            "intelligence" => stats.GetAbilityModifier(stats.Intelligence),
            "wisdom" => stats.GetAbilityModifier(stats.Wisdom),
            "charisma" => stats.GetAbilityModifier(stats.Charisma),
            _ => 0
        };
    }

    private int GetSavingThrowBonus(Dnd5eExtension stats, string name)
    {
        var matchedKey = stats.SavingThrowModifiers.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (matchedKey != null && stats.SavingThrowModifiers.TryGetValue(matchedKey, out var saveMod))
        {
            return saveMod;
        }

        return name.ToLower() switch
        {
            "strength" => stats.GetAbilityModifier(stats.Strength),
            "dexterity" => stats.GetAbilityModifier(stats.Dexterity),
            "constitution" => stats.GetAbilityModifier(stats.Constitution),
            "intelligence" => stats.GetAbilityModifier(stats.Intelligence),
            "wisdom" => stats.GetAbilityModifier(stats.Wisdom),
            "charisma" => stats.GetAbilityModifier(stats.Charisma),
            _ => 0
        };
    }

    protected override async Task<ResolverResult> ResolveAttackAsync(
        RulesetAction action, 
        IChangeContext context, 
        Dnd5eExtension actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct)
    {
        // Multi-instance spells (Magic Missile darts, Scorching Ray rays) fan out over instances,
        // not listed targets. Every other action keeps the one-attack-per-listed-target loop below.
        var spell = action.ActionType == RulesetActionType.Spell ? LookupSpell(action) : null;
        if (spell?.DamageIsPool == true)
        {
            return ResolverResult.Fail("UnresolvableMechanic", BuildPoolDamageError(action));
        }
        if (spell != null && HasInstanceData(spell))
        {
            return await ResolveMultiInstanceAttackAsync(action, spell, context, actorStats, mutations, ct);
        }

        var targets = AttackTargetHelper.SelectTargets(action);
        if (targets.Count == 0)
        {
            return ResolverResult.Fail("InvalidTarget", "Error: No valid target specified for attack.");
        }

        var narratives = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            var result = await ResolveAttackAgainstTargetAsync(
                action, targets[i], context, actorStats, mutations, ct, spell);
            if (!result.Success)
            {
                return result;
            }

            narratives.Add(result.Narrative);
        }

        return ResolverResult.Ok(string.Join(" | ", narratives));
    }

    private async Task<ResolverResult> ResolveAttackAgainstTargetAsync(
        RulesetAction action,
        string targetId,
        IChangeContext context,
        Dnd5eExtension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct,
        SpellDefinition? spell = null,
        string? damageDiceOverride = null)
    {
        if (!context.Characters.TryGetValue(targetId, out var target))
        {
            return ResolverResult.Fail("InvalidTarget", $"Error: Target '{targetId}' not found for attack.");
        }

        if (target.SystemStats is not Dnd5eExtension targetStats)
        {
            return ResolverResult.Fail("IncompatibleRuleset", "Error: Target uses incompatible ruleset stats for current ActiveSystem.");
        }

        var ac = targetStats.ArmorClass;
        ac = ApplyAllModifiers(targetStats, ac, "AC");
        
        if (action.Parameters.TryGetValue("ac", out var acStr) && int.TryParse(acStr, out var overrideAc))
        {
            ac = overrideAc;
        }

        var attackBonus = 0;
        var hasExplicitBonus = TryGetParameter(action.Parameters, out var b, "bonus", "toHitBonus", "spellAttackBonus");
        if (hasExplicitBonus && !int.TryParse(b, out attackBonus))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid bonus value '{b}'.");
        }

        if (!hasExplicitBonus && action.ActionType == RulesetActionType.Spell)
        {
            attackBonus = ResolveSpellAttackBonus(actorStats);
        }

        attackBonus = ApplyAllModifiers(actorStats, attackBonus, "AttackRoll");

        var damageDice = damageDiceOverride ?? action.Parameters.GetValueOrDefault("damageDice", "1d4");
        
        var damageBonus = 0;
        if (action.Parameters.TryGetValue("damageBonus", out var db) && !int.TryParse(db, out damageBonus))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid damageBonus value '{db}'.");
        }

        damageBonus = ApplyAllModifiers(actorStats, damageBonus, "DamageRoll");

        var mechanic = GetMechanicFromAction(action);
        var requiresAttackRoll = spell?.RequiresAttackRoll ?? true;

        var attackRoll = requiresAttackRoll
            ? await _rollService.RollAsync(new RollRequest { Tag = "attack", Expression = "1d20", Bonus = attackBonus, Mechanic = mechanic }, ct)
            : null;
        var damageRoll = await _rollService.RollAsync(new RollRequest { Tag = "damage", Expression = damageDice, Bonus = damageBonus, Mechanic = DiceMechanic.Standard }, ct);

        var isHit = false;
        var isCrit = attackRoll is { HasCritical: true };

        if (!requiresAttackRoll)
        {
            // Auto-hit spell (Magic Missile): no attack roll and no crit — damage always applies.
            isHit = true;
        }
        else if (isCrit)
        {
            isHit = true;
        }
        else if (attackRoll is { HasComplication: true })
        {
            isHit = false;
        }
        else if (attackRoll is { Result: var attackTotal } && attackTotal >= ac)
        {
            isHit = true;
        }

        if (!isHit)
        {
            // Auto-hit spells always hit, so an attack roll exists on every path that reaches here.
            var attackDetail = $"Attack {attackRoll!.Result} vs AC {ac}. {attackRoll.Summary}";
            if (spell?.OnMiss == MissBehavior.Half)
            {
                // Acid Arrow shape: a miss still splashes half the (already rolled) initial damage.
                var splashDamage = ApplyTargetDamageReduction(
                    (int)Math.Floor(damageRoll.Result / 2.0), targetStats, action.DamageType);
                if (splashDamage > 0)
                {
                    mutations.Add(new HpChange { CharacterId = targetId, Delta = -splashDamage });
                }
                return ResolverResult.Ok($"{action.ActionName} vs {target.Name}: Missed, but the acid still splashes for {splashDamage} damage. {attackDetail}");
            }
            return ResolverResult.Ok($"{action.ActionName} vs {target.Name}: Missed. {attackDetail}");
        }

        var finalDamage = damageRoll.Result;
        var critMsg = "";
        if (isCrit)
        {
            var critDmg = await _rollService.RollAsync(new RollRequest { Tag = "critDamage", Expression = damageDice, Mechanic = DiceMechanic.Standard }, ct);
            finalDamage += critDmg.Result;
            critMsg = $" CRITICAL HIT! Added {critDmg.Result} extra damage.";
        }

        finalDamage = ApplyTargetDamageReduction(finalDamage, targetStats, action.DamageType);

        mutations.Add(new HpChange
        {
            CharacterId = targetId,
            Delta = -finalDamage
        });

        var tickMsg = spell?.DelayedTick != null
            ? EmitDelayedTick(action, spell, targetId, target.Name, mutations)
            : "";

        var damageWarning = BuildSpellDamageWarning(action, damageDice, actorStats.Level);

        var attackSegment = attackRoll != null ? $"(Attack {attackRoll.Result} vs AC {ac})" : "(auto-hit)";
        return ResolverResult.Ok($"{action.ActionName} vs {target.Name}: Hit for {finalDamage} damage. {attackSegment}.{critMsg}{tickMsg}{damageWarning}");
    }

    private SpellDefinition? LookupSpell(RulesetAction action)
    {
        if (_spellDefinitionProvider == null)
        {
            return null;
        }

        var slug = NormalizeSpellSlug(action.ActionName);
        return _spellDefinitionProvider.TryGet(System, slug, out var spell) ? spell : null;
    }

    private static bool HasInstanceData(SpellDefinition spell) =>
        spell.InstanceCountAtSlotLevel is { Count: > 0 }
        || spell.InstanceCountAtCharacterLevel is { Count: > 0 };

    private static bool HasPoolData(SpellDefinition spell) =>
        spell.DamagePools is { Count: > 0 } pools && pools.Values.All(p => p is { Count: > 0 });

    /// <summary>One multi-pool cast's derived shape: per-pool dice plus any upcast bonus.</summary>
    private sealed record PoolCastShape(
        List<(string Type, string Dice)> Pools,
        string? BonusDice,
        int BonusLevels,
        string? BonusPool);

    /// <summary>
    /// Matches the caller's summed damage total against the spell's per-slot totals to infer which
    /// slot level was cast (the spend itself is a separate ResourceChange the resolver can't see).
    /// Null when nothing matches — callers fall back to the base slot.
    /// </summary>
    private static int? MatchSummedTier(SpellDefinition spell, string? sentDamageDice)
    {
        if (sentDamageDice is null || spell.DamageAtSlotLevel is not { Count: > 0 } summed)
        {
            return null;
        }

        var normalized = NormalizeDice(sentDamageDice);
        foreach (var candidate in summed.OrderBy(kv => kv.Key))
        {
            if (string.Equals(NormalizeDice(candidate.Value), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Key;
            }
        }

        return null;
    }

    /// <summary>
    /// Emits the synthetic residue effect scheduling a delayed damage tick (Acid Arrow's "2d4 at
    /// the end of its next turn"). Only called on a hit — RequiresInitialHit spells never schedule
    /// on a miss. Returns the narrative clause describing the scheduled tick ("" if none).
    /// </summary>
    private static string EmitDelayedTick(
        RulesetAction action, SpellDefinition spell, string targetId, string targetName, List<WorldChange> mutations)
    {
        var tick = spell.DelayedTick;
        if (tick?.DiceExpressionAtSlotLevel is not { Count: > 0 } diceBySlot)
        {
            return "";
        }

        action.Parameters.TryGetValue("damageDice", out var sent);
        var slot = MatchSummedTier(spell, sent) ?? diceBySlot.Keys.Min();
        var dice = diceBySlot.TryGetValue(slot, out var resolved) ? resolved : diceBySlot[diceBySlot.Keys.Min()];
        mutations.Add(new StatusChange
        {
            CharacterId = targetId,
            Effect = new StatusEffect
            {
                Name = "AcidArrowResidue",
                Category = "Condition",
                PendingDamage = new PendingEffectDamage
                {
                    DiceExpression = dice,
                    DamageType = tick.DamageType ?? spell.DamageType ?? "acid",
                },
                ExpiresAtOwnTurnStart = true,
                AppliedBy = "system/combat-resolver",
                RecoveryHint = "Fades after dealing its delayed damage at the start of the target's next turn.",
            },
        });
        return $" Acid clings to {targetName} ({dice} at the start of their next turn).";
    }

    /// <summary>
    /// Applies the target's damage-type multiplier and flat damage reduction, shared by the hit
    /// and miss-splash paths so both agree on mitigation.
    /// </summary>
    private static int ApplyTargetDamageReduction(int damage, Dnd5eExtension targetStats, string? actionDamageType)
    {
        var damageType = actionDamageType ?? "Physical";

        var drKey = targetStats.DamageResistances.Keys.FirstOrDefault(k => string.Equals(k, damageType, StringComparison.OrdinalIgnoreCase));
        var flatDr = drKey != null && targetStats.DamageResistances.TryGetValue(drKey, out var dr) ? dr : 0;

        if (targetStats.DamageModifiers.TryGetValue(damageType, out var multiplier))
        {
            damage = (int)Math.Floor(damage * multiplier);
        }

        return Math.Max(0, damage - flatDr);
    }

    /// <summary>
    /// Resolves one attack roll + damage roll per damage *instance* (Magic Missile dart, Scorching
    /// Ray ray, Eldritch Blast beam) instead of one per listed target. Instance count and
    /// per-instance dice come from the spell's overlay data, not the caller's damageDice — this is
    /// the one place a multi-instance spell's damage is derived, not caller-sent, because count +
    /// per-instance dice are two numbers damageDice's one string can't express. Escape hatches:
    /// attackCount/shots/etc. overrides the derived count, instanceDamageDice overrides the
    /// per-instance dice.
    /// </summary>
    private async Task<ResolverResult> ResolveMultiInstanceAttackAsync(
        RulesetAction action,
        SpellDefinition spell,
        IChangeContext context,
        Dnd5eExtension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        var (instanceCount, perInstanceDice) = ResolveInstanceShape(action, spell, actorStats.Level);
        if (TryGetParameter(action.Parameters, out var countRaw, "attackCount", "shots", "rateOfFire", "attacks")
            && int.TryParse(countRaw, out var explicitCount) && explicitCount > 0)
        {
            instanceCount = explicitCount;
        }
        if (TryGetParameter(action.Parameters, out var diceOverride, "instanceDamageDice")
            && !string.IsNullOrWhiteSpace(diceOverride))
        {
            perInstanceDice = diceOverride;
        }

        var instances = AttackTargetHelper.DistributeInstances(action.TargetIds, instanceCount);
        if (instances.Count == 0)
        {
            return ResolverResult.Fail("InvalidTarget", "Error: No valid target specified for attack.");
        }

        var narratives = new List<string>();
        foreach (var targetId in instances)
        {
            var result = await ResolveAttackAgainstTargetAsync(
                action, targetId, context, actorStats, mutations, ct, spell, perInstanceDice);
            if (!result.Success)
            {
                return result;
            }

            narratives.Add(result.Narrative);
        }

        return ResolverResult.Ok(string.Join(" | ", narratives));
    }

    private static (int Count, string Dice) ResolveInstanceShape(
        RulesetAction action, SpellDefinition spell, int? casterLevel)
    {
        if (spell.InstanceCountAtCharacterLevel is { Count: > 0 } byChar
            && spell.PerInstanceDamageAtCharacterLevel is { Count: > 0 } charDice)
        {
            var tier = byChar.Keys.Where(l => l <= (casterLevel ?? 1)).DefaultIfEmpty(byChar.Keys.Min()).Max();
            var dice = charDice.TryGetValue(tier, out var tierDice) ? tierDice : charDice[charDice.Keys.Min()];
            return (byChar[tier], dice);
        }

        if (spell.InstanceCountAtSlotLevel is { Count: > 0 } bySlot
            && spell.PerInstanceDamageAtSlotLevel is { Count: > 0 } slotDice)
        {
            // The resolver has no slot-level signal (the slot spend is a separate ResourceChange),
            // so infer the tier from the caller's summed total — the same value the old single-roll
            // shape sent ("5d4+5" matches slot 3's total, resolving 5 darts). Falls back to the
            // base slot when nothing matches (e.g. the caller already sent per-instance dice).
            action.Parameters.TryGetValue("damageDice", out var sent);
            var slot = MatchSummedTier(spell, sent) ?? bySlot.Keys.Min();

            var count = bySlot.TryGetValue(slot, out var resolvedCount) ? resolvedCount : bySlot[bySlot.Keys.Min()];
            var dice = slotDice.TryGetValue(slot, out var resolvedDice) ? resolvedDice : slotDice[slotDice.Keys.Min()];
            return (count, dice);
        }

        return (1, action.Parameters.GetValueOrDefault("damageDice", "1d4"));
    }

    /// <summary>
    /// Ability abbreviation → full name, as dnd5eapi.co's spell "dc.dc_type.index" carries it (e.g.
    /// "dex"), for comparing against the caller's ruleset_action.parameters.save (e.g. "Dexterity").
    /// </summary>
    /// <summary>
    /// Canonical composite dice: splits on '+', combines like-sided terms and flat modifiers,
    /// sorts by die sides, rejoins. "20d6+20d6" and "40d6" both become "40d6"; "2d8+4d6" and
    /// "4d6+2d8" both become "4d6+2d8" — so pool order and pre-combined totals never false-warn.
    /// Anything unparseable falls back to whitespace-stripped comparison.
    /// </summary>
    private static string CanonicalizeCompositeDice(string dice)
    {
        var countsBySides = new SortedDictionary<int, int>();
        var flat = 0;
        foreach (var rawTerm in dice.Split('+'))
        {
            var term = rawTerm.Trim();
            var match = RegularExpressions.Regex.Match(term, @"^(\d+)[dD](\d+)([+-]\d+)?$");
            if (match.Success)
            {
                var sides = int.Parse(match.Groups[2].Value);
                countsBySides.TryGetValue(sides, out var count);
                countsBySides[sides] = count + int.Parse(match.Groups[1].Value);
                if (match.Groups[3].Success)
                {
                    flat += int.Parse(match.Groups[3].Value);
                }
                continue;
            }
            if (int.TryParse(term, out var flatTerm))
            {
                flat += flatTerm;
                continue;
            }
            return NormalizeDice(dice);
        }

        var result = string.Join("+", countsBySides.Select(kv => $"{kv.Value}d{kv.Key}"));
        if (flat > 0)
        {
            result += (result.Length > 0 ? "+" : "") + flat;
        }
        else if (flat < 0)
        {
            result += flat;
        }
        return result.Length > 0 ? result : NormalizeDice(dice);
    }

    /// <summary>Scales NdX dice by a level count ("1d6" x 2 = "2d6"); unparseable input passes through.</summary>
    private static string ScaleDice(string dice, int levels)
    {
        var match = RegularExpressions.Regex.Match(dice.Trim(), @"^(\d+)[dD](\d+)$");
        if (!match.Success || levels < 1)
        {
            return dice;
        }
        return $"{int.Parse(match.Groups[1].Value) * levels}d{match.Groups[2].Value}";
    }

    /// <summary>
    /// Per-slot canonical damage totals for a multi-pool spell: every pool's dice plus any upcast
    /// bonus levels, canonicalized. The caster's upcast choice never changes the total (addition
    /// commutes), so each slot maps to exactly one accepted string.
    /// </summary>
    private static Dictionary<int, string> PoolTotalsBySlot(SpellDefinition spell)
    {
        var pools = spell.DamagePools!;
        var slots = pools.First().Value.Keys;
        var baseSlot = slots.Min();
        var totals = new Dictionary<int, string>();
        foreach (var slot in slots)
        {
            var terms = pools
                .Select(p => p.Value.TryGetValue(slot, out var poolDice) ? poolDice : p.Value[p.Value.Keys.Min()])
                .ToList();
            if (spell.UpcastChoice?.BonusDicePerSlot is string bonus && slot > baseSlot)
            {
                for (var i = 0; i < slot - baseSlot; i++)
                {
                    terms.Add(bonus);
                }
            }
            totals[slot] = CanonicalizeCompositeDice(string.Join("+", terms));
        }
        return totals;
    }

    /// <summary>
    /// Matches the caller's summed damage total against the spell's per-slot pool totals to infer
    /// which slot level was cast. Falls back to the base slot when nothing matches.
    /// </summary>
    private static int InferPoolSlot(SpellDefinition spell, string? sentDamageDice)
    {
        var totals = PoolTotalsBySlot(spell);
        if (sentDamageDice != null)
        {
            var canonical = CanonicalizeCompositeDice(sentDamageDice);
            foreach (var (slot, total) in totals.OrderBy(kv => kv.Key))
            {
                if (string.Equals(total, canonical, StringComparison.OrdinalIgnoreCase))
                {
                    return slot;
                }
            }
        }
        return totals.Keys.Min();
    }

    /// <summary>
    /// Derives a multi-pool cast's per-pool dice (and any upcast bonus) from spell data. The slot
    /// comes from the caller's summed total when it matches a known tier, else the base slot —
    /// same inference rule as multi-instance, since the slot spend is invisible here. The upcast
    /// bonus pool comes from the action's upcastPool parameter (null when missing or unknown,
    /// which the narrative notes without blocking).
    /// </summary>
    private static PoolCastShape ResolvePoolShape(RulesetAction action, SpellDefinition spell)
    {
        var pools = spell.DamagePools!;
        action.Parameters.TryGetValue("damageDice", out var sent);
        var slot = InferPoolSlot(spell, sent);
        var baseSlot = pools.First().Value.Keys.Min();
        var resolved = pools
            .Select(p => (p.Key, p.Value.TryGetValue(slot, out var poolDice) ? poolDice : p.Value[p.Value.Keys.Min()]))
            .ToList();
        if (spell.UpcastChoice?.BonusDicePerSlot is not string bonus || slot <= baseSlot)
        {
            return new PoolCastShape(resolved, null, 0, null);
        }
        string? chosen = null;
        if (action.Parameters.TryGetValue("upcastPool", out var choice))
        {
            chosen = resolved.Select(p => p.Key).FirstOrDefault(t => string.Equals(t, choice, StringComparison.OrdinalIgnoreCase));
        }
        return new PoolCastShape(resolved, bonus, slot - baseSlot, chosen);
    }

    /// <summary>
    /// Rolls each damage pool separately (correct variance and per-type narrative), sums with any
    /// upcast bonus, then applies the save result to the total — "half as much damage" halves the
    /// sum, not each pool. Mirrors TryApplySaveDamageAsync's half-on-save math and single HpChange.
    /// </summary>
    private static async Task<(int Damage, string Detail)> ApplyPoolSaveDamageAsync(
        RulesetAction action,
        PoolCastShape shape,
        string targetId,
        bool saved,
        List<WorldChange> mutations,
        IRollService rollService,
        CancellationToken ct)
    {
        var parts = new List<string>();
        var total = 0;
        foreach (var (type, dice) in shape.Pools)
        {
            var poolRoll = await rollService.RollAsync(new RollRequest
            {
                Tag = "spell-damage",
                Expression = dice,
                Mechanic = DiceMechanic.Standard,
            }, ct);
            total += poolRoll.Result;
            parts.Add($"{poolRoll.Result} {type}");
        }
        if (shape.BonusDice != null)
        {
            var bonusTotal = 0;
            for (var i = 0; i < shape.BonusLevels; i++)
            {
                var bonusRoll = await rollService.RollAsync(new RollRequest
                {
                    Tag = "spell-damage",
                    Expression = shape.BonusDice,
                    Mechanic = DiceMechanic.Standard,
                }, ct);
                bonusTotal += bonusRoll.Result;
            }
            total += bonusTotal;
            parts.Add(shape.BonusPool != null ? $"{bonusTotal} upcast {shape.BonusPool}" : $"{bonusTotal} upcast");
        }

        if (action.Parameters.TryGetValue("damageBonus", out var db) && int.TryParse(db, out var damageBonus))
        {
            total += damageBonus;
        }

        var halfOnSave = ResolveHalfOnSave(action.Parameters);
        var damage = saved && halfOnSave ? (int)Math.Floor(total / 2.0) : saved ? 0 : total;
        if (damage > 0)
        {
            mutations.Add(new HpChange { CharacterId = targetId, Delta = -damage });
        }
        return (damage, $" ({string.Join(" + ", parts)})");
    }

    /// <summary>
    /// Multi-pool spells (Ice Storm, Meteor Swarm, Flame Strike) accept the caller's summed total
    /// in any pool order, pre-combined or composite ("40d6", "20d6+20d6", "2d8+4d6" all match) —
    /// canonicalized before comparison. Warns only when no known slot tier matches. On the save
    /// path damage was derived from pool data; anywhere else it was applied as sent.
    /// </summary>
    private static string BuildMultiPoolDamageWarning(
        RulesetAction action, string damageDice, SpellDefinition spell, bool damageDerived)
    {
        var totals = PoolTotalsBySlot(spell);
        var canonical = CanonicalizeCompositeDice(damageDice);
        if (totals.Values.Any(t => string.Equals(t, canonical, StringComparison.OrdinalIgnoreCase)))
        {
            return "";
        }

        var pools = spell.DamagePools!;
        var baseSlot = pools.First().Value.Keys.Min();
        var known = string.Join("; ", totals.OrderBy(kv => kv.Key).Select(kv =>
        {
            var parts = pools
                .Select(p => $"{(p.Value.TryGetValue(kv.Key, out var poolDice) ? poolDice : p.Value[p.Value.Keys.Min()])} {p.Key}")
                .ToList();
            if (spell.UpcastChoice?.BonusDicePerSlot is string bonus && kv.Key > baseSlot)
            {
                parts.Add($"{ScaleDice(bonus, kv.Key - baseSlot)} upcast (your choice of pool)");
            }
            return $"slot {kv.Key}: {string.Join(" + ", parts)}";
        }));
        var suffix = damageDerived ? "damage was resolved from pool data" : "damage was applied as sent";
        return $" [WARNING] '{action.ActionName}' damageDice '{damageDice}' doesn't match any known multi-pool total ({known}) " +
               $"— {suffix}, but check the slot level cast.";
    }

    private static readonly Dictionary<string, string> AbilityAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["str"] = "Strength",
        ["dex"] = "Dexterity",
        ["con"] = "Constitution",
        ["int"] = "Intelligence",
        ["wis"] = "Wisdom",
        ["cha"] = "Charisma",
    };

    private static string NormalizeSpellSlug(string name) =>
        name.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    private static string NormalizeDice(string dice) =>
        RegularExpressions.Regex.Replace(dice.Trim(), @"\s+", "");

    /// <summary>
    /// Nothing in the engine used to validate ruleset_action.parameters.damageDice against the
    /// caster's actual level or spell-slot tier, so an LLM caller guessing the wrong scaling tier
    /// (e.g. "3d10" — the level 11-16 tier for Fire Bolt — sent by a level 1 caster) rolled and
    /// applied real, unvalidated damage with a narrative that reads as entirely correct
    /// ("Attack 27 vs AC 12"). Now backed by dnd5eapi.co's real per-spell data instead of a
    /// 4-entry hardcoded cantrip table. Soft warning only, consistent with
    /// SpellSlotValidator's CantripWarning — damage still applies as sent, since a homebrew/plugin
    /// spell or an intentional reflavor has no SpellDefinition to check against, and this must not
    /// block real play. dnd5e only (pf2e spell damage is prose-only on AoN, not structured).
    /// Leveled (spell-slot-scaling) spells can't be pinned to an exact tier here: the resolver has
    /// no reliable signal for which slot level the caller spent (that's tracked by a separate
    /// ResourceChange, not this RulesetAction), so those are checked against "matches any known
    /// tier" rather than "matches the caster's specific level" the way cantrips are.
    /// </summary>
    private string BuildSpellDamageWarning(RulesetAction action, string damageDice, int? casterLevel, bool damageDerived = false)
    {
        if (action.ActionType != RulesetActionType.Spell || _spellDefinitionProvider == null)
        {
            return "";
        }

        var slug = NormalizeSpellSlug(action.ActionName);
        if (!_spellDefinitionProvider.TryGet(System, slug, out var spell) || spell == null)
        {
            return "";
        }

        var normalizedDice = NormalizeDice(damageDice);

        if (HasInstanceData(spell))
        {
            return BuildMultiInstanceDamageWarning(action, damageDice, normalizedDice, spell);
        }

        if (HasPoolData(spell))
        {
            return BuildMultiPoolDamageWarning(action, damageDice, spell, damageDerived);
        }

        if ((spell.Level ?? -1) == 0 && spell.DamageAtCharacterLevel is { Count: > 0 } byCharacterLevel)
        {
            var tier = byCharacterLevel.Keys.Where(l => l <= (casterLevel ?? 1)).DefaultIfEmpty(byCharacterLevel.Keys.Min()).Max();
            var expected = byCharacterLevel[tier];
            if (!string.Equals(NormalizeDice(expected), normalizedDice, StringComparison.OrdinalIgnoreCase))
            {
                return $" [WARNING] '{action.ActionName}' at caster level {casterLevel ?? 1} should deal " +
                       $"{expected}, not {damageDice} — damage was applied as sent, but check the cantrip-scaling tier.";
            }
        }
        else if (spell.DamageAtSlotLevel is { Count: > 0 } bySlotLevel)
        {
            if (!bySlotLevel.Values.Any(v => string.Equals(NormalizeDice(v), normalizedDice, StringComparison.OrdinalIgnoreCase)))
            {
                var known = string.Join(", ", bySlotLevel.OrderBy(kv => kv.Key).Select(kv => $"slot {kv.Key}: {kv.Value}"));
                return $" [WARNING] '{action.ActionName}' damageDice '{damageDice}' doesn't match any known spell-slot tier ({known}) " +
                       "— damage was applied as sent, but check the slot level cast.";
            }
        }

        return "";
    }

    /// <summary>
    /// Multi-instance spells (Magic Missile, Scorching Ray, Eldritch Blast) accept EITHER the
    /// caller's summed total ("3d4+3" — the pre-multi-instance caller shape, still used for slot
    /// inference) OR per-instance dice ("1d4+1", e.g. from a split-aware caller). Warns only when
    /// neither matches. Unlike the flat-tier check, damage here is resolved from per-instance
    /// data, not applied as sent.
    /// </summary>
    private static string BuildMultiInstanceDamageWarning(
        RulesetAction action, string damageDice, string normalizedDice, SpellDefinition spell)
    {
        var accepted = new List<string>();
        if (spell.DamageAtSlotLevel is { Count: > 0 } summed)
        {
            accepted.AddRange(summed.Values);
        }
        if (spell.DamageAtCharacterLevel is { Count: > 0 } summedCantrip)
        {
            accepted.AddRange(summedCantrip.Values);
        }
        if (spell.PerInstanceDamageAtSlotLevel is { Count: > 0 } perSlot)
        {
            accepted.AddRange(perSlot.Values);
        }
        if (spell.PerInstanceDamageAtCharacterLevel is { Count: > 0 } perChar)
        {
            accepted.AddRange(perChar.Values);
        }

        if (accepted.Any(v => string.Equals(NormalizeDice(v), normalizedDice, StringComparison.OrdinalIgnoreCase)))
        {
            return "";
        }

        var known = string.Join(", ", accepted.Distinct().OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
        return $" [WARNING] '{action.ActionName}' damageDice '{damageDice}' matches neither a known spell total nor a per-instance value ({known}) " +
               "— damage was resolved from per-instance data, but check the slot level cast.";
    }

    /// <summary>
    /// Pool-shaped damage (Sleep's HP-affect pool) can never be resolved as HP damage — failing
    /// loud here beats silently dealing the pool as damage.
    /// </summary>
    private static string BuildPoolDamageError(RulesetAction action)
    {
        action.Parameters.TryGetValue("damageDice", out var sent);
        var pool = string.IsNullOrWhiteSpace(sent) ? "an HP-affect pool" : $"an HP-affect pool ({sent})";
        return $"Error: '{action.ActionName}' rolls {pool}, not HP damage — the engine cannot resolve " +
            "pool-based spells. Roll the pool and narrate who is affected instead.";
    }

    /// <summary>Effective half-on-save flag: true unless the caller explicitly disabled it.</summary>
    private static bool ResolveHalfOnSave(Dictionary<string, string> parameters) =>
        !parameters.TryGetValue("halfOnSave", out var halfStr)
        || halfStr.Equals("true", StringComparison.OrdinalIgnoreCase)
        || halfStr == "1";

    /// <summary>
    /// dnd5e only. Spells whose SaveSuccess is "none" (Sacred Flame, Disintegrate, ...) deal no
    /// damage on a successful save — but halfOnSave defaults to true, so a caller that omits it
    /// silently deals half damage on a save. Soft warning only, same pattern as the
    /// damage/save-ability checks.
    /// </summary>
    private string BuildSpellHalfOnSaveWarning(RulesetAction action)
    {
        if (action.ActionType != RulesetActionType.Spell || _spellDefinitionProvider == null)
        {
            return "";
        }

        // No damage dice, no damage at stake: a pure control spell must not warn.
        if (!action.Parameters.ContainsKey("damageDice"))
        {
            return "";
        }

        var spell = LookupSpell(action);
        if (!string.Equals(spell?.SaveSuccess, "none", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        if (!ResolveHalfOnSave(action.Parameters))
        {
            return "";
        }

        return $" [WARNING] '{action.ActionName}' deals no damage on a successful save, " +
            "but halfOnSave is true (the default) — damage was applied as sent, but pass halfOnSave=false.";
    }

    /// <summary>dnd5e only, mirrors BuildSpellDamageWarning's soft-warning approach for the saving-throw ability.</summary>
    private string BuildSpellSaveTypeWarning(RulesetAction action, string saveName)
    {
        if (action.ActionType != RulesetActionType.Spell || _spellDefinitionProvider == null)
        {
            return "";
        }

        var slug = NormalizeSpellSlug(action.ActionName);
        if (!_spellDefinitionProvider.TryGet(System, slug, out var spell) || string.IsNullOrEmpty(spell?.SaveType))
        {
            return "";
        }

        var expectedAbility = AbilityAbbreviations.GetValueOrDefault(spell.SaveType.Trim(), spell.SaveType);
        if (!string.Equals(expectedAbility, saveName, StringComparison.OrdinalIgnoreCase))
        {
            return $" [WARNING] '{action.ActionName}' should use a {expectedAbility} save, not {saveName} " +
                   "— save was resolved as sent, but check the saving-throw ability.";
        }

        return "";
    }

    protected override async Task<ResolverResult> ResolveSkillCheckAsync(
        RulesetAction action,
        IChangeContext context,
        Dnd5eExtension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        if (!action.Parameters.TryGetValue("dc", out var dcStr) || !int.TryParse(dcStr, out var dc))
        {
            return ResolverResult.Fail("InvalidParameter", "Error: Skill check requires a 'dc' parameter.");
        }

        var skillName = action.Parameters.GetValueOrDefault("skill", action.ActionName);
        var bonus = GetSkillOrAbilityBonus(actorStats, skillName);
        bonus = ApplyAllModifiers(actorStats, bonus, "SkillCheck", skillName);

        var relationshipLabel = "neutral";
        var relationshipBonus = 0;
        if (SocialSkillGating.ShouldApplyRelationshipModifier(System, action, skillName))
        {
            var targetId = SocialSkillGating.ResolveRelationshipTargetId(action);
            if (targetId != null &&
                context.Characters.TryGetValue(targetId, out var target) &&
                context.Characters.TryGetValue(action.CharacterId, out var actor))
            {
                (relationshipBonus, relationshipLabel) = RelationshipModifierHelper.GetSocialModifier(
                    target, actor, CampaignConfigHelper.EffectiveConfig(context));
                bonus += relationshipBonus;
            }
        }

        var mechanic = GetMechanicFromAction(action);

        var outcome = await _rollService.RollAsync(new RollRequest
        {
            Tag = "skill",
            Expression = "1d20",
            Bonus = bonus,
            Mechanic = mechanic
        }, ct);

        var isSuccess = outcome.Result >= dc;
        var resultStr = isSuccess ? "Success" : "Failure";
        var relationshipSuffix = relationshipBonus != 0 ? $" ({relationshipLabel})" : "";

        return new ResolverResult
        {
            Narrative = $"{action.ActionName} ({skillName}): {resultStr}. Rolled {outcome.Result} vs DC {dc}.{relationshipSuffix} {outcome.Summary}",
            RollTotal = outcome.Result,
            Skill = skillName
        };
    }

    protected override async Task<ResolverResult> ResolveContestedCheckAsync(
        RulesetAction action, 
        IChangeContext context, 
        Dnd5eExtension actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct)
    {
        var targetId = action.TargetIds.FirstOrDefault();
        if (targetId == null || !context.Characters.TryGetValue(targetId, out var target))
        {
            return ResolverResult.Fail("InvalidTarget", "Error: No valid target specified for contested check.");
        }

        if (target.SystemStats is not Dnd5eExtension targetStats)
        {
            return ResolverResult.Fail("IncompatibleRuleset", "Error: Target uses incompatible ruleset stats for current ActiveSystem.");
        }

        var isGrapple = EngagementMutationHelper.IsGrappleAction(action);
        var isEscape = EngagementMutationHelper.IsEscapeGrappleAction(action);

        var actorSkill = action.Parameters.TryGetValue("skill", out var as_name)
            ? as_name
            : isGrapple || isEscape ? "Athletics" : "Strength";
        var targetSkill = action.Parameters.TryGetValue("targetSkill", out var ts_name)
            ? ts_name
            : isGrapple ? "Athletics" : actorSkill;

        var actorBonus = GetSkillOrAbilityBonus(actorStats, actorSkill);
        actorBonus = ApplyAllModifiers(actorStats, actorBonus, "SkillCheck", actorSkill);

        var relationshipLabel = "neutral";
        var relationshipBonus = 0;
        if (!isGrapple && !isEscape && SocialSkillGating.ShouldApplyRelationshipModifier(System, action, actorSkill))
        {
            if (context.Characters.TryGetValue(action.CharacterId, out var actor))
            {
                (relationshipBonus, relationshipLabel) = RelationshipModifierHelper.GetSocialModifier(
                    target, actor, CampaignConfigHelper.EffectiveConfig(context));
                actorBonus += relationshipBonus;
            }
        }

        var targetBonus = GetSkillOrAbilityBonus(targetStats, targetSkill);
        targetBonus = ApplyAllModifiers(targetStats, targetBonus, "SkillCheck", targetSkill);

        var actorRoll = await _rollService.RollAsync(new RollRequest { Tag = "actor", Expression = "1d20", Bonus = actorBonus, Mechanic = GetMechanicFromAction(action) }, ct);
        var targetRoll = await _rollService.RollAsync(new RollRequest { Tag = "target", Expression = "1d20", Bonus = targetBonus, Mechanic = DiceMechanic.Standard }, ct);

        var actorWins = actorRoll.Result > targetRoll.Result;
        var resultStr = actorWins ? "Actor Wins" : "Target Wins";

        if (isGrapple && actorWins)
        {
            EngagementMutationHelper.ApplyGrappleSuccess(action.CharacterId, targetId, mutations);
            resultStr += " Target is now grappled.";
        }
        else if (isEscape && actorWins)
        {
            EngagementMutationHelper.ApplyGrappleEscape(action.CharacterId, targetId, mutations);
            resultStr += " Actor breaks free of the grapple.";
        }

        var relationshipSuffix = relationshipBonus != 0 ? $" ({relationshipLabel})" : "";
        return ResolverResult.Ok($"{action.ActionName}: {resultStr}. Actor rolled {actorRoll.Result} ({actorSkill}){relationshipSuffix}, Target rolled {targetRoll.Result} ({targetSkill}).");
    }

    protected override async Task<ResolverResult> ResolveSavingThrowAsync(
        RulesetAction action, 
        IChangeContext context, 
        Dnd5eExtension actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct)
    {
        // Pool-shaped damage can never resolve as HP damage, even when a caller names the spell
        // on a single-save action. (Normal single saves name the ability, so this is a no-op for them.)
        var spell = LookupSpell(action);
        if (spell?.DamageIsPool == true)
        {
            return ResolverResult.Fail("UnresolvableMechanic", BuildPoolDamageError(action));
        }

        if (!action.Parameters.TryGetValue("dc", out var dcStr) || !int.TryParse(dcStr, out var dc))
        {
            return ResolverResult.Fail("InvalidParameter", "Error: Saving throw requires a 'dc' parameter.");
        }

        var saveName = action.Parameters.GetValueOrDefault("save", "Dexterity");
        var bonus = GetSavingThrowBonus(actorStats, saveName);
        
        bonus = ApplyAllModifiers(actorStats, bonus, "SavingThrow", saveName);
        var mechanic = GetMechanicFromAction(action);

        var outcome = await _rollService.RollAsync(new RollRequest
        {
            Tag = "save",
            Expression = "1d20",
            Bonus = bonus,
            Mechanic = mechanic
        }, ct);

        var isSuccess = outcome.Result >= dc;
        var resultStr = isSuccess ? "Success" : "Failure";
        
        var damageApplied = await TryApplySaveDamageAsync(
            action, action.CharacterId, isSuccess, mutations, _rollService, ct);
        var damageMsg = damageApplied > 0
            ? $" Took {damageApplied} damage."
            : string.Empty;

        return ResolverResult.Ok(
            $"{action.ActionName} ({saveName} Save): {resultStr}. Rolled {outcome.Result} vs DC {dc}.{damageMsg} {outcome.Summary}");
    }

    protected override async Task<ResolverResult> ResolveSpellSaveAsync(
        RulesetAction action,
        IChangeContext context,
        Dnd5eExtension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        var spell = LookupSpell(action);
        if (spell?.DamageIsPool == true)
        {
            return ResolverResult.Fail("UnresolvableMechanic", BuildPoolDamageError(action));
        }

        var targets = AttackTargetHelper.SelectTargets(action);
        if (targets.Count == 0)
        {
            return ResolverResult.Fail("InvalidTarget", "Error: Spell save requires at least one target in targetIds.");
        }

        var dc = ResolveSpellSaveDc(actorStats, action);
        if (dc <= 0)
        {
            return ResolverResult.Fail("InvalidParameter", "Error: Spell save requires a 'dc' parameter or spellSaveDc on the caster.");
        }

        var saveName = action.Parameters.GetValueOrDefault("save", "Dexterity");
        var narratives = new List<string>();
        var poolSpell = spell != null && HasPoolData(spell) ? spell : null;
        var poolShape = poolSpell != null ? ResolvePoolShape(action, poolSpell) : null;

        foreach (var targetId in targets)
        {
            if (!context.Characters.TryGetValue(targetId, out var target))
            {
                return ResolverResult.Fail("InvalidTarget", $"Error: Target '{targetId}' not found for spell save.");
            }

            if (target.SystemStats is not Dnd5eExtension targetStats)
            {
                return ResolverResult.Fail("IncompatibleRuleset", "Error: Target uses incompatible ruleset stats for current ActiveSystem.");
            }

            var bonus = GetSavingThrowBonus(targetStats, saveName);
            bonus = ApplyAllModifiers(targetStats, bonus, "SavingThrow", saveName);
            var mechanic = GetMechanicFromAction(action);

            var outcome = await _rollService.RollAsync(new RollRequest
            {
                Tag = "spell-save",
                Expression = "1d20",
                Bonus = bonus,
                Mechanic = mechanic,
            }, ct);

            var isSuccess = outcome.Result >= dc;
            int damage;
            string damageDetail;
            if (poolShape != null)
            {
                (damage, damageDetail) = await ApplyPoolSaveDamageAsync(
                    action, poolShape, targetId, isSuccess, mutations, _rollService, ct);
            }
            else
            {
                damage = await TryApplySaveDamageAsync(action, targetId, isSuccess, mutations, _rollService, ct);
                damageDetail = "";
            }
            narratives.Add(
                $"{action.ActionName} vs {target.Name}: {(isSuccess ? "Saved" : "Failed")} ({saveName} {outcome.Result} vs DC {dc})"
                + (damage > 0 ? $" — {damage} damage{damageDetail}." : "."));
        }

        var saveTypeWarning = BuildSpellSaveTypeWarning(action, saveName);
        var damageWarning = action.Parameters.TryGetValue("damageDice", out var saveDamageDice)
            ? BuildSpellDamageWarning(action, saveDamageDice, actorStats.Level, poolSpell != null)
            : "";
        var halfOnSaveWarning = BuildSpellHalfOnSaveWarning(action);

        var upcastNote = poolShape is { BonusDice: not null, BonusPool: null }
            ? $" [WARNING] '{action.ActionName}' upcast bonus has no chosen pool — pass upcastPool={string.Join("/", poolShape.Pools.Select(p => p.Type))} to attribute it (damage total is unaffected)."
            : "";

        return ResolverResult.Ok(string.Join(" | ", narratives) + saveTypeWarning + damageWarning + halfOnSaveWarning + upcastNote);
    }

    protected override async Task<ResolverResult> ResolveSpellUtilityAsync(
        RulesetAction action,
        IChangeContext context,
        Dnd5eExtension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        if (!action.Parameters.ContainsKey("dc"))
        {
            // F1: a targeted spell with no mechanics (e.g. Fire Bolt with targetIds and no
            // parameters) used to resolve as a utility no-op — no roll, but the action was
            // still consumed. Fail with a fix hint instead of silently doing nothing.
            // Only when Utility was inferred: an explicit resolution=utility opts out
            // (e.g. a buff whose status the caller commits separately in the same batch).
            if (action.TargetIds.Count > 0
                && !action.Parameters.ContainsKey("resolution")
                && !action.Parameters.ContainsKey("spellResolution"))
            {
                return ResolverResult.Fail("InvalidParameter",
                    $"Error: {action.ActionName} targets {action.TargetIds.Count} character(s) but supplies no spell mechanics, " +
                    "so there is nothing to resolve. Fix: pass the spell's mechanics explicitly — damageDice plus " +
                    "toHitBonus (or resolution=attack) for an attack roll like Fire Bolt, save + dc (plus damageDice) " +
                    "for a saving-throw spell, or resolution=utility for a genuine non-damaging effect (which needs no targets).");
            }

            return ResolverResult.Ok(
                $"{action.ActionName}: Utility spell cast outside combat. Narrate scouting, communication, or ward effects; commit status or knowledge_update if the scene changes.");
        }

        var skillName = action.Parameters.TryGetValue("skill", out var skill)
            ? skill
            : actorStats.SpellcastingAbility ?? "Arcana";
        action.Parameters["skill"] = skillName;
        return await ResolveSkillCheckAsync(action, context, actorStats, mutations, ct);
    }

    private int ResolveSpellSaveDc(Dnd5eExtension actorStats, RulesetAction action)
    {
        if (action.Parameters.TryGetValue("dc", out var dcStr) && int.TryParse(dcStr, out var explicitDc))
        {
            return explicitDc;
        }

        var level = actorStats.Level ?? 1;
        var proficiency = Dnd5eClassProfileResolver.ProficiencyBonus(level);
        var ability = actorStats.SpellcastingAbility
            ?? Dnd5eSpellcastingHelper.InferSpellcastingAbility(actorStats.ClassLevels)
            ?? "Intelligence";
        return Dnd5eSpellcastingHelper.ComputeSpellSaveDc(actorStats, proficiency, ability);
    }

    private int ResolveSpellAttackBonus(Dnd5eExtension actorStats)
    {
        if (actorStats.SpellAttackBonus is int bonus)
        {
            return bonus;
        }

        var level = actorStats.Level ?? 1;
        var proficiency = Dnd5eClassProfileResolver.ProficiencyBonus(level);
        var ability = actorStats.SpellcastingAbility
            ?? Dnd5eSpellcastingHelper.InferSpellcastingAbility(actorStats.ClassLevels)
            ?? "Intelligence";
        return Dnd5eSpellcastingHelper.ComputeSpellAttackBonus(actorStats, proficiency, ability);
    }

    private static async Task<int> TryApplySaveDamageAsync(
        RulesetAction action,
        string targetId,
        bool saved,
        List<WorldChange> mutations,
        IRollService rollService,
        CancellationToken ct)
    {
        if (!action.Parameters.TryGetValue("damageDice", out var damageDice))
        {
            return 0;
        }

        var damageBonus = 0;
        if (action.Parameters.TryGetValue("damageBonus", out var db))
        {
            int.TryParse(db, out damageBonus);
        }

        var damageRoll = await rollService.RollAsync(new RollRequest
        {
            Tag = "spell-damage",
            Expression = damageDice,
            Bonus = damageBonus,
            Mechanic = DiceMechanic.Standard,
        }, ct);

        var halfOnSave = ResolveHalfOnSave(action.Parameters);

        var damage = saved && halfOnSave
            ? (int)Math.Floor(damageRoll.Result / 2.0)
            : saved ? 0 : damageRoll.Result;

        if (damage > 0)
        {
            mutations.Add(new HpChange { CharacterId = targetId, Delta = -damage });
        }

        return damage;
    }

    public override async Task<float> RollInitiativeAsync(Character character, CancellationToken ct = default)
    {
        var stats = character.SystemStats as Dnd5eExtension ?? new Dnd5eExtension();
        var dexMod = stats.GetAbilityModifier(stats.Dexterity);
        dexMod = ApplyAllModifiers(stats, dexMod, "Initiative");
        
        var request = new RollRequest { Tag = "initiative", Expression = "1d20", Bonus = dexMod, Mechanic = DiceMechanic.Standard };
        var outcome = await _rollService.RollAsync(request, ct);
        
        // Use result + bonus as secondary tie-breaker (e.g. 15 roll + 2 mod = 15.1)
        // Widens dex-mod scaling to meaningfully break ties without fully dominating roll order
        return outcome.Result + (dexMod / 20f);
    }
}
