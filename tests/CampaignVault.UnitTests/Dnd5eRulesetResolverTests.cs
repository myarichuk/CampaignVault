using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

public class FakeRollService : IRollService
{
    public Queue<RollOutcome> NextRolls { get; } = new();
    public Queue<IReadOnlyList<RollOutcome>> NextBatches { get; } = new();
    public List<RollRequest> RecordedRequests { get; } = [];

    public Task<RollOutcome> RollAsync(RollRequest request, CancellationToken ct = default)
    {
        RecordedRequests.Add(request);
        return Task.FromResult(NextRolls.Dequeue());
    }

    public Task<IReadOnlyList<RollOutcome>> RollBatchAsync(IEnumerable<RollRequest> requests,
        CancellationToken ct = default)
    {
        return Task.FromResult(NextBatches.Dequeue());
    }
}

public class Dnd5eRulesetResolverTests
{
    /// <summary>Real embedded dnd5e SpellDefinition data (fire_bolt, fireball, ...) extracted to a fresh temp dir, matching the pattern RulesetActionHandlerSpellComponentTests uses.</summary>
    private static SpellDefinitionProvider CreateSpellProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv_dnd5e_resolver_test_" + Guid.NewGuid());
        return new SpellDefinitionProvider(dir, typeof(SpellDefinitionProvider).Assembly);
    }

    private ChangeContext CreateContext(params Character[] characters) =>
        CreateContext(items: null, characters);

    private ChangeContext CreateContext(Dictionary<string, Item>? items, params Character[] characters)
    {
        var charDict = characters.ToDictionary(c => c.Id);
        return new ChangeContext(
            sessionForTests: null,
            characters: charDict,
            items: items ?? new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher(
                new IWorldChangeHandler[0],
                new CampaignVault.Data.CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null
        );
    }

    [Fact]
    public async Task ResolveAttack_Hit_GeneratesHpChange()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 15, HasCritical = false, HasComplication = false, Summary = "Rolled 15" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });

        var resolver = new Dnd5eRulesetResolver(rollService);

        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension { ArmorClass = 14 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Longsword",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d8" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Single(output.Mutations);
        var hpChange = Assert.IsType<HpChange>(output.Mutations[0]);
        Assert.Equal("char2", hpChange.CharacterId);
        Assert.Equal(-8, hpChange.Delta);
        Assert.Contains("Hit for 8 damage", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_ToHitBonusAlias_AppliesAttackBonus()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 12, HasCritical = false, HasComplication = false, Summary = "[8] + 4 = 12" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 5, Summary = "Rolled 5" });

        var resolver = new Dnd5eRulesetResolver(rollService);

        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Dagger",
            Parameters = new Dictionary<string, string> { ["toHitBonus"] = "4" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Equal(4, rollService.RecordedRequests[0].Bonus);
        Assert.Single(output.Mutations);
        Assert.Contains("Attack 12 vs AC 12", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_Miss_GeneratesNoMutations()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 12, HasCritical = false, HasComplication = false, Summary = "Rolled 12" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });

        var resolver = new Dnd5eRulesetResolver(rollService);

        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension { ArmorClass = 14 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Longsword"
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Empty(output.Mutations);
        Assert.Contains("Missed", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_CriticalHit_RollsExtraDamage()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 20, HasCritical = true, HasComplication = false, Summary = "Rolled Nat 20" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 5, Summary = "Rolled 5" });

        var resolver = new Dnd5eRulesetResolver(rollService);

        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension { ArmorClass = 25 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Longsword",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d8" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Single(output.Mutations);
        var hpChange = Assert.IsType<HpChange>(output.Mutations[0]);
        Assert.Equal(-13, hpChange.Delta);
        Assert.Contains("CRITICAL HIT!", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSkillCheck_SucceedsAgainstDC()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 16, Summary = "Rolled 16" });

        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character
        {
            Id = "char1",
            SystemStats = new Dnd5eExtension { SkillModifiers = new Dictionary<string, int> { { "Stealth", 5 } } }
        };

        var context = CreateContext(actor);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            ActionType = RulesetActionType.SkillCheck,
            ActionName = "Sneak",
            Parameters = new Dictionary<string, string> { ["skill"] = "Stealth", ["dc"] = "15" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Empty(output.Mutations);
        Assert.Contains("Success", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSkillCheck_NoSkillParameter_DerivesSkillFromActionName()
    {
        // Regression: ResolveSkillCheckAsync used to default an omitted "skill" parameter to the
        // literal string "Strength", so any check committed the documented way (actionName carries
        // the skill; no redundant parameters.skill) silently rolled against the wrong ability.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 12, Summary = "Rolled 12" });

        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character
        {
            Id = "char1",
            SystemStats = new Dnd5eExtension
            {
                Strength = 20, // ability-mod fallback for "Strength" would be +5
                SkillModifiers = new Dictionary<string, int> { { "Investigation", 3 } }
            }
        };

        var context = CreateContext(actor);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            ActionType = RulesetActionType.SkillCheck,
            ActionName = "Investigation",
            Parameters = new Dictionary<string, string> { ["dc"] = "15" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Equal(3, rollService.RecordedRequests[0].Bonus);
        Assert.Contains("Investigation", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_Spell_WrongCantripTier_WarnsButStillApplies()
    {
        // Regression: nothing used to validate parameters.damageDice against the caster's level, so
        // an LLM caller could send the level-11-16 tier ("3d10") for a level-1 caster and the engine
        // applied it silently, with a narrative that reads as entirely correct. Damage still applies
        // as sent (soft warning only, matching SpellSlotValidator's CantripWarning pattern) - this
        // test locks in that the warning fires and damage is unaffected. Backed by the real
        // fire_bolt.yaml SpellDefinition, not a hardcoded table.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 27, HasCritical = false, HasComplication = false, Summary = "Rolled 27" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 20, Summary = "Rolled 20" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 1 } };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Fire Bolt",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["bonus"] = "9", ["damageDice"] = "3d10" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Single(output.Mutations);
        var hpChange = Assert.IsType<HpChange>(output.Mutations[0]);
        Assert.Equal(-20, hpChange.Delta);
        Assert.Contains("Hit for 20 damage", output.Result.Narrative);
        Assert.Contains("[WARNING]", output.Result.Narrative);
        Assert.Contains("should deal 1d10", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_Spell_CorrectCantripTier_NoWarning()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 15, HasCritical = false, HasComplication = false, Summary = "Rolled 15" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 7, Summary = "Rolled 7" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 1 } };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Fire Bolt",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["bonus"] = "9", ["damageDice"] = "1d10" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_Spell_NoSpellProvider_NoWarning()
    {
        // A resolver built without a SpellDefinitionProvider (e.g. minimal test setups elsewhere)
        // must not throw or misbehave - damage validation just silently doesn't run.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 27, HasCritical = false, HasComplication = false, Summary = "Rolled 27" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 20, Summary = "Rolled 20" });

        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 1 } };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Fire Bolt",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["bonus"] = "9", ["damageDice"] = "3d10" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSpellSave_Fireball_WrongSlotTier_WarnsButStillApplies()
    {
        // Fireball is a leveled, save-based, slot-scaling spell - the case the cantrip-only table
        // never covered. The resolver can't know which slot level the caller spent (that's a
        // separate ResourceChange), so it checks "matches some known tier" rather than pinning an
        // exact one; "12d6" isn't in Fireball's {8d6..14d6} table at all, so it should still warn.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 15, Summary = "Rolled 15" }); // save roll
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 30, Summary = "Rolled 30" }); // damage roll

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 5 } };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Fireball",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Dexterity", ["damageDice"] = "20d6" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Contains("[WARNING]", output.Result.Narrative);
        Assert.Contains("doesn't match any known spell-slot tier", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSpellSave_Fireball_KnownSlotTier_NoWarning()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 15, Summary = "Rolled 15" }); // save roll
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 30, Summary = "Rolled 30" }); // damage roll

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 5 } };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Fireball",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Dexterity", ["damageDice"] = "9d6" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSpellSave_Fireball_WrongSaveAbility_Warns()
    {
        // Fireball's SpellDefinition carries saveType "dex" (SRD 5.1). Sending "save": "Wisdom"
        // should warn but still resolve the save as sent (soft warning, not a hard block).
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 15, Summary = "Rolled 15" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 5 } };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Fireball",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Wisdom" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Contains("[WARNING]", output.Result.Narrative);
        Assert.Contains("should use a Dexterity save, not Wisdom", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSpellSave_IceStorm_FailedSave_RollsEachPoolSeparately()
    {
        // Multi-pool damage is derived from the spell's pool data, not the caller's
        // single damageDice string (which can't express two typed pools) — the same
        // derived-not-caller-sent rule as multi-instance damage.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" }); // failed save
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 7, Summary = "Rolled 7" }); // bludgeoning pool
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 12, Summary = "Rolled 12" }); // cold pool

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 7 } };
        var target = new Character { Id = "char2", Name = "Goblin", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Ice Storm",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Dexterity", ["damageDice"] = "2d8+4d6" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        var damageRequests = rollService.RecordedRequests.Where(r => r.Tag == "spell-damage").ToList();
        Assert.Collection(damageRequests,
            r => Assert.Equal("2d8", r.Expression),
            r => Assert.Equal("4d6", r.Expression));
        var hp = Assert.Single(output.Mutations.OfType<HpChange>());
        Assert.Equal(-19, hp.Delta);
        Assert.Contains("7 bludgeoning + 12 cold", output.Result.Narrative);
        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSpellSave_IceStorm_SavedSave_AppliesHalfOfTotal()
    {
        // "Half as much damage" halves the summed pools, not each pool separately.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 18, Summary = "Rolled 18" }); // successful save
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 7, Summary = "Rolled 7" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 12, Summary = "Rolled 12" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 7 } };
        var target = new Character { Id = "char2", Name = "Goblin", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Ice Storm",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Dexterity", ["damageDice"] = "4d6+2d8" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        var hp = Assert.Single(output.Mutations.OfType<HpChange>());
        Assert.Equal(-9, hp.Delta); // floor(19 / 2)
        Assert.Contains("Saved", output.Result.Narrative);
        Assert.DoesNotContain("[WARNING]", output.Result.Narrative); // pool order is insignificant
    }

    [Fact]
    public async Task ResolveSpellSave_IceStorm_FirstPoolDiceOnly_WarnsButResolvesPools()
    {
        // The old flat table blessed "2d8" (bludgeoning only, cold silently dropped).
        // Against pool data it must warn — while still resolving both pools.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 7, Summary = "Rolled 7" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 12, Summary = "Rolled 12" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 7 } };
        var target = new Character { Id = "char2", Name = "Goblin", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Ice Storm",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Dexterity", ["damageDice"] = "2d8" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        Assert.Contains("doesn't match any known multi-pool total", output.Result.Narrative);
        Assert.Contains("resolved from pool data", output.Result.Narrative);
        Assert.Equal(2, rollService.RecordedRequests.Count(r => r.Tag == "spell-damage"));
        var hp = Assert.Single(output.Mutations.OfType<HpChange>());
        Assert.Equal(-19, hp.Delta);
    }

    [Fact]
    public async Task ResolveSpellSave_IceStorm_UpcastTotal_InfersSlot()
    {
        // No slot-level signal on the action (the spend is a separate ResourceChange),
        // so the tier is inferred from the caller's summed total — same as multi-instance.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 10, Summary = "Rolled 10" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 12, Summary = "Rolled 12" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 9 } };
        var target = new Character { Id = "char2", Name = "Goblin", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Ice Storm",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Dexterity", ["damageDice"] = "3d8+4d6" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        var damageRequests = rollService.RecordedRequests.Where(r => r.Tag == "spell-damage").ToList();
        Assert.Collection(damageRequests,
            r => Assert.Equal("3d8", r.Expression),
            r => Assert.Equal("4d6", r.Expression));
        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSpellSave_MeteorSwarm_CombinedDice_RollsBothPools()
    {
        // Same-sided pools accept the combined total ("40d6" == "20d6+20d6").
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 5, Summary = "Rolled 5" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 60, Summary = "Rolled 60" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 55, Summary = "Rolled 55" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 17 } };
        var target = new Character { Id = "char2", Name = "Goblin", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Meteor Swarm",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Dexterity", ["damageDice"] = "40d6" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        var damageRequests = rollService.RecordedRequests.Where(r => r.Tag == "spell-damage").ToList();
        Assert.Collection(damageRequests,
            r => Assert.Equal("20d6", r.Expression),
            r => Assert.Equal("20d6", r.Expression));
        var hp = Assert.Single(output.Mutations.OfType<HpChange>());
        Assert.Equal(-115, hp.Delta);
        Assert.Contains("60 fire + 55 bludgeoning", output.Result.Narrative);
        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSpellSave_FlameStrike_BaseSlot_RollsBothPools()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 10, Summary = "Rolled 10" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 11, Summary = "Rolled 11" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 9 } };
        var target = new Character { Id = "char2", Name = "Goblin", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Flame Strike",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Dexterity", ["damageDice"] = "8d6" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        var damageRequests = rollService.RecordedRequests.Where(r => r.Tag == "spell-damage").ToList();
        Assert.Collection(damageRequests,
            r => Assert.Equal("4d6", r.Expression),
            r => Assert.Equal("4d6", r.Expression));
        var hp = Assert.Single(output.Mutations.OfType<HpChange>());
        Assert.Equal(-21, hp.Delta);
        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSpellSave_FlameStrike_Upcast_AttributesBonusToChosenPool()
    {
        // Slot 6 ("9d6") adds 1d6 to the pool named by upcastPool.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 10, Summary = "Rolled 10" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 11, Summary = "Rolled 11" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 4, Summary = "Rolled 4" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 11 } };
        var target = new Character { Id = "char2", Name = "Goblin", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Flame Strike",
            Parameters = new Dictionary<string, string>
            {
                ["dc"] = "15",
                ["save"] = "Dexterity",
                ["damageDice"] = "9d6",
                ["upcastPool"] = "radiant",
            }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        var damageRequests = rollService.RecordedRequests.Where(r => r.Tag == "spell-damage").ToList();
        Assert.Collection(damageRequests,
            r => Assert.Equal("4d6", r.Expression),
            r => Assert.Equal("4d6", r.Expression),
            r => Assert.Equal("1d6", r.Expression));
        var hp = Assert.Single(output.Mutations.OfType<HpChange>());
        Assert.Equal(-25, hp.Delta);
        Assert.Contains("4 upcast radiant", output.Result.Narrative);
        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSpellSave_FlameStrike_Upcast_WithoutChoice_StillResolvesWithNote()
    {
        // The upcast total is correct regardless of attribution (addition commutes),
        // so a missing upcastPool only adds a guidance note — it never blocks.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 10, Summary = "Rolled 10" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 11, Summary = "Rolled 11" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 4, Summary = "Rolled 4" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 11 } };
        var target = new Character { Id = "char2", Name = "Goblin", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Flame Strike",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Dexterity", ["damageDice"] = "9d6" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        Assert.Equal(3, rollService.RecordedRequests.Count(r => r.Tag == "spell-damage"));
        var hp = Assert.Single(output.Mutations.OfType<HpChange>());
        Assert.Equal(-25, hp.Delta);
        Assert.Contains("upcastPool=", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_PoolSpell_AsAttack_WarnsWithAppliedAsSent()
    {
        // Pool spells are save spells; sent down the attack path the engine applies the
        // caller's dice as sent (soft-warning philosophy) with a pool-aware warning.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 20, Summary = "Rolled 20" }); // attack roll
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 12, Summary = "Rolled 12" }); // damage roll

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 9 } };
        var target = new Character { Id = "char2", Name = "Goblin", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Flame Strike",
            Parameters = new Dictionary<string, string> { ["bonus"] = "5", ["damageDice"] = "4d6" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        Assert.Contains("doesn't match any known multi-pool total", output.Result.Narrative);
        Assert.Contains("applied as sent", output.Result.Narrative);
        var hp = Assert.Single(output.Mutations.OfType<HpChange>());
        Assert.Equal(-12, hp.Delta);
    }

    [Fact]
    public async Task ResolveAttack_InvalidBonus_ReturnsError()
    {
        var rollService = new FakeRollService();
        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Attack",
            Parameters = new Dictionary<string, string> { ["bonus"] = "not_a_number" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Contains("invalid bonus value", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_MismatchedTargetExtension_ReturnsError()
    {
        var rollService = new FakeRollService();
        var resolver = new Dnd5eRulesetResolver(rollService);

        // Actor is correct, but target is using a different system's extension (e.g. Pf2eExtension or base SystemExtension)
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Pf2eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Attack"
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Contains("incompatible ruleset stats", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveContestedCheck_Success()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 18 }); // Actor
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 12 }); // Target

        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.ContestedCheck,
            ActionName = "Grapple"
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Contains("Actor Wins", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAsync_SavingThrow_UsesAdvantageState()
    {
        var mockRollService = Substitute.For<IRollService>();
        mockRollService.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Result = 15, Summary = "Rolled 15" }));

        var resolver = new Dnd5eRulesetResolver(mockRollService);

        var actor = new Character { Id = "test-char", SystemStats = new Dnd5eExtension { Dexterity = 14 } };
        var context = CreateContext(actor);

        var action = new RulesetAction
        {
            CharacterId = "test-char",
            ActionType = RulesetActionType.SavingThrow,
            ActionName = "Dexterity Save",
            AdvantageState = AdvantageState.Advantage,
            Parameters = new Dictionary<string, string> { { "dc", "14" }, { "save", "Dexterity" } }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        await mockRollService.Received(1).RollAsync(Arg.Is<RollRequest>(req => req.Mechanic == DiceMechanic.Advantage),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAttackAsync_AppliesDamageResistance()
    {
        var mockRollService = Substitute.For<IRollService>();
        mockRollService.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(new RollOutcome { Result = 20, Summary = "Hit" }), // Attack
                Task.FromResult(new RollOutcome { Result = 10, Summary = "Damage" }) // Damage
            );

        var resolver = new Dnd5eRulesetResolver(mockRollService);

        var targetId = "char_2";
        var context = CreateContext(
            new Character { Id = "test-char", SystemStats = new Dnd5eExtension() },
            new Character
            {
                Id = targetId,
                SystemStats = new Dnd5eExtension
                    { DamageModifiers = new Dictionary<string, float> { { "Fire", 0.5f } } }
            }
        );

        var action = new RulesetAction
        {
            CharacterId = "test-char",
            TargetIds = [targetId],
            ActionType = RulesetActionType.Attack,
            ActionName = "Fire Bolt",
            DamageType = "Fire",
            Parameters = new Dictionary<string, string> { { "damageDice", "1d10" } }
        };

        var output = await resolver.ResolveAsync(context, action);

        var hpChange = output.Mutations.OfType<HpChange>().FirstOrDefault();
        Assert.NotNull(hpChange);
        Assert.Equal(-5, hpChange.Delta); // 10 damage * 0.5 resistance
    }

    [Fact]
    public async Task ResolveAttack_AutoAppliesHeldWeaponProperties_ByActionName()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 18, HasCritical = false, HasComplication = false, Summary = "Rolled 18" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 9, Summary = "Rolled 9" });

        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character { Id = "chars/valen", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "chars/merc-1", Name = "Merc", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };
        var schlag = new Item
        {
            Id = "items/schlag",
            Name = "Schlag",
            HolderId = "chars/valen",
            CoreCategory = ItemCategories.Weapon,
            Properties = new Dictionary<string, object>
            {
                ["damageDice"] = "1d10",
                ["bonus"] = "9",
                ["damageBonus"] = "5"
            }
        };

        var context = CreateContext(
            new Dictionary<string, Item> { [schlag.Id] = schlag },
            actor,
            target);

        var action = new RulesetAction
        {
            CharacterId = "chars/valen",
            TargetIds = ["chars/merc-1"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Schlag"
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Equal(9, rollService.RecordedRequests[0].Bonus);
        Assert.Equal("1d10", rollService.RecordedRequests[1].Expression);
        Assert.Equal(5, rollService.RecordedRequests[1].Bonus);
        Assert.Single(output.Mutations);
        Assert.Contains("Schlag vs Merc: Hit for", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_MultiTarget_ResolvesSeparateRollsPerTarget()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 15, HasCritical = false, HasComplication = false, Summary = "Rolled 15" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 6, Summary = "Rolled 6" });
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 16, HasCritical = false, HasComplication = false, Summary = "Rolled 16" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 7, Summary = "Rolled 7" });

        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character { Id = "chars/valen", SystemStats = new Dnd5eExtension() };
        var merc1 = new Character { Id = "chars/merc-1", Name = "Merc 1", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };
        var merc2 = new Character { Id = "chars/merc-2", Name = "Merc 2", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };

        var context = CreateContext(actor, merc1, merc2);
        var action = new RulesetAction
        {
            CharacterId = "chars/valen",
            TargetIds = ["chars/merc-1", "chars/merc-2"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Schlag",
            Parameters = new Dictionary<string, string>
            {
                ["damageDice"] = "1d6",
                ["attackCount"] = "2"
            }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Equal(2, output.Mutations.Count);
        Assert.Contains("Merc 1", output.Result.Narrative);
        Assert.Contains("Merc 2", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_MagicMissile_AutoHitsWithoutAttackRoll()
    {
        // Magic Missile auto-hits: no attack roll is consumed even against unhittable AC, and the
        // caller's summed total ("3d4+3") resolves as 3 independent per-instance rolls ("1d4+1").
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 4, Summary = "Rolled 4" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 3, Summary = "Rolled 3" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 5, Summary = "Rolled 5" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 1 } };
        var target = new Character { Id = "char2", Name = "Goblin", SystemStats = new Dnd5eExtension { ArmorClass = 30 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Magic Missile",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["damageDice"] = "3d4+3" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Equal(3, output.Mutations.Count);
        Assert.Equal(new[] { -4, -3, -5 }, output.Mutations.Select(m => Assert.IsType<HpChange>(m).Delta));
        Assert.All(output.Mutations, m => Assert.Equal("char2", Assert.IsType<HpChange>(m).CharacterId));
        Assert.Equal(3, output.Result.Narrative.Split(" | ").Length);
        Assert.Contains("(auto-hit)", output.Result.Narrative);
        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
        Assert.Equal(3, rollService.RecordedRequests.Count);
        Assert.All(rollService.RecordedRequests, r =>
        {
            Assert.Equal("damage", r.Tag);
            Assert.Equal("1d4+1", r.Expression);
        });
    }

    [Fact]
    public async Task ResolveAttack_MagicMissile_SplitAcrossThreeTargets_AppliesIndependently()
    {
        var rollService = new FakeRollService();
        foreach (var total in new[] { 4, 2, 5 })
        {
            rollService.NextRolls.Enqueue(new RollOutcome { Result = total, Summary = $"Rolled {total}" });
        }

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 1 } };
        var goblin1 = new Character { Id = "char2", Name = "Goblin 1", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };
        var goblin2 = new Character { Id = "char3", Name = "Goblin 2", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };
        var goblin3 = new Character { Id = "char4", Name = "Goblin 3", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };

        var context = CreateContext(actor, goblin1, goblin2, goblin3);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2", "char3", "char4"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Magic Missile",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["damageDice"] = "3d4+3" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Equal(3, output.Mutations.Count);
        Assert.Equal("char2", Assert.IsType<HpChange>(output.Mutations[0]).CharacterId);
        Assert.Equal("char3", Assert.IsType<HpChange>(output.Mutations[1]).CharacterId);
        Assert.Equal("char4", Assert.IsType<HpChange>(output.Mutations[2]).CharacterId);
        Assert.Equal(-4, Assert.IsType<HpChange>(output.Mutations[0]).Delta);
        Assert.Equal(-2, Assert.IsType<HpChange>(output.Mutations[1]).Delta);
        Assert.Equal(-5, Assert.IsType<HpChange>(output.Mutations[2]).Delta);
    }

    [Fact]
    public async Task ResolveAttack_MagicMissile_UpcastTotal_InfersSlotDartCount()
    {
        // "5d4+5" matches slot 3's summed total, so the engine resolves 5 darts, not the base 3.
        var rollService = new FakeRollService();
        for (var i = 0; i < 5; i++)
        {
            rollService.NextRolls.Enqueue(new RollOutcome { Result = 3, Summary = "Rolled 3" });
        }

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 5 } };
        var target = new Character { Id = "char2", Name = "Ogre", SystemStats = new Dnd5eExtension { ArmorClass = 11 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Magic Missile",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["damageDice"] = "5d4+5" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Equal(5, output.Mutations.Count);
        Assert.Equal(5, output.Result.Narrative.Split(" | ").Length);
        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
        Assert.All(rollService.RecordedRequests, r => Assert.Equal("1d4+1", r.Expression));
    }

    [Fact]
    public async Task ResolveAttack_ScorchingRay_ThreeRaysAtOneTarget_EachRolledIndependently()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 15, HasCritical = false, HasComplication = false, Summary = "Rolled 15" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 7, Summary = "Rolled 7" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 18, HasCritical = false, HasComplication = false, Summary = "Rolled 18" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 4, Summary = "Rolled 4" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 5, HasCritical = false, HasComplication = false, Summary = "Rolled 5" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 6, Summary = "Rolled 6" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 3 } };
        var target = new Character { Id = "char2", Name = "Orc", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Scorching Ray",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["bonus"] = "0", ["damageDice"] = "2d6" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Equal(2, output.Mutations.Count);
        Assert.Equal(-7, Assert.IsType<HpChange>(output.Mutations[0]).Delta);
        Assert.Equal(-4, Assert.IsType<HpChange>(output.Mutations[1]).Delta);
        Assert.Contains("Hit for 7 damage", output.Result.Narrative);
        Assert.Contains("Hit for 4 damage", output.Result.Narrative);
        Assert.Contains("Missed", output.Result.Narrative);
        Assert.Equal(6, rollService.RecordedRequests.Count);
        Assert.All(
            rollService.RecordedRequests.Where(r => r.Tag == "damage"),
            r => Assert.Equal("2d6", r.Expression));
    }

    [Fact]
    public async Task ResolveAttack_ScorchingRay_PerRayDamage_DoesNotFalseWarnAgainstSummedTier()
    {
        // "2d6" is both the API's slot-2 entry and the per-ray value — either reading must not warn.
        // Also exercises the uneven split: 3 rays over 2 targets → 2/1.
        var rollService = new FakeRollService();
        foreach (var damage in new[] { 5, 6, 3 })
        {
            rollService.NextRolls.Enqueue(new RollOutcome { Result = 15, HasCritical = false, HasComplication = false, Summary = "Rolled 15" });
            rollService.NextRolls.Enqueue(new RollOutcome { Result = damage, Summary = $"Rolled {damage}" });
        }

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 3 } };
        var orc1 = new Character { Id = "char2", Name = "Orc 1", SystemStats = new Dnd5eExtension { ArmorClass = 10 } };
        var orc2 = new Character { Id = "char3", Name = "Orc 2", SystemStats = new Dnd5eExtension { ArmorClass = 10 } };

        var context = CreateContext(actor, orc1, orc2);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2", "char3"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Scorching Ray",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["bonus"] = "0", ["damageDice"] = "2d6" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Equal(3, output.Mutations.Count);
        Assert.Equal("char2", Assert.IsType<HpChange>(output.Mutations[0]).CharacterId);
        Assert.Equal("char3", Assert.IsType<HpChange>(output.Mutations[1]).CharacterId);
        Assert.Equal("char2", Assert.IsType<HpChange>(output.Mutations[2]).CharacterId);
        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_ScorchingRay_ExplicitAttackCount_OverridesDerivedCount()
    {
        // Upcast expression the data alone can't carry: 4 rays (a 3rd-level slot) via attackCount.
        var rollService = new FakeRollService();
        for (var i = 0; i < 4; i++)
        {
            rollService.NextRolls.Enqueue(new RollOutcome { Result = 15, HasCritical = false, HasComplication = false, Summary = "Rolled 15" });
            rollService.NextRolls.Enqueue(new RollOutcome { Result = 5, Summary = "Rolled 5" });
        }

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 5 } };
        var target = new Character { Id = "char2", Name = "Troll", SystemStats = new Dnd5eExtension { ArmorClass = 10 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Scorching Ray",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["bonus"] = "0", ["damageDice"] = "2d6", ["attackCount"] = "4" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Equal(4, output.Mutations.Count);
        Assert.Equal(4, output.Result.Narrative.Split(" | ").Length);
    }

    [Fact]
    public async Task ResolveAttack_EldritchBlast_Level5_ResolvesTwoBeams()
    {
        // Cantrip instance counts key off caster level, not slot inference: level 5 → 2 beams.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 16, HasCritical = false, HasComplication = false, Summary = "Rolled 16" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 14, HasCritical = false, HasComplication = false, Summary = "Rolled 14" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 6, Summary = "Rolled 6" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 5 } };
        var target = new Character { Id = "char2", Name = "Cultist", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Eldritch Blast",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["bonus"] = "0", ["damageDice"] = "1d10" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Equal(2, output.Mutations.Count);
        Assert.Equal(-8, Assert.IsType<HpChange>(output.Mutations[0]).Delta);
        Assert.Equal(-6, Assert.IsType<HpChange>(output.Mutations[1]).Delta);
        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
        Assert.All(
            rollService.RecordedRequests.Where(r => r.Tag == "damage"),
            r => Assert.Equal("1d10", r.Expression));
    }

    [Fact]
    public async Task ResolveAttack_AcidArrow_Miss_StillAppliesHalfDamage()
    {
        // Acid Arrow deals half its initial damage even on a miss (and no delayed tick — Phase 4).
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 5, HasCritical = false, HasComplication = false, Summary = "Rolled 5" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 10, Summary = "Rolled 10" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 3 } };
        var target = new Character { Id = "char2", Name = "Ogre", SystemStats = new Dnd5eExtension { ArmorClass = 14 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Acid Arrow",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["bonus"] = "0", ["damageDice"] = "4d4" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Single(output.Mutations);
        Assert.Equal(-5, Assert.IsType<HpChange>(output.Mutations[0]).Delta);
        Assert.Contains("still splashes for 5 damage", output.Result.Narrative);
        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_AcidArrow_Hit_EmitsDelayedTickStatus()
    {
        // A hit schedules the delayed tick as a synthetic residue effect alongside the HP change.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 15, HasCritical = false, HasComplication = false, Summary = "Rolled 15" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 12, Summary = "Rolled 12" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 3 } };
        var target = new Character { Id = "char2", Name = "Ogre", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Acid Arrow",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["bonus"] = "0", ["damageDice"] = "4d4" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Equal(2, output.Mutations.Count);
        Assert.Equal(-12, Assert.IsType<HpChange>(output.Mutations[0]).Delta);
        var status = Assert.IsType<StatusChange>(output.Mutations[1]);
        Assert.Equal("char2", status.CharacterId);
        Assert.NotNull(status.Effect);
        Assert.Equal("AcidArrowResidue", status.Effect.Name);
        Assert.True(status.Effect.ExpiresAtOwnTurnStart);
        Assert.NotNull(status.Effect.PendingDamage);
        Assert.Equal("2d4", status.Effect.PendingDamage.DiceExpression);
        Assert.Equal("acid", status.Effect.PendingDamage.DamageType);
        Assert.Contains("Acid clings to Ogre (2d4 at the start of their next turn).", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_AcidArrow_UpcastHit_SchedulesSlotTickDice()
    {
        // "5d4" matches slot 3's total, so the scheduled tick uses the slot-3 dice ("3d4").
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 16, HasCritical = false, HasComplication = false, Summary = "Rolled 16" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 14, Summary = "Rolled 14" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 5 } };
        var target = new Character { Id = "char2", Name = "Troll", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Acid Arrow",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["bonus"] = "0", ["damageDice"] = "5d4" }
        };

        var output = await resolver.ResolveAsync(context, action);

        var status = Assert.IsType<StatusChange>(output.Mutations[1]);
        Assert.Equal("3d4", status.Effect!.PendingDamage!.DiceExpression);
        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_AcidArrow_Miss_SchedulesNoTick()
    {
        // RequiresInitialHit: the splash still lands (Phase 3), but no residue is scheduled.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 5, HasCritical = false, HasComplication = false, Summary = "Rolled 5" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 10, Summary = "Rolled 10" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 3 } };
        var target = new Character { Id = "char2", Name = "Ogre", SystemStats = new Dnd5eExtension { ArmorClass = 14 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Acid Arrow",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["bonus"] = "0", ["damageDice"] = "4d4" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Single(output.Mutations);
        Assert.IsType<HpChange>(output.Mutations[0]);
        Assert.DoesNotContain("Acid clings", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSpellSave_SaveSuccessNone_WarnsWhenHalfOnSaveDefaultsToTrue()
    {
        // Sacred Flame deals no damage on a save — but halfOnSave defaults to true, so omitting
        // it silently applies half. Soft warning only: the half damage still lands.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 16, HasCritical = false, HasComplication = false, Summary = "Rolled 16" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 6, Summary = "Rolled 6" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 1 } };
        var target = new Character { Id = "char2", Name = "Skeleton", SystemStats = new Dnd5eExtension { Dexterity = 10 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Sacred Flame",
            Parameters = new Dictionary<string, string> { ["resolution"] = "save", ["save"] = "Dexterity", ["dc"] = "15", ["damageDice"] = "1d8" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Single(output.Mutations);
        Assert.Equal(-3, Assert.IsType<HpChange>(output.Mutations[0]).Delta);
        Assert.Contains("Saved", output.Result.Narrative);
        Assert.Contains("[WARNING]", output.Result.Narrative);
        Assert.Contains("halfOnSave=false", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSpellSave_SaveSuccessNone_ExplicitHalfOnSaveFalse_NoWarningNoDamage()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 16, HasCritical = false, HasComplication = false, Summary = "Rolled 16" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 6, Summary = "Rolled 6" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 1 } };
        var target = new Character { Id = "char2", Name = "Skeleton", SystemStats = new Dnd5eExtension { Dexterity = 10 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Sacred Flame",
            Parameters = new Dictionary<string, string> { ["resolution"] = "save", ["save"] = "Dexterity", ["dc"] = "15", ["damageDice"] = "1d8", ["halfOnSave"] = "false" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Empty(output.Mutations);
        Assert.Contains("Saved", output.Result.Narrative);
        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_Sleep_FailsLoudWithoutRolling()
    {
        // Sleep's 5d8 is an HP-affect pool, not HP damage — resolving it must fail before any
        // roll is consumed, not silently deal 5d8 damage.
        var rollService = new FakeRollService();

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 1 } };
        var target = new Character { Id = "char2", Name = "Guard", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Sleep",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["damageDice"] = "5d8" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.False(output.Result.Success);
        Assert.Equal("UnresolvableMechanic", output.Result.ErrorCode);
        Assert.Contains("HP-affect pool", output.Result.Narrative);
        Assert.Empty(output.Mutations);
        Assert.Empty(rollService.RecordedRequests);
    }

    [Fact]
    public async Task ResolveSpellSave_Sleep_FailsLoud()
    {
        var rollService = new FakeRollService();

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 1 } };
        var target = new Character { Id = "char2", Name = "Guard", SystemStats = new Dnd5eExtension { Dexterity = 10 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Sleep",
            Parameters = new Dictionary<string, string> { ["resolution"] = "save", ["dc"] = "15", ["damageDice"] = "5d8" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.False(output.Result.Success);
        Assert.Equal("UnresolvableMechanic", output.Result.ErrorCode);
        Assert.Empty(output.Mutations);
        Assert.Empty(rollService.RecordedRequests);
    }

    [Fact]
    public async Task ResolveSpellSave_SaveSuccessNone_WithoutDamageDice_NoWarning()
    {
        // No damage dice, no damage at stake: a pure control spell must not warn about halfOnSave.
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 16, HasCritical = false, HasComplication = false, Summary = "Rolled 16" });

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 3 } };
        var target = new Character { Id = "char2", Name = "Bandit", SystemStats = new Dnd5eExtension { Wisdom = 10 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Hold Person",
            Parameters = new Dictionary<string, string> { ["resolution"] = "save", ["save"] = "Wisdom", ["dc"] = "15" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        Assert.Empty(output.Mutations);
        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSavingThrow_SleepNamedAction_FailsLoud()
    {
        // Even on the single-save path, pool-shaped damage can never resolve as HP damage.
        var rollService = new FakeRollService();

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Wisdom = 10 } };

        var context = CreateContext(actor);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            ActionType = RulesetActionType.SavingThrow,
            ActionName = "Sleep",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Wisdom", ["damageDice"] = "5d8" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.False(output.Result.Success);
        Assert.Equal("UnresolvableMechanic", output.Result.ErrorCode);
        Assert.Empty(output.Mutations);
        Assert.Empty(rollService.RecordedRequests);
    }

    [Fact]
    public async Task ResolveAttack_MagicMissile_NoTargets_FailsInvalidTarget()
    {
        var rollService = new FakeRollService();

        var resolver = new Dnd5eRulesetResolver(rollService, spellDefinitionProvider: CreateSpellProvider());
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension { Level = 1 } };

        var context = CreateContext(actor);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = [],
            ActionType = RulesetActionType.Spell,
            ActionName = "Magic Missile",
            Parameters = new Dictionary<string, string> { ["resolution"] = "attack", ["damageDice"] = "3d4+3" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.False(output.Result.Success);
        Assert.Equal("InvalidTarget", output.Result.ErrorCode);
        Assert.Empty(output.Mutations);
        Assert.Empty(rollService.RecordedRequests);
    }
}
