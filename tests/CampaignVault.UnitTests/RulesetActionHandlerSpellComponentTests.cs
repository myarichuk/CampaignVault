using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Covers the spell-component gate in RulesetActionHandler.EvaluateSpellComponentsAsync:
/// standard incapacitation, tagged StatModifiers component blocks/waivers, passive feat waivers,
/// and the soft-warning fallback for untagged homebrew conditions.
/// </summary>
[Collection("RavenDB")]
public class RulesetActionHandlerSpellComponentTests : IClassFixture<RavenDBFixture>
{
    private readonly string _campaign = "test-campaign-" + Guid.NewGuid().ToString("N")[..8];
    private readonly RavenDBFixture _fixture;
    private readonly CampaignDocumentKeys _keys = new();
    private static readonly Assembly Assembly = typeof(SpellDefinitionProvider).Assembly;

    public RulesetActionHandlerSpellComponentTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private static SpellDefinitionProvider CreateSpellProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv_spell_gate_test_" + Guid.NewGuid());
        return new SpellDefinitionProvider(dir, Assembly);
    }

    private static FeatDefinitionProvider CreateFeatProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv_feat_gate_test_" + Guid.NewGuid());
        return new FeatDefinitionProvider(dir, Assembly);
    }

    private async Task StoreConfigAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string activeSystem)
    {
        await session.StoreAsync(new CampaignConfig { Id = _keys.Config(_campaign), ActiveSystem = activeSystem });
        await session.SaveChangesAsync();
    }

    private RulesetActionHandler CreateHandler(IRollService? rollService = null)
    {
        IRulesetModule[] modules =
        [
            new Dnd5eRulesetResolver(rollService ?? Substitute.For<IRollService>()),
            new Pf2eRulesetResolver(rollService ?? Substitute.For<IRollService>()),
            new NarrativeRulesetResolver(rollService ?? Substitute.For<IRollService>()),
        ];
        var selector = new RulesetModuleSelector(modules);
        return new RulesetActionHandler(selector, _keys, CreateSpellProvider(), CreateFeatProvider());
    }

    private ChangeContext CreateContext(
        Raven.Client.Documents.Session.IAsyncDocumentSession session,
        Dictionary<string, Character> characters)
    {
        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        return new ChangeContext(
            session,
            characters,
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            [],
            dispatcher,
            null,
            _campaign);
    }

    private async Task StoreCustomSpellAsync(
        Raven.Client.Documents.Session.IAsyncDocumentSession session,
        string name, bool? verbal, bool? somatic, bool? material)
    {
        await session.StoreAsync(new CustomSpell
        {
            Id = $"spells/{name}",
            Name = name,
            System = RulesetSystem.Dnd5e,
            Verbal = verbal,
            Somatic = somatic,
            Material = material,
            CampaignName = _campaign,
        });
        await session.SaveChangesAsync();
    }

    private static RulesetAction CastAction(string characterId, string spellName, string resolution = "utility") => new()
    {
        CharacterId = characterId,
        ActionType = RulesetActionType.Spell,
        ActionName = spellName,
        Parameters = new Dictionary<string, string> { { "resolution", resolution } },
    };

    [Fact]
    public async Task ApplyAsync_IncapacitatedCaster_HardBlocksSpellRegardlessOfComponents()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await StoreConfigAsync(session, RulesetSystem.Dnd5e);
        await StoreCustomSpellAsync(session, "test_spark", verbal: false, somatic: false, material: false);

        var actor = new Character
        {
            Id = "chars/actor",
            Name = "Actor",
            SystemStats = new Dnd5eExtension
            {
                StatusEffects = [new StatusEffect { Name = "Incapacitated", Category = "Condition" }],
            },
        };

        var context = CreateContext(session, new Dictionary<string, Character> { [actor.Id] = actor });
        var handler = CreateHandler();

        var result = await handler.ApplyAsync(CastAction(actor.Id, "test_spark"), context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("SpellcastingBlocked", result.Message);
    }

    [Fact]
    public async Task ApplyAsync_VerbalRequiredAndBlocked_FailsCast()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await StoreConfigAsync(session, RulesetSystem.Dnd5e);
        await StoreCustomSpellAsync(session, "test_word_of_power", verbal: true, somatic: false, material: false);

        var actor = new Character
        {
            Id = "chars/actor",
            Name = "Actor",
            SystemStats = new Dnd5eExtension
            {
                StatusEffects =
                [
                    new StatusEffect
                    {
                        Name = "Gagged",
                        Category = "Condition",
                        StatModifiers = new Dictionary<string, float> { [CastingComponentGate.BlocksVerbal] = 1 },
                    },
                ],
            },
        };

        var context = CreateContext(session, new Dictionary<string, Character> { [actor.Id] = actor });
        var handler = CreateHandler();

        var result = await handler.ApplyAsync(CastAction(actor.Id, "test_word_of_power"), context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("SpellcastingBlocked", result.Message);
        Assert.Contains("Verbal", result.Message);
    }

    [Fact]
    public async Task ApplyAsync_SomaticBlockedButWaivedByStatModifier_Succeeds()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await StoreConfigAsync(session, RulesetSystem.Dnd5e);
        await StoreCustomSpellAsync(session, "test_gesture_spell", verbal: false, somatic: true, material: false);

        var actor = new Character
        {
            Id = "chars/actor",
            Name = "Actor",
            SystemStats = new Dnd5eExtension
            {
                StatusEffects =
                [
                    new StatusEffect
                    {
                        Name = "Hands Full",
                        Category = "Condition",
                        StatModifiers = new Dictionary<string, float>
                        {
                            [CastingComponentGate.BlocksSomatic] = 1,
                            [CastingComponentGate.WaivesSomatic] = 1,
                        },
                    },
                ],
            },
        };

        var context = CreateContext(session, new Dictionary<string, Character> { [actor.Id] = actor });
        var handler = CreateHandler();

        var result = await handler.ApplyAsync(CastAction(actor.Id, "test_gesture_spell"), context, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ApplyAsync_SomaticBlockedButWaivedByPassiveFeat_Succeeds()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await StoreConfigAsync(session, RulesetSystem.Dnd5e);
        await StoreCustomSpellAsync(session, "test_gesture_spell2", verbal: false, somatic: true, material: false);
        await session.StoreAsync(new CustomFeat
        {
            Id = "feats/test_war_caster",
            Name = "test_war_caster",
            System = RulesetSystem.Dnd5e,
            CastingWaivers = [CastingComponentGate.SomaticHandsFullWaiver],
            CampaignName = _campaign,
        });
        await session.SaveChangesAsync();

        var actor = new Character
        {
            Id = "chars/actor",
            Name = "Actor",
            SystemStats = new Dnd5eExtension
            {
                Feats = ["test_war_caster"],
                StatusEffects =
                [
                    new StatusEffect
                    {
                        Name = "Hands Full",
                        Category = "Condition",
                        StatModifiers = new Dictionary<string, float> { [CastingComponentGate.BlocksSomatic] = 1 },
                    },
                ],
            },
        };

        var context = CreateContext(session, new Dictionary<string, Character> { [actor.Id] = actor });
        var handler = CreateHandler();

        var result = await handler.ApplyAsync(CastAction(actor.Id, "test_gesture_spell2"), context, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ApplyAsync_UntaggedHomebrewCondition_WarnsButSucceeds()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await StoreConfigAsync(session, RulesetSystem.Dnd5e);
        await StoreCustomSpellAsync(session, "test_mind_word", verbal: true, somatic: false, material: false);

        var actor = new Character
        {
            Id = "chars/actor",
            Name = "Actor",
            SystemStats = new Dnd5eExtension
            {
                StatusEffects = [new StatusEffect { Name = "Mind-Fogged", Category = "Condition" }],
            },
        };

        var context = CreateContext(session, new Dictionary<string, Character> { [actor.Id] = actor });
        var handler = CreateHandler();

        var result = await handler.ApplyAsync(CastAction(actor.Id, "test_mind_word"), context, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ApplyAsync_NoComponentsRequired_UnaffectedByBlockingStatus()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await StoreConfigAsync(session, RulesetSystem.Dnd5e);
        await StoreCustomSpellAsync(session, "test_pure_thought", verbal: false, somatic: false, material: false);

        var actor = new Character
        {
            Id = "chars/actor",
            Name = "Actor",
            SystemStats = new Dnd5eExtension
            {
                StatusEffects =
                [
                    new StatusEffect
                    {
                        Name = "Gagged",
                        Category = "Condition",
                        StatModifiers = new Dictionary<string, float> { [CastingComponentGate.BlocksVerbal] = 1 },
                    },
                ],
            },
        };

        var context = CreateContext(session, new Dictionary<string, Character> { [actor.Id] = actor });
        var handler = CreateHandler();

        var result = await handler.ApplyAsync(CastAction(actor.Id, "test_pure_thought"), context, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ApplyAsync_UnknownSpellName_SkipsComponentCheckEntirely()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await StoreConfigAsync(session, RulesetSystem.Dnd5e);

        var actor = new Character
        {
            Id = "chars/actor",
            Name = "Actor",
            SystemStats = new Dnd5eExtension
            {
                StatusEffects =
                [
                    new StatusEffect
                    {
                        Name = "Gagged",
                        Category = "Condition",
                        StatModifiers = new Dictionary<string, float> { [CastingComponentGate.BlocksVerbal] = 1 },
                    },
                ],
            },
        };

        var context = CreateContext(session, new Dictionary<string, Character> { [actor.Id] = actor });
        var handler = CreateHandler();

        var result = await handler.ApplyAsync(CastAction(actor.Id, "totally_made_up_spell_name"), context, CancellationToken.None);

        Assert.True(result.Success);
    }
}
