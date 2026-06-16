using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Pressure;

using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests.SimulationHarness;

/// <summary>
/// Deterministic harness scenarios mirroring the June 11 Grok combat playtest:
/// bootstrap HP + systemStats per ruleset, pressure nag, combat loop via commit tools.
/// </summary>
[Collection("RavenDB")]
public class SystemStatsBootstrapHarnessTests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;
    private readonly RavenDBFixture _fixture;

    public SystemStatsBootstrapHarnessTests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
        _fixture = fixture;
        new Character_Search().Execute(_store);
    }

    [Fact]
    public async Task Scenario_Dnd5e_PressureThenBootstrapThenCombat()
    {
        var campaign = "harness-dnd5e-" + Guid.NewGuid().ToString("N")[..8];
        var tools = CreateHarnessTools(campaign);
        await tools.SelectCampaign(campaign);
        await tools.SetActiveSystem(RulesetSystem.Dnd5e, null, campaign);

        var locId = $"locations/forest-road-{Guid.NewGuid():N}";
        var pcId = "chars/elara-voss";
        var enemyId = "chars/goblin-scout-1";

        await RunPressureThenPatchAsync(tools, campaign, locId, pcId, enemyId, RulesetSystem.Dnd5e,
            bootstrapChanges: BuildDnd5eBootstrapCommit(locId, pcId, enemyId));

        await RunCombatLoopAsync(tools, campaign, locId, pcId, enemyId,
            attack: new RulesetAction
            {
                ActorId = pcId,
                TargetIds = [enemyId],
                ActionType = RulesetActionType.Attack,
                ActionName = "Longsword",
                ActionCategory = ActionCategory.Melee,
                Parameters = new Dictionary<string, string>
                {
                    { "bonus", "5" },
                    { "damageDice", "1d8" },
                    { "damageBonus", "3" }
                }
            },
            assertStoredStats: c =>
            {
                var stats = Assert.IsType<Dnd5eExtension>(c.SystemStats);
                Assert.Equal(16, stats.ArmorClass);
                Assert.Equal(5, stats.SkillModifiers["Athletics"]);
            });
    }

    [Fact]
    public async Task Scenario_Pf2e_PressureThenBootstrapThenCombat()
    {
        var campaign = "harness-pf2e-" + Guid.NewGuid().ToString("N")[..8];
        var tools = CreateHarnessTools(campaign);
        await tools.SelectCampaign(campaign);
        await tools.SetActiveSystem(RulesetSystem.Pathfinder2e, null, campaign);

        var locId = $"locations/canopy-trail-{Guid.NewGuid():N}";
        var pcId = "chars/kyra-sunblade";
        var enemyId = "chars/skeletal-champion-1";

        await RunPressureThenPatchAsync(tools, campaign, locId, pcId, enemyId, RulesetSystem.Pathfinder2e,
            bootstrapChanges: BuildPf2eBootstrapCommit(locId, pcId, enemyId));

        await RunCombatLoopAsync(tools, campaign, locId, pcId, enemyId,
            attack: new RulesetAction
            {
                ActorId = pcId,
                TargetIds = [enemyId],
                ActionType = RulesetActionType.Attack,
                ActionName = "Longsword Strike",
                ActionCategory = ActionCategory.Melee,
                Parameters = new Dictionary<string, string>
                {
                    { "bonus", "9" },
                    { "damageDice", "1d8" },
                    { "damageBonus", "4" }
                }
            },
            assertStoredStats: c =>
            {
                var stats = Assert.IsType<Pf2eExtension>(c.SystemStats);
                Assert.Equal(19, stats.ArmorClass);
                Assert.Equal(8, stats.SkillModifiers["Perception"]);
            });
    }

    [Fact]
    public async Task Scenario_Fallout2d20_PressureThenBootstrapThenCombat()
    {
        var campaign = "harness-fallout-" + Guid.NewGuid().ToString("N")[..8];
        var tools = CreateHarnessTools(campaign);
        await tools.SelectCampaign(campaign);
        await tools.SetActiveSystem(RulesetSystem.Fallout2d20, null, campaign);

        var locId = $"locations/highway-ruins-{Guid.NewGuid():N}";
        var pcId = "chars/vault-dweller-1";
        var enemyId = "chars/raider-scout-1";

        await RunPressureThenPatchAsync(tools, campaign, locId, pcId, enemyId, RulesetSystem.Fallout2d20,
            bootstrapChanges: BuildFalloutBootstrapCommit(locId, pcId, enemyId));

        await RunCombatLoopAsync(tools, campaign, locId, pcId, enemyId,
            attack: new RulesetAction
            {
                ActorId = pcId,
                TargetIds = [enemyId],
                ActionType = RulesetActionType.Attack,
                ActionName = "10mm Pistol",
                ActionCategory = ActionCategory.Ranged,
                DamageType = "Physical",
                Parameters = new Dictionary<string, string>
                {
                    { "attribute", "Agility" },
                    { "skill", "SmallGuns" },
                    { "pool", "2" },
                    { "damageDice", "3" },
                    { "difficulty", "1" }
                }
            },
            assertStoredStats: c =>
            {
                var stats = Assert.IsType<Fallout2d20Extension>(c.SystemStats);
                Assert.Equal(7, stats.Agility);
                Assert.Equal(2, stats.Skills["SmallGuns"]);
                Assert.Contains("SmallGuns", stats.TagSkills);
            });
    }

    private async Task RunPressureThenPatchAsync(
        CampaignTools tools,
        string campaign,
        string locId,
        string pcId,
        string enemyId,
        RulesetSystem system,
        WorldChange[] bootstrapChanges)
    {
        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Location
            {
                Id = locId,
                Name = "Encounter Site",
                Type = LocationType.Wilderness,
                CampaignName = campaign
            });
            await session.SaveChangesAsync();
        }

        var underbootstrapped = new WorldChange[]
        {
            new CharacterCreate
            {
                CharacterId = pcId,
                Name = "Underbootstrapped PC",
                KeepAlive = true,
                MaxHp = 18,
                CurrentHp = 18,
                CurrentLocationId = locId,
                CurrentActivity = "Ready for combat"
            },
            new CharacterCreate
            {
                CharacterId = enemyId,
                Name = "Underbootstrapped Enemy",
                MaxHp = 10,
                CurrentHp = 10,
                CurrentLocationId = locId,
                CurrentActivity = "Threatening"
            }
        };

        var seedResult = await tools.Commit(underbootstrapped, "Spawn combatants without systemStats", campaign);
        Assert.True(seedResult.Success, seedResult.Summary);

        await WaitForCombatantsIndexedAsync(campaign, pcId, enemyId);

        var nagResult = await tools.GetWorldState(locId, campaign);
        Assert.True(nagResult.Success);
        var nagPressure = string.Join("\n", nagResult.Data?.WorldPressure ?? []);
        Assert.Contains("uninitialized systemStats", nagPressure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(pcId, nagPressure);

        var bootstrapResult = await tools.Commit(bootstrapChanges, "Full combat bootstrap", campaign);
        Assert.True(bootstrapResult.Success, bootstrapResult.Summary);

        await WaitForCombatantsIndexedAsync(campaign, pcId, enemyId);

        var clearResult = await tools.GetWorldState(locId, campaign);
        Assert.True(clearResult.Success);
        var clearPressure = string.Join("\n", clearResult.Data?.WorldPressure ?? []);
        Assert.DoesNotContain("uninitialized systemStats", clearPressure, StringComparison.OrdinalIgnoreCase);

        using (var session = _store.OpenAsyncSession())
        {
            var pc = await session.LoadAsync<Character>(pcId);
            Assert.NotNull(pc);
            Assert.True(SystemStatsCompleteness.IsComplete(pc, system));
        }
    }

    private async Task RunCombatLoopAsync(
        CampaignTools tools,
        string campaign,
        string locId,
        string pcId,
        string enemyId,
        RulesetAction attack,
        Action<Character> assertStoredStats)
    {
        var sceneBefore = await tools.GetScene(locId, partyPresent: true, campaignName: campaign);
        Assert.True(sceneBefore.Success);
        Assert.Contains(sceneBefore.Data?.PresentNPCs ?? [], n => n.Id == pcId);
        Assert.Contains(sceneBefore.Data?.PresentNPCs ?? [], n => n.Id == enemyId);

        var start = await tools.StartCombat(locId, [pcId, enemyId], campaign);
        Assert.True(start.Success, start.Summary);
        Assert.True(start.Data!.IsActive);

        int hpBefore;
        using (var session = _store.OpenAsyncSession())
        {
            var enemyBefore = await session.LoadAsync<Character>(enemyId);
            Assert.NotNull(enemyBefore);
            hpBefore = enemyBefore!.CurrentHp;
        }

        var attackResult = await tools.Commit([attack], "Harness attack", campaign);
        Assert.True(attackResult.Success, attackResult.Summary);
        var handlerMessages = string.Join("\n", attackResult.Data?.Summary ?? []);
        Assert.Contains("Hit", handlerMessages, StringComparison.OrdinalIgnoreCase);

        using (var verifySession = _store.OpenAsyncSession())
        {
            var enemyAfter = await verifySession.LoadAsync<Character>(enemyId);
            Assert.NotNull(enemyAfter);
            Assert.True(enemyAfter!.CurrentHp < hpBefore, $"Attack should reduce enemy HP (before={hpBefore}, after={enemyAfter.CurrentHp}). Summary: {attackResult.Summary}");

            var pc = await verifySession.LoadAsync<Character>(pcId);
            Assert.NotNull(pc);
            assertStoredStats(pc!);
        }

        var turn = await tools.NextTurn(null, campaign);
        Assert.True(turn.Success, turn.Summary);

        var end = await tools.EndCombat(campaign);
        Assert.True(end.Success, end.Summary);
    }

    private static WorldChange[] BuildDnd5eBootstrapCommit(string locId, string pcId, string enemyId) =>
    [
        new SystemStatsChange
        {
            CharacterId = pcId,
            SystemStats = new Dnd5eExtension
            {
                ArmorClass = 16,
                Strength = 16,
                Dexterity = 14,
                Constitution = 14,
                SkillModifiers = new Dictionary<string, int> { { "Athletics", 5 }, { "Perception", 2 } },
                SavingThrowModifiers = new Dictionary<string, int> { { "Constitution", 4 } }
            }
        },
        new SystemStatsChange
        {
            CharacterId = enemyId,
            SystemStats = new Dnd5eExtension
            {
                ArmorClass = 15,
                Dexterity = 14,
                Strength = 8,
                SkillModifiers = new Dictionary<string, int> { { "Stealth", 6 } }
            }
        },
        new ActivityChange { CharacterId = pcId, NewLocationId = locId, NewActivity = "Advancing on the enemy" },
        new ActivityChange { CharacterId = enemyId, NewLocationId = locId, NewActivity = "Ambush!" }
    ];

    private static WorldChange[] BuildPf2eBootstrapCommit(string locId, string pcId, string enemyId) =>
    [
        new SystemStatsChange
        {
            CharacterId = pcId,
            SystemStats = new Pf2eExtension
            {
                ArmorClass = 19,
                StrengthMod = 4,
                DexterityMod = 2,
                ConstitutionMod = 2,
                SkillModifiers = new Dictionary<string, int> { { "Perception", 8 }, { "Athletics", 9 } },
                SavingThrowModifiers = new Dictionary<string, int> { { "Fortitude", 9 }, { "Reflex", 7 }, { "Will", 6 } }
            }
        },
        new SystemStatsChange
        {
            CharacterId = enemyId,
            SystemStats = new Pf2eExtension
            {
                ArmorClass = 17,
                StrengthMod = 3,
                DexterityMod = 0,
                SkillModifiers = new Dictionary<string, int> { { "Athletics", 7 } },
                SavingThrowModifiers = new Dictionary<string, int> { { "Fortitude", 8 }, { "Reflex", 4 }, { "Will", 6 } }
            }
        },
        new ActivityChange { CharacterId = pcId, NewLocationId = locId, NewActivity = "Engaging the undead" },
        new ActivityChange { CharacterId = enemyId, NewLocationId = locId, NewActivity = "Rattling its blade" }
    ];

    private static WorldChange[] BuildFalloutBootstrapCommit(string locId, string pcId, string enemyId) =>
    [
        new SystemStatsChange
        {
            CharacterId = pcId,
            SystemStats = new Fallout2d20Extension
            {
                Agility = 7,
                Perception = 6,
                Endurance = 6,
                Defense = 1,
                Skills = new Dictionary<string, int> { { "SmallGuns", 2 }, { "Sneak", 1 } },
                TagSkills = ["SmallGuns"]
            }
        },
        new SystemStatsChange
        {
            CharacterId = enemyId,
            SystemStats = new Fallout2d20Extension
            {
                Agility = 6,
                Perception = 5,
                Endurance = 5,
                Defense = 1,
                Skills = new Dictionary<string, int> { { "SmallGuns", 1 } },
                DamageResistance = new Dictionary<string, int> { { "Physical", 0 } }
            }
        },
        new ActivityChange { CharacterId = pcId, NewLocationId = locId, NewActivity = "Taking cover behind rubble" },
        new ActivityChange { CharacterId = enemyId, NewLocationId = locId, NewActivity = "Popping up to shoot" }
    ];

    private async Task WaitForCombatantsIndexedAsync(string campaign, params string[] characterIds)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var session = _store.OpenAsyncSession();
            var found = await PressureQueryHelper.QueryCombatantCharactersAsync(session, campaign, 100);
            if (characterIds.All(id => found.Any(c => c.Id == id)))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for combatants to index: {string.Join(", ", characterIds)}");
    }

    private CampaignTools CreateHarnessTools(string campaign)
    {
        var context = new CurrentCampaignContext();
        context.SetCurrent(campaign);
        return TestCampaignToolsFactory.Create(_fixture, context, rollService: new HarnessPredictableRollService());
    }

    private static IWorldChangeHandler[] BuildProductionHandlers(
        IRulesetModuleSelector selector,
        CampaignDocumentKeys keys,
        ICurrentCampaignContext context) =>
    [
        new HpChangeHandler(),
        new ItemTransferHandler(),
        new StatusChangeHandler(),
        new EventOccurredHandler(),
        new RumorEvolvesHandler(),
        new RelationshipChangeHandler(),
        new EngagementRelationChangeHandler(),
        new SpatialPositionChangeHandler(),
        new NeedChangeHandler(),
        new AttributeChangeHandler(),
        new MoodChangeHandler(),
        new ActivityChangeHandler(),
        new LocationCreateHandler(),
        new LocationUpdateHandler(),
        new CharacterCreateHandler(),
        new ItemCreateHandler(),
        new ScheduleChangeHandler(),
        new TravelChangeHandler(),
        new FactionReputationChangeHandler(),
        new FactionStateChangeHandler(),
        new QuestCreateHandler(),
        new QuestProgressHandler(),
        new FactionCreateHandler(),
        new ItemUpdateHandler(),
        new CharacterUpdateHandler(),
        new SystemStatsChangeHandler(),
        new KnowledgeUpdateHandler(),
        new RulesetActionHandler(selector, keys, context),
        new RumorCreateHandler(),
        new RestChangeHandler()
    ];

    /// <summary>Deterministic rolls so harness combat assertions are stable across rulesets.</summary>
    private sealed class HarnessPredictableRollService : IRollService
    {
        public Task<RollOutcome> RollAsync(RollRequest request, CancellationToken ct = default)
        {
            if (request.Mechanic == DiceMechanic.SuccessCount)
            {
                return Task.FromResult(new RollOutcome
                {
                    Tag = request.Tag,
                    Successes = 2,
                    Summary = "Harness: 2 successes"
                });
            }

            var diceVal = request.Expression.Contains("d8", StringComparison.Ordinal) ? 5 : 10;
            return Task.FromResult(new RollOutcome
            {
                Tag = request.Tag,
                Result = diceVal + request.Bonus,
                HasCritical = false,
                HasComplication = false,
                Summary = "Harness predictable roll"
            });
        }

        public async Task<IReadOnlyList<RollOutcome>> RollBatchAsync(
            IEnumerable<RollRequest> requests,
            CancellationToken ct = default)
        {
            var outcomes = new List<RollOutcome>();
            foreach (var request in requests)
            {
                outcomes.Add(await RollAsync(request, ct));
            }

            return outcomes;
        }

        public Task<FalloutCombatDiceResult> RollFalloutCombatDiceAsync(int count, CancellationToken ct = default) =>
            Task.FromResult(new FalloutCombatDiceResult(Damage: 4, Effects: 0, HasCritical: false));
    }
}