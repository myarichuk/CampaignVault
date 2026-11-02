using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data.Impressions;
using CampaignVault.Models;
using CampaignVault.Services;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>IMPRESSIONS_PLAN.md I3: an illusion is cast as its rules allow, and the narrator is told what that is.</summary>
public class IllusionRulesTests
{
    private static readonly SpellDefinitionProvider Spells = new(
        Path.Combine(Path.GetTempPath(), "cv_illusion_spells_" + Guid.NewGuid()),
        typeof(SpellDefinitionProvider).Assembly);

    private static RulesetAction Cast(string spell, string? mode = null)
    {
        var action = new RulesetAction { CharacterId = "chars/maeve", ActionName = spell, ActionType = RulesetActionType.Spell };
        if (mode != null)
        {
            action.Parameters["mode"] = mode;
        }

        return action;
    }

    [Theory]
    [InlineData(RulesetSystem.Dnd5e, "minor_illusion")]
    [InlineData(RulesetSystem.Dnd5e, "silent_image")]
    [InlineData(RulesetSystem.Dnd5e, "major_image")]
    [InlineData(RulesetSystem.Pathfinder2e, "figment")]
    [InlineData(RulesetSystem.Pathfinder2e, "illusory_object")]
    [InlineData(RulesetSystem.Pathfinder2e, "illusory_creature")]
    public void The_generated_illusions_carry_their_quoted_limits(string system, string spell)
    {
        Assert.True(Spells.TryGet(system, spell, out var definition));
        Assert.False(string.IsNullOrWhiteSpace(definition!.IllusionLimits));
        Assert.True(IllusionRules.IsIllusion(definition));
    }

    [Theory]
    [InlineData(RulesetSystem.Dnd5e, "minor_illusion", null, false)]
    [InlineData(RulesetSystem.Dnd5e, "minor_illusion", "both", false)]
    [InlineData(RulesetSystem.Dnd5e, "minor_illusion", "Image", true)]
    [InlineData(RulesetSystem.Dnd5e, "minor_illusion", "sound", true)]
    [InlineData(RulesetSystem.Pathfinder2e, "figment", null, false)]
    [InlineData(RulesetSystem.Pathfinder2e, "figment", "image", true)]
    [InlineData(RulesetSystem.Dnd5e, "silent_image", null, true)]
    public void A_spell_with_forms_takes_exactly_one(string system, string spell, string? mode, bool allowed)
    {
        Spells.TryGet(system, spell, out var definition);
        var refusal = IllusionRules.ModeRefusal(definition, Cast(spell, mode));
        Assert.Equal(allowed, refusal == null);
        if (!allowed)
        {
            Assert.Contains("needs parameters.mode: image or sound", refusal);
        }
    }

    [Fact]
    public void The_disbelief_check_is_the_systems_own()
    {
        Assert.Equal("Investigation", IllusionRules.DisbeliefSkill(RulesetSystem.Dnd5e));
        Assert.Equal("Perception", IllusionRules.DisbeliefSkill(RulesetSystem.Pathfinder2e));
        Assert.Equal(13, IllusionRules.CasterDc(new Character { Id = "chars/m", Name = "M", SystemStats = new Dnd5eExtension { SpellSaveDc = 13 } }));
        Assert.Equal(19, IllusionRules.CasterDc(new Character { Id = "chars/m", Name = "M", SystemStats = new Pf2eExtension { SpellDc = 19 } }));
        Assert.Null(IllusionRules.CasterDc(new Character { Id = "chars/m", Name = "M", SystemStats = new Dnd5eExtension() }));
    }
}

/// <summary>I3 through take_turn.</summary>
[Collection("RavenDB")]
public class IllusionCastTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public IllusionCastTests(RavenDBFixture fixture) => _fixture = fixture;

    private static Task<ToolResult<TurnResult>> CastAsync(NightRoadSightTests.Road r, string? mode)
    {
        var parameters = new Dictionary<string, string> { ["resolution"] = "utility", ["innate"] = "true", ["dc"] = "13" };
        if (mode != null)
        {
            parameters["mode"] = mode;
        }

        return r.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new RulesetAction
                {
                    CharacterId = r.PcId, ActionType = RulesetActionType.Spell, ActionCategory = ActionCategory.Spell,
                    ActionName = "minor_illusion", Parameters = parameters
                }
            ],
            Narrative = "Maeve shapes a ghostly figure on the road."
        }, r.Slug);
    }

    [Fact]
    public async Task Minor_illusion_without_a_form_is_refused_with_the_forms_named()
    {
        var r = await NightRoadSightTests.SeedRoadAsync(_fixture, startCombat: false);

        var cast = await CastAsync(r, null);

        Assert.False(cast.Success);
        Assert.Contains("needs parameters.mode: image or sound", string.Join("\n", cast.Data?.Summary ?? []) + cast.Summary);
    }

    [Fact]
    public async Task A_legal_cast_echoes_the_spells_limits_and_the_check_that_sees_through_it()
    {
        var r = await NightRoadSightTests.SeedRoadAsync(_fixture, startCombat: false);

        var cast = await CastAsync(r, "image");

        Assert.True(cast.Success, cast.Summary);
        var line = Assert.Single(cast.Data!.Summary ?? [], l => l.Contains("ILLUSION LIMITS (minor_illusion"));
        Assert.Contains("no larger than a 5-foot cube", line);
        Assert.Contains("can't create sound, light, smell", line);
        Assert.Contains("Investigation vs DC", line);
    }
}
