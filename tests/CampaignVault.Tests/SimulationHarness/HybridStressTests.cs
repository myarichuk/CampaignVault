using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests.SimulationHarness;

[Collection("RavenDB")]
public class HybridStressTests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;
    private readonly Random _rng = new();
    private readonly RavenDBFixture _fixture;

    public HybridStressTests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
        _fixture = fixture;
    }

    [Fact]
    public async Task ChaoticCampaignLoop_StressTests_Invariants()
    {
        var engine = new DefaultSimulationEngine(
            [new NeedsAccumulationRule(), new RumorDecayRule(), new ScheduleEvaluationRule()],
            null);
        var repo = _fixture.CreateRepository(engineOverride: engine);
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        using var session = _store.OpenAsyncSession();
        var simulator = new LlmSimulator(tools, session);

        // Setup: A small region with 3 NPCs
        var regionId = "locations/fuzz-region-" + Guid.NewGuid();
        var npcs = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var id = $"npcs/fuzzer-{i}-" + Guid.NewGuid();
            await repo.UpsertCharacterAsync(session, new Character
            {
                Id = id,
                Name = $"Fuzz NPC {i}",
                Schedule = new Schedule { DefaultLocationId = regionId },
                Social = new SocialProfile(),
                Needs = new NeedsProfile()
            }, TestCampaignDefaults.Slug);
            npcs.Add(id);
        }

        await repo.UpsertLocationAsync(session,
            new Location { Id = regionId, Name = "Fuzz Test Ground", Type = LocationType.Region }, TestCampaignDefaults.Slug);
        await session.SaveChangesAsync();

        // RUN LOOP
        await simulator.Kickoff(regionId);

        for (var iteration = 0; iteration < 20; iteration++) // Run 20 cycles
        {
            // 1. Randomly mutate relationships or needs (Resolution phase simulate)
            var targetId = npcs[_rng.Next(npcs.Count)];
            var changes = new List<WorldChange>();

            // Random Need Change (potentially extreme)
            changes.Add(new NeedChange
            {
                CharacterId = targetId,
                Need = _rng.Next(2) == 0 ? "hunger" : "tiredness",
                Delta = (float)(_rng.NextDouble() * 200 - 100) // -100 to +100
            });

            // Random Relationship Change
            changes.Add(new RelationshipChange
            {
                CharacterId = targetId,
                TargetId = npcs[_rng.Next(npcs.Count)],
                Delta = _rng.Next(-50, 50),
                Reason = "Random fuzz interaction"
            });

            var resolveResult = await tools.Commit(changes.ToArray(), $"Fuzz cycle {iteration}");
            Assert.True(resolveResult.Success);

            // 2. Random Time Passage (Downtime phase simulate)
            var days = _rng.Next(1, 15);
            var advanceResult = await tools.AdvanceWorld(days, TimeOfDay.Night, "Fuzz time skip");
            Assert.True(advanceResult.Success);

            await session.SaveChangesAsync();

            // 3. Invariant Checks
            foreach (var id in npcs)
            {
                var charDoc = await session.LoadAsync<Character>(id);
                Assert.NotNull(charDoc.Social);
                Assert.NotNull(charDoc.Needs);

                // Needs must be clamped 0-100
                foreach (var (need, val) in charDoc.Needs.ActiveNeeds)
                {
                    Assert.True(val >= 0f && val <= 100f,
                        $"Need {need} was {val} (out of bounds) on iteration {iteration}");
                }

                // Relationships must be clamped -100 to 100
                foreach (var (rel, val) in charDoc.Social.Relationships)
                {
                    Assert.True(val >= -100 && val <= 100,
                        $"Relationship to {rel} was {val} (out of bounds) on iteration {iteration}");
                }
            }
        }
    }
}
