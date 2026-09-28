using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Phase 2c: cast-time summon resolution — slot/count/creature parameters, both-ways
/// linking, and control-cap enforcement over the real embedded spell catalog.
/// </summary>
public class SummonResolutionTests
{
    private static SpellDefinitionProvider CreateSpellProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv_summon_res_spell_" + Guid.NewGuid());
        return new SpellDefinitionProvider(dir, typeof(SpellDefinitionProvider).Assembly);
    }

    private static CreatureDefinitionProvider CreateCreatureProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv_summon_res_creature_" + Guid.NewGuid());
        return new CreatureDefinitionProvider(dir, typeof(CreatureDefinitionProvider).Assembly);
    }

    private static Dnd5eRulesetResolver CreateResolver() =>
        new(Substitute.For<IRollService>(),
            spellDefinitionProvider: CreateSpellProvider(),
            creatureDefinitionProvider: CreateCreatureProvider());

    private static Character Caster(string id = "chars/necromancer") => new()
    {
        Id = id,
        Name = "Necra",
        CampaignName = "summon-test",
        CurrentLocationId = "locs/crypt",
        SystemStats = new Dnd5eExtension { Level = 5 },
    };

    private static ChangeContext CreateContext(params Character[] characters) =>
        ChangeContextTestHelper.Create(
            characters: characters.ToDictionary(c => c.Id),
            campaignName: "summon-test");

    private static RulesetAction CastAction(string casterId, string spellName, Dictionary<string, string>? parameters = null) => new()
    {
        CharacterId = casterId,
        ActionType = RulesetActionType.Spell,
        ActionName = spellName,
        Parameters = parameters ?? new Dictionary<string, string>(),
    };

    [Fact]
    public async Task AnimateDead_BaseSlot_CreatesOneLinkedSkeleton()
    {
        var resolver = CreateResolver();
        var caster = Caster();
        var context = CreateContext(caster);

        var output = await resolver.ResolveAsync(context, CastAction(caster.Id, "Animate Dead"), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        var create = Assert.IsType<CharacterCreate>(Assert.Single(output.Mutations.OfType<CharacterCreate>()));
        Assert.Equal("chars/necromancer-minion-1", create.CharacterId);
        Assert.Equal("Skeleton", create.Name);
        Assert.Equal(13, create.MaxHp);
        Assert.Equal(caster.Id, create.ControlledById);
        Assert.Equal("locs/crypt", create.CurrentLocationId);
        Assert.True(create.KeepAlive);
        var binding = Assert.IsType<MinionBinding>(create.MinionBinding);
        Assert.Equal(caster.Id, binding.ControllerId);
        Assert.Equal("animate_dead", binding.SpellName);
        Assert.Equal(SummonDisposition.Loyal, binding.Disposition);
        Assert.False(binding.ConcentrationBound);
        Assert.Equal(1, binding.DurationDays);
        Assert.Equal(1, binding.ExpiresAtDay);
        Assert.Null(binding.ExpiresAtRound);
        var casterUpdate = Assert.IsType<CharacterUpdate>(Assert.Single(output.Mutations.OfType<CharacterUpdate>()));
        Assert.Equal(caster.Id, casterUpdate.CharacterId);
        Assert.Equal([create.CharacterId], casterUpdate.ControlsMinionIdsAdd);
        Assert.Contains("controlled by Necra (loyal)", output.Result.Narrative, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnimateDead_Slot4_CreatesThreeZombies()
    {
        var resolver = CreateResolver();
        var caster = Caster();
        var context = CreateContext(caster);

        var output = await resolver.ResolveAsync(context, CastAction(caster.Id, "Animate Dead", new Dictionary<string, string>
        {
            ["slotLevel"] = "4",
            ["creature"] = "zombie",
        }), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        var creates = output.Mutations.OfType<CharacterCreate>().ToList();
        Assert.Equal(3, creates.Count);
        Assert.All(creates, c => Assert.Equal(22, c.MaxHp));
        Assert.Equal(["Zombie 1", "Zombie 2", "Zombie 3"], creates.Select(c => c.Name).ToList());
    }

    [Fact]
    public async Task AnimateDead_FewerCountThanTable_Succeeds()
    {
        var resolver = CreateResolver();
        var caster = Caster();
        var context = CreateContext(caster);

        var output = await resolver.ResolveAsync(context, CastAction(caster.Id, "Animate Dead", new Dictionary<string, string>
        {
            ["slotLevel"] = "4",
            ["count"] = "2",
        }), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Equal(2, output.Mutations.OfType<CharacterCreate>().Count());
    }

    [Fact]
    public async Task AnimateDead_CountAboveTable_Fails()
    {
        var resolver = CreateResolver();
        var caster = Caster();
        var context = CreateContext(caster);

        var output = await resolver.ResolveAsync(context, CastAction(caster.Id, "Animate Dead", new Dictionary<string, string>
        {
            ["slotLevel"] = "3",
            ["count"] = "2",
        }), TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Equal("InvalidParameter", output.Result.ErrorCode);
        Assert.Empty(output.Mutations);
    }

    [Fact]
    public async Task AnimateDead_UnknownCreature_Fails()
    {
        var resolver = CreateResolver();
        var caster = Caster();
        var context = CreateContext(caster);

        var output = await resolver.ResolveAsync(context, CastAction(caster.Id, "Animate Dead", new Dictionary<string, string>
        {
            ["creature"] = "dragon",
        }), TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Equal("InvalidParameter", output.Result.ErrorCode);
    }

    [Fact]
    public async Task AnimateDead_SlotBelowSpellLevel_Fails()
    {
        var resolver = CreateResolver();
        var caster = Caster();
        var context = CreateContext(caster);

        var output = await resolver.ResolveAsync(context, CastAction(caster.Id, "Animate Dead", new Dictionary<string, string>
        {
            ["slotLevel"] = "2",
        }), TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Equal("InvalidParameter", output.Result.ErrorCode);
    }

    [Fact]
    public async Task AnimateDead_BadSlotLevel_Fails()
    {
        var resolver = CreateResolver();
        var caster = Caster();
        var context = CreateContext(caster);

        var output = await resolver.ResolveAsync(context, CastAction(caster.Id, "Animate Dead", new Dictionary<string, string>
        {
            ["slotLevel"] = "eleven",
        }), TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Equal("InvalidParameter", output.Result.ErrorCode);
    }

    [Fact]
    public async Task Summon_MaxHpOverride_UsesExplicitStatistics()
    {
        var resolver = CreateResolver();
        var caster = Caster();
        var context = CreateContext(caster);

        var output = await resolver.ResolveAsync(context, CastAction(caster.Id, "Animate Dead", new Dictionary<string, string>
        {
            ["creature"] = "zombie",
            ["maxHp"] = "30",
        }), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        var create = Assert.IsType<CharacterCreate>(Assert.Single(output.Mutations.OfType<CharacterCreate>()));
        Assert.Equal(30, create.MaxHp);
        Assert.Equal(30, create.CurrentHp);
    }

    [Fact]
    public async Task Summon_WithoutAnyStatistics_FailsWithGuidance()
    {
        var resolver = new SeedlessSummonResolver(Substitute.For<IRollService>());
        var caster = Caster();
        var context = CreateContext(caster);

        var output = await resolver.ResolveAsync(context, CastAction(caster.Id, "Test Summon"), TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Equal("MissingStatistics", output.Result.ErrorCode);
        Assert.Contains("maxHp", output.Result.Narrative);
    }

    [Fact]
    public async Task Summon_ExplicitResolutionMode_SkipsSummonPath()
    {
        var resolver = CreateResolver();
        var caster = Caster();
        var context = CreateContext(caster);

        var output = await resolver.ResolveAsync(context, CastAction(caster.Id, "Animate Dead", new Dictionary<string, string>
        {
            ["resolution"] = "utility",
        }), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success);
        Assert.Empty(output.Mutations);
    }

    [Fact]
    public async Task Summon_ControlCap_ReleasesOldestFirst()
    {
        var resolver = new CappedSummonResolver(Substitute.For<IRollService>());
        var caster = Caster();
        var oldMinion = new Character
        {
            Id = "chars/old",
            Name = "Old",
            CampaignName = "summon-test",
            ControlledById = caster.Id,
            MinionBinding = new MinionBinding { ControllerId = caster.Id },
            LastUpdated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var newMinion = new Character
        {
            Id = "chars/new",
            Name = "New",
            CampaignName = "summon-test",
            ControlledById = caster.Id,
            MinionBinding = new MinionBinding { ControllerId = caster.Id },
            LastUpdated = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        caster.ControlsMinionIds.Add(oldMinion.Id);
        caster.ControlsMinionIds.Add(newMinion.Id);
        var context = CreateContext(caster, oldMinion, newMinion);

        var output = await resolver.ResolveAsync(context, CastAction(caster.Id, "Test Summon"), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        var cleared = output.Mutations.OfType<CharacterUpdate>().Where(u => u.ClearMinionLink == true).ToList();
        Assert.Equal([oldMinion.Id], cleared.Select(u => u.CharacterId).ToList());
        var casterUpdate = output.Mutations.OfType<CharacterUpdate>().Single(u => u.CharacterId == caster.Id);
        Assert.Single(casterUpdate.ControlsMinionIdsAdd!);
        Assert.Equal([oldMinion.Id], casterUpdate.ControlsMinionIdsRemove);
        Assert.Contains("released Old (oldest first)", output.Result.Narrative);
    }

    [Fact]
    public async Task Reassert_KeepsNamedMinions_AndLapsesTheRest()
    {
        var resolver = new ReassertSummonResolver(Substitute.For<IRollService>());
        var caster = Caster();
        var kept1 = RetainedMinion("chars/kept-1", "Kept One", lapsed: true);
        var kept2 = RetainedMinion("chars/kept-2", "Kept Two", lapsed: true);
        var dropped = RetainedMinion("chars/dropped", "Dropped", lapsed: false);
        caster.ControlsMinionIds.Add(kept1.Id);
        caster.ControlsMinionIds.Add(kept2.Id);
        caster.ControlsMinionIds.Add(dropped.Id);
        var context = CreateContext(caster, kept1, kept2, dropped);

        var action = CastAction(caster.Id, "Test Reassert", new Dictionary<string, string>
        {
            ["slotLevel"] = "3",
            ["reassert"] = "true",
        });
        action.TargetIds.Add(kept1.Id);
        action.TargetIds.Add(kept2.Id);

        var output = await resolver.ResolveAsync(context, action, TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Empty(output.Mutations.OfType<CharacterCreate>());
        var relinks = output.Mutations.OfType<CharacterUpdate>().Where(u => u.ControlledById == caster.Id).ToList();
        Assert.Equal([kept1.Id, kept2.Id], relinks.Select(u => u.CharacterId).OrderBy(id => id).ToList());
        var clear = Assert.Single(output.Mutations.OfType<CharacterUpdate>(), u => u.ClearMinionLink == true);
        Assert.Equal(dropped.Id, clear.CharacterId);
        Assert.Contains("reasserts control over Kept One, Kept Two", output.Result.Narrative);
    }

    [Fact]
    public async Task Reassert_MoreTargetsThanKeep_Fails()
    {
        var resolver = new ReassertSummonResolver(Substitute.For<IRollService>());
        var caster = Caster();
        var context = CreateContext(caster);

        var action = CastAction(caster.Id, "Test Reassert", new Dictionary<string, string>
        {
            ["slotLevel"] = "3",
            ["reassert"] = "true",
        });
        action.TargetIds.Add("chars/a");
        action.TargetIds.Add("chars/b");
        action.TargetIds.Add("chars/c");

        var output = await resolver.ResolveAsync(context, action, TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Equal("InvalidParameter", output.Result.ErrorCode);
        Assert.Empty(output.Mutations);
    }

    [Fact]
    public async Task Reassert_WithoutTargets_Fails()
    {
        var resolver = new ReassertSummonResolver(Substitute.For<IRollService>());
        var caster = Caster();
        var context = CreateContext(caster);

        var output = await resolver.ResolveAsync(context, CastAction(caster.Id, "Test Reassert", new Dictionary<string, string>
        {
            ["reassert"] = "true",
        }), TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Equal("InvalidParameter", output.Result.ErrorCode);
    }

    [Fact]
    public async Task Reassert_ForeignMinion_Fails()
    {
        var resolver = new ReassertSummonResolver(Substitute.For<IRollService>());
        var caster = Caster();
        var stranger = new Character
        {
            Id = "chars/stranger",
            Name = "Stranger",
            CampaignName = "summon-test",
            SystemStats = new Dnd5eExtension(),
        };
        var context = CreateContext(caster, stranger);

        var action = CastAction(caster.Id, "Test Reassert", new Dictionary<string, string>
        {
            ["reassert"] = "true",
        });
        action.TargetIds.Add(stranger.Id);

        var output = await resolver.ResolveAsync(context, action, TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Equal("InvalidTarget", output.Result.ErrorCode);
    }

    [Fact]
    public async Task Reassert_WithoutRetainTable_Fails()
    {
        var resolver = new CappedSummonResolver(Substitute.For<IRollService>());
        var caster = Caster();
        var context = CreateContext(caster);

        var output = await resolver.ResolveAsync(context, CastAction(caster.Id, "Test Summon", new Dictionary<string, string>
        {
            ["reassert"] = "true",
        }), TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Equal("InvalidParameter", output.Result.ErrorCode);
    }

    private static Character RetainedMinion(string id, string name, bool lapsed) => new()
    {
        Id = id,
        Name = name,
        CampaignName = "summon-test",
        MaxHp = 13,
        CurrentHp = 13,
        ControlledById = lapsed ? null : "chars/necromancer",
        MinionBinding = new MinionBinding
        {
            ControllerId = "chars/necromancer",
            SpellName = "test_reassert",
            Disposition = SummonDisposition.Loyal,
            DurationDays = 1,
            ExpiresAtDay = 1,
            ControlLapsed = lapsed,
        },
        SystemStats = new Dnd5eExtension(),
    };

    private sealed class ReassertSummonResolver(IRollService roll)
        : Dnd5eRulesetResolver(roll)
    {
        private static readonly SpellDefinition TestSpell = new()
        {
            Name = "test_reassert",
            Level = 3,
            Concentration = false,
            Summon = new SummonEffect
            {
                Creatures = ["skeleton"],
                InlineSeed = new SummonStatSeed { Hp = 13 },
                CountAtSlotLevel = new Dictionary<int, int> { [3] = 1 },
                RetainCountAtSlotLevel = new Dictionary<int, int> { [3] = 2 },
                DurationDays = 1,
            },
        };

        protected override SpellDefinition? LookupSummonSpell(RulesetAction action) => TestSpell;
    }

    [Fact]
    public async Task Pf2eSummonUndead_CreatesSkeletonGuard()
    {
        var resolver = new Pf2eRulesetResolver(
            Substitute.For<IRollService>(),
            spellDefinitionProvider: CreateSpellProvider(),
            creatureDefinitionProvider: CreateCreatureProvider());
        var caster = Caster();
        caster.SystemStats = new Pf2eExtension();
        var context = CreateContext(caster);

        var output = await resolver.ResolveAsync(context, CastAction(caster.Id, "Summon Undead"), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        var create = Assert.IsType<CharacterCreate>(Assert.Single(output.Mutations.OfType<CharacterCreate>()));
        Assert.Equal("Skeleton Guard", create.Name);
        Assert.Equal(11, create.MaxHp);
        var binding = Assert.IsType<MinionBinding>(create.MinionBinding);
        Assert.Equal("summon_undead", binding.SpellName);
        Assert.Equal(10, binding.DurationRounds);
        Assert.Null(binding.ExpiresAtRound);
        Assert.True(binding.ConcentrationBound);
    }

    private sealed class SeedlessSummonResolver(IRollService roll)
        : Dnd5eRulesetResolver(roll)
    {
        private static readonly SpellDefinition TestSpell = new()
        {
            Name = "test_summon",
            Level = 1,
            Concentration = false,
            Summon = new SummonEffect
            {
                CountAtSlotLevel = new Dictionary<int, int> { [1] = 1 },
            },
        };

        protected override SpellDefinition? LookupSummonSpell(RulesetAction action) => TestSpell;
    }

    private sealed class CappedSummonResolver(IRollService roll)
        : Dnd5eRulesetResolver(roll)
    {
        private static readonly SpellDefinition TestSpell = new()
        {
            Name = "test_summon",
            Level = 1,
            Concentration = false,
            Summon = new SummonEffect
            {
                InlineSeed = new SummonStatSeed { Hp = 10 },
                CountAtSlotLevel = new Dictionary<int, int> { [1] = 1 },
                ControlCap = new SummonControlCap { MaxCreatures = 2 },
            },
        };

        protected override SpellDefinition? LookupSummonSpell(RulesetAction action) => TestSpell;
    }
}
