using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using CampaignVault.Rulesets;
using CampaignVault.Data.ChangeHandlers;
using Raven.Client.Documents;
using System.Threading.Tasks;
using Xunit;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class CombatE2ETests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;

    public CombatE2ETests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
    }

    private class PredictableRollService : IRollService
    {
        public Task<RollOutcome> RollAsync(RollRequest request, System.Threading.CancellationToken ct = default)
        {
            int diceVal = 10;
            if (request.Expression.Contains("d8")) diceVal = 5;
            
            return Task.FromResult(new RollOutcome 
            { 
                Tag = request.Tag,
                Result = diceVal + request.Bonus, 
                HasCritical = false, 
                HasComplication = false, 
                Summary = "Predictable roll"
            });
        }

        public async Task<IReadOnlyList<RollOutcome>> RollBatchAsync(
            IEnumerable<RollRequest> requests,
            System.Threading.CancellationToken ct = default)
        {
            var outcomes = new List<RollOutcome>();
            foreach (var req in requests)
            {
                outcomes.Add(await RollAsync(req, ct));
            }
            return outcomes;
        }

        public Task<FalloutCombatDiceResult> RollFalloutCombatDiceAsync(int count, System.Threading.CancellationToken ct = default)
        {
            return Task.FromResult(new FalloutCombatDiceResult(0, 0, false));
        }
    }

    private CampaignTools CreateTools(string campaignName)
    {
        var behavior = new DefaultBehaviorSynthesizer();
        var rollService = new PredictableRollService();
        
        var dnd5e = new Dnd5eRulesetResolver(rollService);
        var pf2e = new Pf2eRulesetResolver(rollService);
        var fallout = new Fallout2d20RulesetResolver(rollService);
        var selector = new RulesetResolverSelector(new IRulesetResolver[] { dnd5e, pf2e, fallout });

        var context = new CurrentCampaignContext();
        context.SetCurrent(campaignName);

        var keys = new CampaignDocumentKeys();
        var handlers = new IWorldChangeHandler[]
        {
            new HpChangeHandler(),
            new ItemTransferHandler(),
            new StatusChangeHandler(),
            new EventOccurredHandler(),
            new RumorEvolvesHandler(),
            new RelationshipChangeHandler(),
            new NeedChangeHandler(),
            new AttributeChangeHandler(),
            new MoodChangeHandler(),
            new ActivityChangeHandler(),
            new RulesetActionHandler(selector, keys, context)
        };

        var repo = new CampaignRepository(
            _store,
            new DefaultSimulationEngine(System.Array.Empty<ISimulationRule>()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CampaignRepository>.Instance,
            behavior,
            keys,
            context,
            handlers
        );

        return new CampaignTools(repo, behavior, selector, keys, context);
    }

    [Fact]
    public async Task Combat_RulesetAction_EndToEnd_Flow()
    {
        var campaignName = "e2e-combat-test";
        var tools = CreateTools(campaignName);
        var repo = new CampaignRepository(_store);

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(session, new Character { Id = "characters/hero", Name = "Hero", CurrentHp = 50, MaxHp = 50, SystemStats = new Dnd5eExtension { ArmorClass = 10 } });
            await repo.UpsertCharacterAsync(session, new Character { Id = "characters/goblin", Name = "Goblin", CurrentHp = 15, MaxHp = 15, SystemStats = new Dnd5eExtension { ArmorClass = 10 } });
            await session.SaveChangesAsync();
        }

        // 1. Initialize System
        await tools.SetActiveSystem(RulesetSystem.Dnd5e, null, campaignName);

        // 2. Start Combat
        var startResult = await tools.StartCombat("loc-1", new[] { "characters/hero", "characters/goblin" }, campaignName);
        Assert.True(startResult.Success);

        // 3. Commit a Ruleset Action (Hero attacks Goblin)
        var actionJson = JsonSerializer.Serialize<WorldChange[]>(new[] { new RulesetAction
        {
            ActionType = RulesetActionType.Attack,
            ActorId = "characters/hero",
            TargetIds = new List<string> { "characters/goblin" },
            Parameters = new Dictionary<string, string> { { "bonus", "5" }, { "damageDice", "1d8" }, { "damageBonus", "3" } }
        }});

        var commitResult = await tools.Commit(actionJson, "Hero attacks Goblin", campaignName);
        Assert.True(commitResult.Success);

        // Verify the attack dealt damage
        using (var session = _store.OpenAsyncSession())
        {
            var goblin = await repo.GetCharacterAsync(session, "characters/goblin", null);
            Assert.NotNull(goblin);
            Assert.True(goblin.CurrentHp < 15); // Damage was applied
        }

        // 4. Advance Turn
        var turn1 = await tools.NextTurn(null, campaignName);
        Assert.True(turn1.Success);

        // 5. Commit another change (Goblin gets a status)
        var statusJson = JsonSerializer.Serialize<WorldChange[]>(new[] 
        { 
            new StatusChange { CharacterId = "characters/goblin", Effect = new StatusEffect { Name = "Stunned", ExpiresAtRound = 1 } }
        });
        await tools.Commit(statusJson, "Goblin gets stunned", campaignName);

        // 6. End Combat
        var endResult = await tools.EndCombat(campaignName);
        Assert.True(endResult.Success);

        // 7. Verify combat is ended
        var turnAfterEnd = await tools.NextTurn(null, campaignName);
        Assert.False(turnAfterEnd.Success);
        Assert.Equal("NotFound", turnAfterEnd.Error);
    }
}
