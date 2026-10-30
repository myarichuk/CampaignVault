using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;
using CampaignVault.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Playtest: a level-6 mage's Magic Missile at a goblin resolved as "utility" and did no damage, and a stab outside
/// combat never started the fight. Spell routing now fails closed and follows the handbook entry; a hostile action
/// with no combat is refused at the take_turn boundary (<see cref="CombatStartGuard"/>).
/// </summary>
public class SpellRoutingFailClosedTests
{
    private static readonly SpellDefinitionProvider Spells =
        new(Path.Combine(Path.GetTempPath(), "cv_spell_routing_" + Guid.NewGuid()), typeof(SpellDefinitionProvider).Assembly);

    private static ChangeContext CreateContext(params Character[] characters) => new(
        sessionForTests: null,
        characters: characters.ToDictionary(c => c.Id),
        items: new Dictionary<string, Item>(),
        locations: new Dictionary<string, Location>(),
        factions: new Dictionary<string, Faction>(),
        quests: new Dictionary<string, Quest>(),
        logger: NullLogger.Instance,
        summary: [],
        dispatcher: new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
        campaignName: null);

    private static Character Mage() => new()
    {
        Id = "mage",
        Name = "Mage",
        SystemStats = new Dnd5eExtension { Level = 6, Intelligence = 18, SpellcastingAbility = "Intelligence", SpellSaveDc = 15 },
    };

    private static Character Goblin() => new()
    {
        Id = "goblin",
        Name = "Goblin",
        CurrentHp = 7,
        MaxHp = 7,
        SystemStats = new Dnd5eExtension { Dexterity = 14, ArmorClass = 15 },
    };

    private static (IRollService Rolls, List<RollRequest> Requests) Rolls(int result = 3)
    {
        var requests = new List<RollRequest>();
        var rolls = Substitute.For<IRollService>();
        rolls.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<RollRequest>();
            requests.Add(request);
            return Task.FromResult(new RollOutcome { Result = result, Summary = "rolled" });
        });
        return (rolls, requests);
    }

    private static RulesetAction Cast(string spell, string[] targets, params (string Key, string Value)[] parameters) => new()
    {
        CharacterId = "mage",
        TargetIds = [.. targets],
        ActionType = RulesetActionType.Spell,
        ActionCategory = ActionCategory.Spell,
        ActionName = spell,
        Parameters = parameters.ToDictionary(p => p.Key, p => p.Value),
    };

    [Fact]
    public async Task MagicMissile_WithoutResolution_AutoHitsFromSpellData()
    {
        var (rolls, requests) = Rolls();
        var resolver = new Dnd5eRulesetResolver(rolls, spellDefinitionProvider: Spells);

        var output = await resolver.ResolveAsync(CreateContext(Mage(), Goblin()), Cast("Magic Missile", ["goblin"]), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Equal(3, output.Mutations.OfType<HpChange>().Count(h => h.CharacterId == "goblin"));
        Assert.DoesNotContain(requests, r => r.Tag == "attack");
    }

    [Fact]
    public async Task MagicMissile_SlotLevel_SetsDartCount()
    {
        var (rolls, _) = Rolls();
        var resolver = new Dnd5eRulesetResolver(rolls, spellDefinitionProvider: Spells);

        var output = await resolver.ResolveAsync(CreateContext(Mage(), Goblin()),
            Cast("Magic Missile", ["goblin"], ("resolution", "attack"), ("slotLevel", "3"), ("damageDice", "1d4+1")),
            TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Equal(5, output.Mutations.OfType<HpChange>().Count(h => h.CharacterId == "goblin"));
    }

    [Fact]
    public async Task MagicMissile_ExplicitUtilityAtATarget_IsRejected()
    {
        var resolver = new Dnd5eRulesetResolver(Rolls().Rolls, spellDefinitionProvider: Spells);

        var output = await resolver.ResolveAsync(CreateContext(Mage(), Goblin()),
            Cast("Magic Missile", ["goblin"], ("resolution", "utility")), TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Contains("can't resolve as utility", output.Result.Narrative);
        Assert.Contains("never misses", output.Result.Narrative);
        Assert.Empty(output.Mutations);
    }

    [Theory]
    [InlineData("autohit")]
    [InlineData("damage")]
    public async Task UnknownResolution_FailsClosed(string resolution)
    {
        var resolver = new Dnd5eRulesetResolver(Rolls().Rolls, spellDefinitionProvider: Spells);

        var output = await resolver.ResolveAsync(CreateContext(Mage(), Goblin()),
            Cast("Magic Missile", ["goblin"], ("resolution", resolution)), TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Contains(SpellResolutionHelper.ValidResolutions, output.Result.Narrative);
        Assert.Empty(output.Mutations);
    }

    [Fact]
    public async Task FireBolt_WithoutDice_RollsTheCantripTierForTheCasterLevel()
    {
        var (rolls, requests) = Rolls(result: 20);
        var resolver = new Dnd5eRulesetResolver(rolls, spellDefinitionProvider: Spells);

        var output = await resolver.ResolveAsync(CreateContext(Mage(), Goblin()), Cast("Fire Bolt", ["goblin"]), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Contains(requests, r => r.Tag == "attack");
        Assert.Contains(requests, r => r.Tag == "damage" && r.Expression == "2d10");
    }

    [Fact]
    public async Task UntargetedDamageRider_StaysUtility()
    {
        // Divine Favor carries damage data but is a self-buff: with no target it must not turn into an attack.
        var resolver = new Dnd5eRulesetResolver(Rolls().Rolls, spellDefinitionProvider: Spells);

        var output = await resolver.ResolveAsync(CreateContext(Mage()), Cast("Divine Favor", []), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Empty(output.Mutations);
    }

    [Fact]
    public async Task AttackTypeSpellWithoutDamageData_GetsNoPhantomDamage()
    {
        // Ray of Enfeeblement has an attack type but no damage table: it must not become a 1d4 attack.
        var resolver = new Dnd5eRulesetResolver(Rolls().Rolls, spellDefinitionProvider: Spells);

        var output = await resolver.ResolveAsync(CreateContext(Mage(), Goblin()), Cast("Ray of Enfeeblement", ["goblin"]), TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Contains("no spell mechanics", output.Result.Narrative);
        Assert.Empty(output.Mutations.OfType<HpChange>());
    }

    [Fact]
    public async Task Pf2e_TargetedSpellWithoutMechanics_FailsWithFixHint()
    {
        var resolver = new Pf2eRulesetResolver(Rolls().Rolls);
        var caster = new Character { Id = "mage", Name = "Mage", SystemStats = new Pf2eExtension { Level = 3 } };
        var target = new Character { Id = "goblin", Name = "Goblin", SystemStats = new Pf2eExtension() };

        var bare = await resolver.ResolveAsync(CreateContext(caster, target), Cast("Force Barrage", ["goblin"]), TestContext.Current.CancellationToken);
        var utility = await resolver.ResolveAsync(CreateContext(caster, target),
            Cast("Force Barrage", ["goblin"], ("resolution", "utility")), TestContext.Current.CancellationToken);

        Assert.False(bare.Result.Success);
        Assert.Contains("resolution=attack", bare.Result.Narrative);
        Assert.True(utility.Result.Success);
    }

    // --- CombatStartGuard ---

    private static SpellDefinition? Lookup(RulesetAction action) =>
        Spells.TryGet(RulesetSystem.Dnd5e, action.ActionName.Trim().ToLowerInvariant().Replace(' ', '_'), out var spell) ? spell : null;

    private static RulesetAction Stab(params (string Key, string Value)[] parameters) => new()
    {
        CharacterId = "rogue",
        TargetIds = ["goblin"],
        ActionType = RulesetActionType.Attack,
        ActionName = "Dagger",
        Parameters = parameters.ToDictionary(p => p.Key, p => p.Value),
    };

    [Fact]
    public void Guard_AttackOutsideCombat_IsRefused()
    {
        var conflict = CombatStartGuard.FindConflict([Stab()], combatActive: false, Lookup);

        Assert.NotNull(conflict);
        Assert.Contains("[CombatNotStarted]", conflict);
        Assert.Contains("surprisedIds", conflict);
    }

    [Fact]
    public void Guard_AttackDuringCombat_Passes() =>
        Assert.Null(CombatStartGuard.FindConflict([Stab()], combatActive: true, Lookup));

    [Fact]
    public void Guard_OutsideCombatOptOut_Passes() =>
        Assert.Null(CombatStartGuard.FindConflict([Stab((CombatStartGuard.OutsideCombatParameter, "true"))], combatActive: false, Lookup));

    [Fact]
    public void Guard_DamageSpellAtSomeoneOutsideCombat_IsRefused()
    {
        Assert.NotNull(CombatStartGuard.FindConflict([Cast("Magic Missile", ["goblin"])], combatActive: false, Lookup));
        Assert.NotNull(CombatStartGuard.FindConflict([Cast("Fireball", ["goblin"])], combatActive: false, Lookup));
    }

    [Fact]
    public void Guard_NonHostileActionsOutsideCombat_Pass()
    {
        WorldChange[] changes =
        [
            Cast("Cure Wounds", ["goblin"], ("resolution", "heal"), ("healDice", "1d8")),
            Cast("Detect Magic", []),
            Cast("Magic Missile", ["mage"]),
            Cast("Charm Person", ["goblin"]),
            Cast("Suggestion", ["goblin"], ("resolution", "save"), ("save", "Wisdom")),
            new RulesetAction { CharacterId = "rogue", ActionType = RulesetActionType.SkillCheck, ActionName = "Stealth", Parameters = new() { ["dc"] = "12" } },
        ];

        Assert.Null(CombatStartGuard.FindConflict(changes, combatActive: false, Lookup));
    }

    [Fact]
    public void Guard_GrappleOutsideCombat_IsRefused()
    {
        var grapple = new RulesetAction
        {
            CharacterId = "rogue", TargetIds = ["goblin"], ActionType = RulesetActionType.ContestedCheck, ActionName = "Grapple",
        };

        Assert.NotNull(CombatStartGuard.FindConflict([grapple], combatActive: false, Lookup));
    }
}
