using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class ConcentrationBreakTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public ConcentrationBreakTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private ChangeContext CreateContext(IAsyncDocumentSession session, Character character)
    {
        return new ChangeContext(
            session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            () => Task.FromResult(new CampaignTime { TotalDaysElapsed = 10 }),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask,
            [],
            new WorldChangeDispatcher(new List<IWorldChangeHandler>(), new CampaignDocumentKeys()),
            null,
            "test-campaign"
        );
    }

    private static Character CreateConcentratingCharacter(int constitutionMod = 0)
    {
        return new Character
        {
            Id = "chars/caster",
            Name = "Caster",
            MaxHp = 50,
            CurrentHp = 50,
            SystemStats = new Dnd5eExtension
            {
                Constitution = 10 + constitutionMod * 2,
                StatusEffects =
                [
                    new StatusEffect { Name = "Concentration: Bless", Category = "Buff" }
                ]
            }
        };
    }

    private static Character CreatePf2eConcentratingCharacter(int? fortitudeSaveModifier = null, int constitutionMod = 0)
    {
        var pf2e = new Pf2eExtension
        {
            ConstitutionMod = constitutionMod,
            StatusEffects =
            [
                new StatusEffect { Name = "Concentration: Bless", Category = "Buff" }
            ]
        };
        if (fortitudeSaveModifier.HasValue)
        {
            pf2e.SavingThrowModifiers["Fortitude"] = fortitudeSaveModifier.Value;
        }

        return new Character
        {
            Id = "chars/caster",
            Name = "Caster",
            MaxHp = 50,
            CurrentHp = 50,
            SystemStats = pf2e
        };
    }

    private sealed class FixedRollService(int result) : IRollService
    {
        public Task<RollOutcome> RollAsync(RollRequest request, CancellationToken ct = default) =>
            Task.FromResult(new RollOutcome { Tag = request.Tag, Result = result + request.Bonus });

        public async Task<IReadOnlyList<RollOutcome>> RollBatchAsync(
            IEnumerable<RollRequest> requests, CancellationToken ct = default)
        {
            var outcomes = new List<RollOutcome>();
            foreach (var request in requests)
            {
                outcomes.Add(await RollAsync(request, ct));
            }
            return outcomes;
        }
    }

    [Fact]
    public async Task HpChange_FailedConcentrationSave_BreaksConcentration()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = CreateConcentratingCharacter();
        var ctx = CreateContext(session, character);
        var handler = new HpChangeHandler(new FixedRollService(1)); // 1 + 0 bonus = 1, well below DC 10

        var result = await handler.ApplyAsync(new HpChange { CharacterId = character.Id, Delta = -20 }, ctx);

        Assert.True(result.Success);
        Assert.DoesNotContain(character.SystemStats!.StatusEffects, e => e.Name.Contains("Concentration"));
    }

    [Fact]
    public async Task HpChange_SuccessfulConcentrationSave_KeepsConcentration()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = CreateConcentratingCharacter();
        var ctx = CreateContext(session, character);
        var handler = new HpChangeHandler(new FixedRollService(20)); // 20 + 0 bonus, well above DC

        var result = await handler.ApplyAsync(new HpChange { CharacterId = character.Id, Delta = -20 }, ctx);

        Assert.True(result.Success);
        Assert.Contains(character.SystemStats!.StatusEffects, e => e.Name.Contains("Concentration"));
    }

    [Fact]
    public async Task HpChange_NoDamage_DoesNotRollConcentrationSave()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = CreateConcentratingCharacter();
        var ctx = CreateContext(session, character);
        var handler = new HpChangeHandler(new FixedRollService(1));

        var result = await handler.ApplyAsync(new HpChange { CharacterId = character.Id, Delta = 5 }, ctx);

        Assert.True(result.Success);
        Assert.Contains(character.SystemStats!.StatusEffects, e => e.Name.Contains("Concentration"));
    }

    [Fact]
    public async Task HpChange_Pf2e_FailedFortitudeSave_BreaksConcentration()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = CreatePf2eConcentratingCharacter(fortitudeSaveModifier: 2);
        var ctx = CreateContext(session, character);
        var handler = new HpChangeHandler(new FixedRollService(1)); // 1 + 2 bonus = 3, well below DC 10

        var result = await handler.ApplyAsync(new HpChange { CharacterId = character.Id, Delta = -20 }, ctx);

        Assert.True(result.Success);
        Assert.DoesNotContain(character.SystemStats!.StatusEffects, e => e.Name.Contains("Concentration"));
    }

    [Fact]
    public async Task HpChange_Pf2e_SuccessfulFortitudeSave_KeepsConcentration()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = CreatePf2eConcentratingCharacter(fortitudeSaveModifier: 8);
        var ctx = CreateContext(session, character);
        var handler = new HpChangeHandler(new FixedRollService(15)); // 15 + 8 bonus, well above DC

        var result = await handler.ApplyAsync(new HpChange { CharacterId = character.Id, Delta = -20 }, ctx);

        Assert.True(result.Success);
        Assert.Contains(character.SystemStats!.StatusEffects, e => e.Name.Contains("Concentration"));
    }

    [Fact]
    public async Task HpChange_Pf2e_FallsBackToConstitutionModWhenNoFortitudeEntry()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = CreatePf2eConcentratingCharacter(fortitudeSaveModifier: null, constitutionMod: 6);
        var ctx = CreateContext(session, character);
        var handler = new HpChangeHandler(new FixedRollService(10)); // 10 + 6 fallback mod = 16, above DC 10

        var result = await handler.ApplyAsync(new HpChange { CharacterId = character.Id, Delta = -20 }, ctx);

        Assert.True(result.Success);
        Assert.Contains(character.SystemStats!.StatusEffects, e => e.Name.Contains("Concentration"));
    }

    [Fact]
    public async Task StatusChange_NewConcentrationEffect_BreaksPriorConcentration()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = CreateConcentratingCharacter();
        var ctx = CreateContext(session, character);
        var handler = RulesetDataTestHelper.CreateStatusChangeHandler();

        var result = await handler.ApplyAsync(new StatusChange
        {
            CharacterId = character.Id,
            Effect = new StatusEffect { Name = "Concentration: Hold Person", Category = "Debuff" }
        }, ctx);

        Assert.True(result.Success);
        Assert.Single(character.SystemStats!.StatusEffects);
        Assert.Equal("Concentration: Hold Person", character.SystemStats.StatusEffects[0].Name);
    }
}
