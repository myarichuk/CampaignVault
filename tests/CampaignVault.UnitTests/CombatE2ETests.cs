using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
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
    }

    private CampaignTools CreateTools() =>
        TestCampaignToolsFactory.Create(_fixture, rollService: new PredictableRollService());

    [Fact]
    public async Task Combat_RulesetAction_EndToEnd_Flow()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var campaignName = $"e2e-combat-{suffix}";
        var heroId = $"chars/e2e-hero-{suffix}";
        var goblinId = $"chars/e2e-goblin-{suffix}";
        var tools = CreateTools();
        var repo = _fixture.CreateRepository();

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, campaignName), new CharacterUpsertRequest
                {
                    Id = heroId, Name = "Hero", CurrentHp = 50, MaxHp = 50,
                    SystemStats = new Dnd5eExtension { ArmorClass = 10 }
                });
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, campaignName), new CharacterUpsertRequest
                {
                    Id = goblinId, Name = "Goblin", CurrentHp = 15, MaxHp = 15,
                    SystemStats = new Dnd5eExtension { ArmorClass = 10 }
                });
            await session.SaveChangesAsync();
        }

        // 1. Initialize System
        await tools.SetActiveSystem(RulesetSystem.Dnd5e, null, campaignName);

        // 2. Start Combat
        var startResult = await tools.StartCombat("loc-1", [heroId, goblinId], campaignName);
        Assert.True(startResult.Success, $"StartCombat failed: {startResult.Error} — {startResult.Summary}");

        // 3. Commit a Ruleset Action (Hero attacks Goblin)
        var actionJson = JsonSerializer.Serialize<WorldChange[]>([
            new RulesetAction
            {
                ActionType = RulesetActionType.Attack,
                CharacterId = heroId,
                TargetIds = [goblinId],
                Parameters = new Dictionary<string, string>
                    { { "bonus", "5" }, { "damageDice", "1d8" }, { "damageBonus", "3" } }
            }
        ]);

        var commitResult = await tools.Commit(actionJson, "Hero attacks Goblin", campaignName);
        Assert.True(commitResult.Success);

        // Verify the attack dealt damage
        using (var session = _store.OpenAsyncSession())
        {
            var goblin = await repo.GetCharacterAsync(_fixture.CreateCampaignSession(session, campaignName), goblinId);
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
                CharacterId = goblinId, Effect = new StatusEffect { Name = "Stunned", ExpiresAtRound = 1 }
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
