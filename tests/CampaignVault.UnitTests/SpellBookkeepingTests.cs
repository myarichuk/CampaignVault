using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

/// <summary>Phase 7 of the mechanics remediation: spellcasting bookkeeping (M-60 slot pairing, M-61 known and prepared, M-62 automatic concentration).</summary>
[Collection("RavenDB")]
public class SpellBookkeepingTests(RavenDBFixture fixture) : IClassFixture<RavenDBFixture>
{
    private readonly string _campaign = "test-campaign-" + Guid.NewGuid().ToString("N")[..8];
    private readonly CampaignDocumentKeys _keys = new();
    private static readonly Assembly Assembly = typeof(SpellDefinitionProvider).Assembly;

    private RulesetActionHandler Handler()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv_spell_book_test_" + Guid.NewGuid());
        var roll = Substitute.For<IRollService>();
        IRulesetModule[] modules = [new Dnd5eRulesetResolver(roll), new Pf2eRulesetResolver(roll), new NarrativeRulesetResolver(roll)];
        return new RulesetActionHandler(new RulesetModuleSelector(modules), _keys, new SpellDefinitionProvider(dir, Assembly), new FeatDefinitionProvider(dir, Assembly));
    }

    private async Task<Raven.Client.Documents.Session.IAsyncDocumentSession> SessionAsync(Dictionary<string, string>? options = null)
    {
        var session = fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new CampaignConfig { Id = _keys.Config(_campaign), ActiveSystem = RulesetSystem.Dnd5e, SystemOptions = options ?? [] });
        await session.SaveChangesAsync();
        return session;
    }

    private readonly List<string> _summary = [];

    private ChangeContext Context(Raven.Client.Documents.Session.IAsyncDocumentSession session, Character caster, IReadOnlyList<WorldChange>? batch)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv_spell_book_cond_" + Guid.NewGuid());
        var dispatcher = new WorldChangeDispatcher([new StatusChangeHandler(new ConditionDefinitionProvider(dir, Assembly))], _keys, NullLogger<WorldChangeDispatcher>.Instance);
        var context = new ChangeContext(
            session, new Dictionary<string, Character> { [caster.Id] = caster }, new Dictionary<string, Item>(), new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(), new Dictionary<string, Quest>(), NullLogger.Instance, _summary, dispatcher, null, null, _campaign)
        {
            Batch = batch,
        };
        return context;
    }

    private static Character Wizard(Action<Dnd5eExtension>? tweak = null)
    {
        var stats = new Dnd5eExtension
        {
            ResourcePools = new Dictionary<string, ResourcePool> { ["spell_slots_1"] = new() { Current = 2, Max = 2 }, ["spell_slots_3"] = new() { Current = 2, Max = 2 } },
        };
        tweak?.Invoke(stats);
        return new Character { Id = "chars/wizard", Name = "Mira", IsPc = true, SystemStats = stats };
    }

    private static RulesetAction Cast(string spell, params (string Key, string Value)[] extra)
    {
        var action = new RulesetAction
        {
            CharacterId = "chars/wizard", ActionType = RulesetActionType.Spell, ActionName = spell,
            Parameters = new Dictionary<string, string> { ["resolution"] = "utility" },
        };
        foreach (var (k, v) in extra)
        {
            action.Parameters[k] = v;
        }

        return action;
    }

    private static ResourceChange Spend(string pool = "spell_slots_3", string character = "chars/wizard") =>
        new() { CharacterId = character, PoolName = pool, Delta = -1 };

    private async Task<ChangeHandlerResult> CastAsync(Character caster, RulesetAction action, IReadOnlyList<WorldChange>? batch, Dictionary<string, string>? options = null)
    {
        using var session = await SessionAsync(options);
        return await Handler().ApplyAsync(action, Context(session, caster, batch), TestContext.Current.CancellationToken);
    }

    // ---- M-60: unpaired slot spend ----

    [Fact]
    public async Task M60_ALeveledCastWithNoSlotSpendInTheBatch_IsRefused()
    {
        var action = Cast("fireball");

        var result = await CastAsync(Wizard(), action, [action]);

        Assert.False(result.Success);
        Assert.Contains("SpellBookkeeping", result.Message);
        Assert.Contains("spell_slots_3", result.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task M60_TheSlotSpendCountsWhetherItComesBeforeOrAfterTheCast(bool spendFirst)
    {
        var action = Cast("fireball");
        WorldChange[] batch = spendFirst ? [Spend(), action] : [action, Spend()];

        var result = await CastAsync(Wizard(), action, batch);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task M60_ASpendForAnotherCharacter_DoesNotPairTheCast()
    {
        var action = Cast("fireball");

        var result = await CastAsync(Wizard(), action, [action, Spend(character: "chars/someone-else")]);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task M60_ACantrip_NeedsNoSlot()
    {
        var action = Cast("fire_bolt");

        var result = await CastAsync(Wizard(), action, [action]);

        Assert.True(result.Success, result.Message);
    }

    [Theory]
    [InlineData("ritual")]
    [InlineData("innate")]
    [InlineData("item")]
    [InlineData("focus")]
    public async Task M60_TheExemptionParameters_SkipThePairing(string key)
    {
        var action = Cast("fireball", (key, "true"));

        var result = await CastAsync(Wizard(), action, [action]);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task M60_ACasterWithNoSlotPools_IsNotChecked()
    {
        var action = Cast("fireball");

        var result = await CastAsync(Wizard(s => s.ResourcePools = []), action, [action]);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task M60_WithNoBatch_NothingIsCheckedBecauseTheEngineCannotSeeOne()
    {
        var result = await CastAsync(Wizard(), Cast("fireball"), batch: null);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task M60_ASpellTheEngineHasNoDefinitionFor_IsNeverChecked()
    {
        var action = Cast("my_homebrew_blast");

        var result = await CastAsync(Wizard(), action, [action]);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task M60_WarnMode_CastsAndSaysWhatWasMissing()
    {
        var action = Cast("fireball");
        using var session = await SessionAsync(new Dictionary<string, string> { ["spellBookkeeping"] = "warn" });
        var context = Context(session, Wizard(), [action]);

        var result = await Handler().ApplyAsync(action, context, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.Contains(_summary, m => m.Contains("SpellBookkeeping WARNING"));
    }

    // ---- M-61: known and prepared ----

    [Fact]
    public async Task M61_AKnownCasterCastingASpellItDoesNotKnow_IsRefused()
    {
        var action = Cast("fireball");
        var caster = Wizard(s => s.Spells.Known = ["magic_missile"]);

        var result = await CastAsync(caster, action, [Spend(), action]);

        Assert.False(result.Success);
        Assert.Contains("doesn't know", result.Message);
    }

    [Fact]
    public async Task M61_ASpellOnTheKnownList_IsAllowedWhateverTheSpelling()
    {
        var action = Cast("Fireball");
        var caster = Wizard(s => s.Spells.Known = ["fireball"]);

        var result = await CastAsync(caster, action, [Spend(), action]);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task M61_AKnownButUnpreparedSpell_IsRefusedForAPreparedCaster()
    {
        var action = Cast("fireball");
        var caster = Wizard(s => { s.Spells.Known = ["fireball", "magic_missile"]; s.Spells.Prepared = ["magic_missile"]; });

        var result = await CastAsync(caster, action, [Spend(), action]);

        Assert.False(result.Success);
        Assert.Contains("hasn't prepared", result.Message);
    }

    [Fact]
    public async Task M61_APreparedSpell_IsAllowed()
    {
        var action = Cast("magic_missile");
        var caster = Wizard(s => { s.Spells.Known = ["fireball", "magic_missile"]; s.Spells.Prepared = ["magic_missile"]; });

        var result = await CastAsync(caster, action, [Spend("spell_slots_1"), action]);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task M61_ACantripTheCasterLacks_IsRefused()
    {
        var action = Cast("fire_bolt");
        var caster = Wizard(s => s.Spells.Cantrips = ["prestidigitation"]);

        var result = await CastAsync(caster, action, [action]);

        Assert.False(result.Success);
        Assert.Contains("cantrip", result.Message);
    }

    [Fact]
    public async Task M61_ARitualOrInnateCast_SkipsTheListCheck()
    {
        var action = Cast("fireball", ("ritual", "true"));
        var caster = Wizard(s => s.Spells.Known = ["magic_missile"]);

        var result = await CastAsync(caster, action, [action]);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task M61_ACasterWithNothingRecorded_IsNotChecked()
    {
        var action = Cast("fireball");

        var result = await CastAsync(Wizard(), action, [Spend(), action]);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task M61_WarnMode_LetsAnUnknownSpellThroughWithAMessage()
    {
        var action = Cast("fireball");
        using var session = await SessionAsync(new Dictionary<string, string> { ["SpellBookkeeping"] = "WARN" });
        var context = Context(session, Wizard(s => s.Spells.Known = ["magic_missile"]), [Spend(), action]);

        var result = await Handler().ApplyAsync(action, context, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.Contains(_summary, m => m.Contains("doesn't know"));
    }

    // ---- M-62: automatic concentration ----

    private static bool Concentrating(Character c) =>
        c.SystemStats!.StatusEffects.Any(ConcentrationRules.IsConcentration);

    [Fact]
    public async Task M62_CastingAConcentrationSpell_MarksTheCasterAsConcentrating()
    {
        var action = Cast("bless");
        var caster = Wizard();

        var result = await CastAsync(caster, action, [Spend("spell_slots_1"), action]);

        Assert.True(result.Success, result.Message);
        var effect = Assert.Single(caster.SystemStats!.StatusEffects, ConcentrationRules.IsConcentration);
        Assert.Contains("Bless", effect.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task M62_ANewConcentrationCast_BreaksTheEarlierOne()
    {
        var caster = Wizard(s => s.StatusEffects.Add(new StatusEffect { Name = "Concentration: Detect Magic", Category = "Concentration", ConditionName = "concentration" }));
        var action = Cast("bless");

        await CastAsync(caster, action, [Spend("spell_slots_1"), action]);

        var effect = Assert.Single(caster.SystemStats!.StatusEffects, ConcentrationRules.IsConcentration);
        Assert.Contains("Bless", effect.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task M62_AManualConcentrationStatusInTheSameBatch_IsNotDoubled()
    {
        var action = Cast("bless");
        var manual = new StatusChange
        {
            CharacterId = "chars/wizard",
            Effect = new StatusEffect { Name = "Concentration: Bless", Category = "Concentration", ConditionName = "concentration" },
        };
        var caster = Wizard();

        await CastAsync(caster, action, [Spend("spell_slots_1"), action, manual]);

        // The manual status is applied by its own handler later in a real batch; here only the cast ran, so the engine added none.
        Assert.False(Concentrating(caster));
    }

    [Fact]
    public async Task M62_ASpellWithoutConcentration_AddsNothing()
    {
        var action = Cast("magic_missile");
        var caster = Wizard();

        await CastAsync(caster, action, [Spend("spell_slots_1"), action]);

        Assert.False(Concentrating(caster));
    }

    [Fact]
    public void M62_Pf2eSpellsAreLeftOut_BecauseTheDataFlagsTheConcentrateTraitWithTheSameField()
    {
        var spell = new CampaignVault.Data.Templates.SpellDefinition { Name = "acid_grip", System = RulesetSystem.Pathfinder2e, Level = 2, Concentration = true };
        var pf2e = new Character { Id = "p", SystemStats = new Pf2eExtension() };

        Assert.False(SpellBookkeeping.AppliesConcentration(pf2e, spell));
        Assert.True(SpellBookkeeping.AppliesConcentration(new Character { Id = "d", SystemStats = new Dnd5eExtension() }, spell));
    }
}
