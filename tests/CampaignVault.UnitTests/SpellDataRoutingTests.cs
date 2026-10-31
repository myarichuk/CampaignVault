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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Spell routing without a <c>resolution</c> is only as good as the handbook data. These pin the overlay fixes
/// (scripts/spell_damage_overlay*.yaml) against the real embedded handbook: SRD spells whose text names a save or
/// spell attack the API omits, and pf2e's one Player Core auto-hit spell.
/// </summary>
public class SpellDataRoutingTests
{
    private static readonly SpellDefinitionProvider Spells =
        new(Path.Combine(Path.GetTempPath(), "cv_spell_data_routing_" + Guid.NewGuid()), typeof(SpellDefinitionProvider).Assembly);

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

    private static Character Druid() => new()
    {
        Id = "mage",
        Name = "Druid",
        SystemStats = new Dnd5eExtension { Level = 6, Wisdom = 18, SpellcastingAbility = "Wisdom", SpellSaveDc = 15 },
    };

    private static Character Goblin() => new()
    {
        Id = "goblin",
        Name = "Goblin",
        CurrentHp = 30,
        MaxHp = 30,
        SystemStats = new Dnd5eExtension { Dexterity = 14, ArmorClass = 15 },
    };

    private static (IRollService Rolls, List<RollRequest> Requests) Rolls(int result = 3)
    {
        var requests = new List<RollRequest>();
        var rolls = Substitute.For<IRollService>();
        rolls.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            requests.Add(call.Arg<RollRequest>());
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

    private static SpellDefinition Spell(string system, string slug)
    {
        Assert.True(Spells.TryGet(system, slug, out var spell), $"{system} handbook has no '{slug}'");
        return spell!;
    }

    [Theory]
    [InlineData("call_lightning")]
    [InlineData("flaming_sphere")]
    public void Dnd5e_DexSaveSpells_CarryTheirSave(string slug)
    {
        var spell = Spell(RulesetSystem.Dnd5e, slug);

        Assert.Equal("dex", spell.SaveType);
        Assert.Equal("half", spell.SaveSuccess);
    }

    [Theory]
    [InlineData("flame_blade", "melee")]
    [InlineData("scorching_ray", "ranged")]
    public void Dnd5e_SpellAttackSpells_CarryTheirAttackType(string slug, string attackType) =>
        Assert.Equal(attackType, Spell(RulesetSystem.Dnd5e, slug).AttackType);

    [Fact]
    public void Pf2e_ForceBarrage_NeedsNoAttackRoll() =>
        Assert.False(Spell(RulesetSystem.Pathfinder2e, "force_barrage").RequiresAttackRoll);

    [Fact]
    public async Task CallLightning_WithoutResolution_RollsADexSave()
    {
        var (rolls, requests) = Rolls();
        var resolver = new Dnd5eRulesetResolver(rolls, spellDefinitionProvider: Spells);

        var output = await resolver.ResolveAsync(CreateContext(Druid(), Goblin()), Cast("Call Lightning", ["goblin"]), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Contains(requests, r => r.Tag == "spell-save");
        Assert.DoesNotContain(requests, r => r.Tag == "attack");
        Assert.Contains("Dex", output.Result.Narrative, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Failed", output.Result.Narrative);
        Assert.Contains(output.Mutations.OfType<HpChange>(), h => h.CharacterId == "goblin" && h.Delta < 0);
    }

    [Fact]
    public async Task FlameBlade_WithoutResolution_IsAMeleeSpellAttack()
    {
        var (rolls, requests) = Rolls(result: 20);
        var resolver = new Dnd5eRulesetResolver(rolls, spellDefinitionProvider: Spells);

        var output = await resolver.ResolveAsync(CreateContext(Druid(), Goblin()), Cast("Flame Blade", ["goblin"]), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Contains(requests, r => r.Tag == "attack");
        Assert.Contains(requests, r => r.Tag == "damage" && r.Expression == "3d6");
    }

    [Fact]
    public async Task Pf2e_ForceBarrage_AutoHitsWithoutAnAttackRollOrCritical()
    {
        // A natural 20 on every die: had the attack been rolled it would crit and double the damage.
        var (rolls, requests) = Rolls(result: 20);
        var resolver = new Pf2eRulesetResolver(rolls, spellDefinitionProvider: Spells);
        var caster = new Character { Id = "mage", Name = "Mage", SystemStats = new Pf2eExtension { Level = 3 } };
        var target = new Character { Id = "goblin", Name = "Goblin", CurrentHp = 30, MaxHp = 30, SystemStats = new Pf2eExtension { ArmorClass = 40 } };

        var output = await resolver.ResolveAsync(CreateContext(caster, target),
            Cast("Force Barrage", ["goblin"], ("resolution", "attack"), ("damageDice", "1d4+1")), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.DoesNotContain(requests, r => r.Tag == "attack");
        Assert.Contains("(auto-hit)", output.Result.Narrative);
        var hit = Assert.Single(output.Mutations.OfType<HpChange>(), h => h.CharacterId == "goblin");
        Assert.False(hit.Critical);
        Assert.Equal(-20, hit.Delta);
    }
}
