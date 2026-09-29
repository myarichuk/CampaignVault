using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>A homebrew feat with declared effects reaches the roll through the real ruleset_action handler.</summary>
[Collection("RavenDB")]
public class FeatEffectsHandlerTests(RavenDBFixture fixture) : IClassFixture<RavenDBFixture>
{
    private static readonly Assembly Assembly = typeof(SpellDefinitionProvider).Assembly;
    private readonly CampaignDocumentKeys _keys = new();
    private readonly string _campaign = "feat-effects-" + Guid.NewGuid().ToString("N")[..8];

    private ChangeContext Context(Raven.Client.Documents.Session.IAsyncDocumentSession session, params Character[] characters) => new(
        session,
        characters.ToDictionary(c => c.Id),
        new Dictionary<string, Item>(),
        new Dictionary<string, Location>(),
        new Dictionary<string, Faction>(),
        new Dictionary<string, Quest>(),
        NullLogger.Instance,
        [],
        new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
        null,
        null,
        _campaign);

    private async Task<(ChangeHandlerResult Result, FakeRollService Rolls)> AttackAsync(
        CustomFeat? homebrew, Dictionary<string, string>? extra = null, Action<ChangeContext>? configure = null)
    {
        using var session = fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new CampaignConfig { Id = _keys.Config(_campaign), ActiveSystem = RulesetSystem.Dnd5e }, TestContext.Current.CancellationToken);
        if (homebrew is not null)
        {
            homebrew.CampaignName = _campaign;
            await session.StoreAsync(homebrew, TestContext.Current.CancellationToken);
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        await session.Query<CustomFeat, CustomFeat_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
            .ToListAsync(TestContext.Current.CancellationToken);

        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 15, Summary = "15" });
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 5, Summary = "5" });
        var handler = new RulesetActionHandler(
            new RulesetModuleSelector([new Dnd5eRulesetResolver(rolls)]),
            _keys,
            new SpellDefinitionProvider(Path.Combine(Path.GetTempPath(), "cv_fe_" + Guid.NewGuid()), Assembly),
            new FeatDefinitionProvider(Path.Combine(Path.GetTempPath(), "cv_fe_" + Guid.NewGuid()), Assembly));

        var hank = new Character
        {
            Id = "chars/hank",
            Name = "Hank",
            SystemStats = new Dnd5eExtension { Level = 3, Dexterity = 14, Feats = ["Long Shot"], Attributes = { ["proficiencyBonus"] = 2 } },
        };
        var pell = new Character { Id = "chars/pell", Name = "Pell", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };
        var parameters = new Dictionary<string, string> { ["damageDice"] = "1d8", ["weaponTags"] = "ranged" };
        foreach (var (k, v) in extra ?? [])
        {
            parameters[k] = v;
        }

        var action = new RulesetAction
        {
            CharacterId = hank.Id,
            TargetIds = [pell.Id],
            ActionType = RulesetActionType.Attack,
            ActionName = "Shortbow",
            Parameters = parameters,
        };
        var context = Context(session, hank, pell);
        configure?.Invoke(context);
        var result = await handler.ApplyAsync(action, context, TestContext.Current.CancellationToken);
        return (result, rolls);
    }

    private static CustomFeat LongShot(Action<CustomFeat>? tweak = null)
    {
        var feat = new CustomFeat
        {
            Id = "feats/long_shot_" + Guid.NewGuid().ToString("N")[..6],
            Name = "Long Shot",
            System = RulesetSystem.Dnd5e,
            Effects =
            [
                new FeatEffect { Kind = FeatEffectKinds.AttackBonus, Value = 2, Weapon = ["ranged"] },
                new FeatEffect { Kind = FeatEffectKinds.DamageBonus, Value = 3, Weapon = ["ranged"], Assert = ["highGround"], When = "the archer shoots from higher ground" },
            ],
        };
        tweak?.Invoke(feat);
        return feat;
    }

    [Fact]
    public async Task HomebrewFeatEffect_ReachesTheAttackRoll()
    {
        var (result, rolls) = await AttackAsync(LongShot());

        Assert.True(result.Success, result.Message);
        Assert.Equal(6, rolls.RecordedRequests[0].Bonus);   // DEX +2, prof +2, Long Shot +2
        Assert.Contains("Long Shot +2", result.Message);
        Assert.Contains("needs assert=highGround", result.Message);
    }

    [Fact]
    public async Task HomebrewFeatEffect_AssertedFlag_UnlocksTheDamage()
    {
        var (_, rolls) = await AttackAsync(LongShot(), new Dictionary<string, string> { ["assert"] = "highGround" });

        Assert.Equal(5, rolls.RecordedRequests[1].Bonus);   // DEX +2 + Long Shot +3
    }

    [Fact]
    public async Task FeatNotRecorded_ContributesNothing()
    {
        var (_, rolls) = await AttackAsync(LongShot(f => f.Name = "Some Other Feat"));

        Assert.Equal(4, rolls.RecordedRequests[0].Bonus);
    }

    [Fact]
    public async Task FeatGatedOnUnloadedPlugin_IsInert()
    {
        var (_, rolls) = await AttackAsync(LongShot(f => f.Requires = new FeatRequirement { Plugin = "no-such-plugin-" + Guid.NewGuid() }));

        Assert.Equal(4, rolls.RecordedRequests[0].Bonus);
    }

    [Fact]
    public async Task FeatGatedOnAMode_IsInertUntilThatModeRuns()
    {
        var mode = "astral-" + Guid.NewGuid().ToString("N")[..6];
        var gated = () => LongShot(f => f.Requires = new FeatRequirement { Mode = mode });

        var (_, off) = await AttackAsync(gated());
        Assert.Equal(4, off.RecordedRequests[0].Bonus);

        var (_, on) = await AttackAsync(gated(), configure: ctx =>
            ctx.EnterMode(new ModeEncounter { Id = "m1", ModeId = mode, LocationId = "locations/x", IsActive = true }));
        Assert.Equal(6, on.RecordedRequests[0].Bonus);
    }
}
