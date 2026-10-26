using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// A saving throw is rolled against the save the caller named, validated per ruleset, or the one the handbook
/// entry for the named spell declares. An omitted save with nothing to derive it from is an error, never a
/// default; an unknown name is an error, never a +0 roll.
/// </summary>
public class SavingThrowNameResolutionTests
{
    [Theory]
    [InlineData(RulesetSystem.Dnd5e, "dex", "Dexterity", null)]
    [InlineData(RulesetSystem.Dnd5e, "Dex. Save", "Dexterity", null)]
    [InlineData(RulesetSystem.Dnd5e, "WISDOM saving throw", "Wisdom", null)]
    [InlineData(RulesetSystem.Pathfinder2e, "fort", "Fortitude", null)]
    [InlineData(RulesetSystem.Pathfinder2e, "Reflex save", "Reflex", null)]
    [InlineData(RulesetSystem.Pathfinder2e, "Constitution", "Fortitude", "Constitution read as the Fortitude save")]
    [InlineData(RulesetSystem.Pathfinder2e, "wis", "Will", "wis read as the Will save")]
    public void TryResolve_CanonicalizesKnownNames(string system, string input, string expected, string? expectedNote)
    {
        Assert.True(SavingThrowNames.TryResolve(system, input, out var canonical, out var note));
        Assert.Equal(expected, canonical);
        Assert.Equal(expectedNote, note);
    }

    [Theory]
    [InlineData(RulesetSystem.Dnd5e, "Fortitude")]
    [InlineData(RulesetSystem.Dnd5e, "Will")]
    [InlineData(RulesetSystem.Dnd5e, "")]
    [InlineData(RulesetSystem.Pathfinder2e, "Charisma")]
    [InlineData(RulesetSystem.Pathfinder2e, "Strength")]
    [InlineData(RulesetSystem.Pathfinder2e, "luck")]
    public void TryResolve_RejectsNamesTheRulesetDoesNotRoll(string system, string input)
    {
        Assert.False(SavingThrowNames.TryResolve(system, input, out _, out _));
    }

    [Fact]
    public async Task Dnd5e_OmittedSave_WithNoHandbookSpell_FailsAndListsTheSaves()
    {
        var rolls = new FakeRollService();
        var resolver = new Dnd5eRulesetResolver(rolls);
        var actor = new Character { Id = "chars/a", Name = "A", SystemStats = new Dnd5eExtension() };

        var output = await resolver.ResolveAsync(Context(actor), new RulesetAction
        {
            CharacterId = actor.Id,
            ActionType = RulesetActionType.SavingThrow,
            ActionName = "Pit trap",
            Parameters = new Dictionary<string, string> { ["dc"] = "13" },
        }, TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Equal("InvalidParameter", output.Result.ErrorCode);
        Assert.Contains("'save' parameter", output.Result.Narrative);
        Assert.Contains("Strength, Dexterity, Constitution, Intelligence, Wisdom, Charisma", output.Result.Narrative);
        Assert.Empty(rolls.RecordedRequests);
    }

    [Fact]
    public async Task Dnd5e_UnknownSaveName_FailsInsteadOfRollingAtZero()
    {
        var rolls = new FakeRollService();
        var resolver = new Dnd5eRulesetResolver(rolls);
        var actor = new Character { Id = "chars/a", Name = "A", SystemStats = new Dnd5eExtension() };

        var output = await resolver.ResolveAsync(Context(actor), new RulesetAction
        {
            CharacterId = actor.Id,
            ActionType = RulesetActionType.SavingThrow,
            ActionName = "Fear",
            Parameters = new Dictionary<string, string> { ["dc"] = "13", ["save"] = "Fortitude" },
        }, TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Contains("'Fortitude' is not a dnd5e saving throw", output.Result.Narrative);
        Assert.Empty(rolls.RecordedRequests);
    }

    [Fact]
    public async Task Dnd5e_AbbreviatedSave_RollsTheCanonicalAbility()
    {
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 15, Summary = "[15]" });
        var resolver = new Dnd5eRulesetResolver(rolls);
        var actor = new Character { Id = "chars/a", Name = "A", SystemStats = new Dnd5eExtension { Dexterity = 16 } };

        var output = await resolver.ResolveAsync(Context(actor), new RulesetAction
        {
            CharacterId = actor.Id,
            ActionType = RulesetActionType.SavingThrow,
            ActionName = "Pit trap",
            Parameters = new Dictionary<string, string> { ["dc"] = "13", ["save"] = "dex save" },
        }, TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Contains("(Dexterity Save)", output.Result.Narrative);
        Assert.Equal(3, Assert.Single(rolls.RecordedRequests).Bonus); // DEX 16 → +3, not the +0 an unknown name used to get
    }

    [Fact]
    public async Task Dnd5e_OmittedSave_OnHandbookSpell_UsesTheEntrysSaveTypeAndSaysSo()
    {
        var rolls = new FakeRollService();
        foreach (var r in new[] { 9, 20, 9 })
        {
            rolls.NextRolls.Enqueue(new RollOutcome { Result = r, Summary = $"[{r}]" });
        }

        var dir = Path.Combine(Path.GetTempPath(), "cv_save_name_test_" + Guid.NewGuid());
        var resolver = new Dnd5eRulesetResolver(rolls, spellDefinitionProvider: new SpellDefinitionProvider(dir, typeof(SpellDefinitionProvider).Assembly));
        var caster = new Character { Id = "chars/caster", Name = "Caster", SystemStats = new Dnd5eExtension { Level = 5 } };
        var target = new Character { Id = "chars/goblin", Name = "Goblin", SystemStats = new Dnd5eExtension { Dexterity = 14 } };

        var output = await resolver.ResolveAsync(Context(caster, target), new RulesetAction
        {
            CharacterId = caster.Id,
            TargetIds = [target.Id],
            ActionType = RulesetActionType.Spell,
            ActionName = "Fireball",
            Parameters = new Dictionary<string, string> { ["resolution"] = "save", ["dc"] = "15", ["damageDice"] = "8d6" },
        }, TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Contains("[Dexterity save from the 'Fireball' entry]", output.Result.Narrative);
        Assert.DoesNotContain("[WARNING]", output.Result.Narrative);
        var save = rolls.RecordedRequests.First(r => r.Tag == "spell-save");
        Assert.Equal(2, save.Bonus); // the target's DEX 14, so the derived save really was Dexterity
    }

    [Fact]
    public async Task Pf2e_AbilityName_MapsToItsSave_WithTheTrainedBonusAndANote()
    {
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 14, Summary = "[14]" });
        var resolver = new Pf2eRulesetResolver(rolls);
        var actor = new Character { Id = "chars/a", Name = "A", SystemStats = new Pf2eExtension { Level = 3, ConstitutionMod = 2 } };

        var output = await resolver.ResolveAsync(Context(actor), new RulesetAction
        {
            CharacterId = actor.Id,
            ActionType = RulesetActionType.SavingThrow,
            ActionName = "Poison",
            Parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = "Constitution" },
        }, TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Contains("(Fortitude)", output.Result.Narrative);
        Assert.Contains("[Constitution read as the Fortitude save]", output.Result.Narrative);
        // Trained Fortitude at level 3: CON +2, plus level 3 + 2. The old bare-ability path rolled +2.
        Assert.Equal(7, Assert.Single(rolls.RecordedRequests).Bonus);
    }

    [Fact]
    public async Task Pf2e_OmittedSave_Fails()
    {
        var rolls = new FakeRollService();
        var resolver = new Pf2eRulesetResolver(rolls);
        var actor = new Character { Id = "chars/a", Name = "A", SystemStats = new Pf2eExtension { Level = 1 } };

        var output = await resolver.ResolveAsync(Context(actor), new RulesetAction
        {
            CharacterId = actor.Id,
            ActionType = RulesetActionType.SavingThrow,
            ActionName = "Poison",
            Parameters = new Dictionary<string, string> { ["dc"] = "15" },
        }, TestContext.Current.CancellationToken);

        Assert.False(output.Result.Success);
        Assert.Contains("Fortitude, Reflex, Will", output.Result.Narrative);
        Assert.Empty(rolls.RecordedRequests);
    }

    private static ChangeContext Context(params Character[] characters) =>
        new(
            sessionForTests: null,
            characters: characters.ToDictionary(c => c.Id),
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: "save-names");
}
