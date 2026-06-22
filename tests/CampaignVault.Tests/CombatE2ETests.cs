using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Tools;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class CombatE2ETests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;
    private readonly RavenDBFixture _fixture;

    public CombatE2ETests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
        _fixture = fixture;
    }

    private class PredictableRollService : IRollService
    {
        public Task<RollOutcome> RollAsync(RollRequest request, System.Threading.CancellationToken ct = default)
        {
            var diceVal = 10;
            if (request.Expression.Contains("d8"))
            {
                diceVal = 5;
            }

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

        public Task<FalloutCombatDiceResult> RollFalloutCombatDiceAsync(int count,
            System.Threading.CancellationToken ct = default)
        {
            return Task.FromResult(new FalloutCombatDiceResult(0, 0, false));
        }
    }

    private CampaignTools CreateTools(string campaignName)
    {
        var context = new CurrentCampaignContext();
        context.SetCurrent(campaignName);
        return TestCampaignToolsFactory.Create(_fixture, context, rollService: new PredictableRollService());
    }

    [Fact]
    public async Task Combat_RulesetAction_EndToEnd_Flow()
    {
        var campaignName = "e2e-combat-test";
        var tools = CreateTools(campaignName);
        var repo = _fixture.CreateRepository();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(session,
                new Character
                {
                    Id = "characters/hero", Name = "Hero", CurrentHp = 50, MaxHp = 50,
                    SystemStats = new Dnd5eExtension { ArmorClass = 10 }
                });
            await repo.UpsertCharacterAsync(session,
                new Character
                {
                    Id = "characters/goblin", Name = "Goblin", CurrentHp = 15, MaxHp = 15,
                    SystemStats = new Dnd5eExtension { ArmorClass = 10 }
                });
            await session.SaveChangesAsync();
        }

        // 1. Initialize System
        await tools.SetActiveSystem(RulesetSystem.Dnd5e, null, campaignName);

        // 2. Start Combat
        var startResult = await tools.StartCombat("loc-1", ["characters/hero", "characters/goblin"], campaignName);
        Assert.True(startResult.Success);

        // 3. Commit a Ruleset Action (Hero attacks Goblin)
        var actionJson = JsonSerializer.Serialize<WorldChange[]>([
            new RulesetAction
            {
                ActionType = RulesetActionType.Attack,
                ActorId = "characters/hero",
                TargetIds = ["characters/goblin"],
                Parameters = new Dictionary<string, string>
                    { { "bonus", "5" }, { "damageDice", "1d8" }, { "damageBonus", "3" } }
            }
        ]);

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
        var statusJson = JsonSerializer.Serialize<WorldChange[]>([
            new StatusChange
            {
                CharacterId = "characters/goblin", Effect = new StatusEffect { Name = "Stunned", ExpiresAtRound = 1 }
            }
        ]);
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