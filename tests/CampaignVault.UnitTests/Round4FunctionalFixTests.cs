using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Guidance.Contributors;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Round-4 functional bugs F1–F3 (TOKEN_SURFACE_PLAN.md), fixture-free so they
/// run without RavenDB:
/// F1 — a targeted spell with no mechanics (Fire Bolt + targetIds, no params)
/// must fail with a fix hint instead of resolving as a silent utility no-op.
/// F2 — the spell-component soft warning fires only for conditions whose name
/// reads like a casting blocker (Mage Armor stays quiet).
/// F3 — a lone skill check (ruleset_action, no raw hp/status edit) must not
/// trip the "combat/status changes but no event" reminder.
/// </summary>
public class Round4FunctionalFixTests
{
    private static ChangeContext CreateContext(params Character[] characters)
    {
        var charDict = characters.ToDictionary(c => c.Id);
        return new ChangeContext(
            sessionForTests: null,
            characters: charDict,
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher(
                [],
                new CampaignVault.Data.CampaignDocumentKeys(),
                NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null);
    }

    private static Character Caster() => new()
    {
        Id = "caster",
        Name = "Caster",
        SystemStats = new Dnd5eExtension
        {
            Level = 3,
            Intelligence = 16,
            SpellcastingAbility = "Intelligence",
            SpellSaveDc = 13,
        },
    };

    private static Character Target() => new()
    {
        Id = "target",
        Name = "Goblin",
        SystemStats = new Dnd5eExtension { Dexterity = 10 },
    };

    [Fact]
    public async Task F1_TargetedSpellWithoutMechanics_FailsWithFixHint()
    {
        var resolver = new Dnd5eRulesetResolver(Substitute.For<IRollService>());
        var action = new RulesetAction
        {
            CharacterId = "caster",
            TargetIds = ["target"],
            ActionType = RulesetActionType.Spell,
            ActionCategory = ActionCategory.Spell,
            ActionName = "Fire Bolt",
            Parameters = [],
        };

        var output = await resolver.ResolveAsync(CreateContext(Caster(), Target()), action);

        Assert.False(output.Result.Success);
        Assert.Contains("resolution=attack", output.Result.Narrative);
        Assert.Contains("damageDice", output.Result.Narrative);
        Assert.Empty(output.Mutations);
    }

    [Fact]
    public async Task F1_UntargetedUtilitySpell_StillResolves()
    {
        var resolver = new Dnd5eRulesetResolver(Substitute.For<IRollService>());
        var action = new RulesetAction
        {
            CharacterId = "caster",
            ActionType = RulesetActionType.Spell,
            ActionCategory = ActionCategory.Spell,
            ActionName = "Dancing Lights",
            Parameters = [],
        };

        var output = await resolver.ResolveAsync(CreateContext(Caster()), action);

        Assert.True(output.Result.Success);
    }

    [Fact]
    public async Task F1_ExplicitUtilityWithTargets_StillResolves()
    {
        var resolver = new Dnd5eRulesetResolver(Substitute.For<IRollService>());
        var action = new RulesetAction
        {
            CharacterId = "caster",
            TargetIds = ["target"],
            ActionType = RulesetActionType.Spell,
            ActionCategory = ActionCategory.Spell,
            ActionName = "Mage Armor",
            Parameters = new Dictionary<string, string> { { "resolution", "utility" } },
        };

        var output = await resolver.ResolveAsync(CreateContext(Caster(), Target()), action);

        Assert.True(output.Result.Success);
    }

    [Theory]
    [InlineData("Mage Armor", false)]
    [InlineData("Bless", false)]
    [InlineData("Brave", false)]
    [InlineData("Mind-Fogged", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Gagged", true)]
    [InlineData("Bound Hands", true)]
    [InlineData("Cannot Speak", true)]
    [InlineData("Manacled", true)]
    [InlineData("Paralyzed", true)]
    public void F2_ComponentBlockerNameGate(string? name, bool expected)
    {
        Assert.Equal(expected, RulesetActionHandler.LooksLikeComponentBlocker(name));
    }

    [Fact]
    public void F3_LoneSkillCheck_DoesNotTripCombatStatusReminder()
    {
        var result = new TurnResult();
        MutationTools.ComposeReminders(
            [new RulesetAction
            {
                CharacterId = "chars/a",
                ActionType = RulesetActionType.SkillCheck,
                ActionName = "Perception",
            }],
            result);

        Assert.Null(result.NarrativeReminder);
    }

    [Fact]
    public void F3_RawHpEditWithoutEvent_StillReminds()
    {
        var result = new TurnResult();
        MutationTools.ComposeReminders(
            [new HpChange { CharacterId = "chars/a", Delta = -3 }],
            result);

        Assert.Contains("combat/status changes but no 'event'", result.NarrativeReminder);
    }

    [Fact]
    public async Task CombatStartedGuidance_FiresOnRoundOneCombatScene()
    {
        var contributor = new CombatStartedGuidanceContributor();
        var scene = new SceneView
        {
            Location = null!,
            ActiveCombat = new CombatEncounterView("locations/arena", 1, [], "chars/hero", true),
        };
        var ctx = new PressureContext("campaign", new CampaignTime(), new CampaignConfig(), null!, Scene: scene);

        Assert.Single(await contributor.EvaluateAsync(ctx));
    }

    [Fact]
    public async Task CombatStartedGuidance_QuietWithoutScene()
    {
        var contributor = new CombatStartedGuidanceContributor();
        var ctx = new PressureContext("campaign", new CampaignTime(), new CampaignConfig(), null!);

        Assert.Empty(await contributor.EvaluateAsync(ctx));
    }

    [Fact]
    public void F3_RawHpEditWithEvent_StaysQuiet()
    {
        var result = new TurnResult();
        MutationTools.ComposeReminders(
            [
                new HpChange { CharacterId = "chars/a", Delta = -3 },
                new EventOccurred { Summary = "A blow lands." },
            ],
            result);

        Assert.Null(result.NarrativeReminder);
    }
}
