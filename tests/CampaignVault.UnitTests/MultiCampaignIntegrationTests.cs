using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class MultiCampaignIntegrationTests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;
    private readonly RavenDBFixture _fixture;

    public MultiCampaignIntegrationTests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
        _fixture = fixture;
    }

    private CampaignTools CreateTools() => TestCampaignToolsFactory.Create(_fixture);

    [Fact]
    public async Task CreateCampaign_Works_WithExplicitName()
    {
        var tools = CreateTools();
        var slug = "brand-new-world-" + Guid.NewGuid().ToString("N")[..8];

        var createResult = await tools.CreateCampaign(slug, RulesetSystem.Dnd5e);

        Assert.True(createResult.Success);
        Assert.NotNull(createResult.Data);
        Assert.Equal(slug, createResult.Data.Name);
    }

    [Fact]
    public async Task CreateCampaign_ThenGetCurrent_Works()
    {
        var tools = CreateTools();
        var slug = "brand-new-world-" + Guid.NewGuid().ToString("N")[..8];

        await tools.CreateCampaign(slug, RulesetSystem.Dnd5e);

        var currentResult = await tools.GetCurrentCampaign(slug);
        Assert.True(currentResult.Success);
        Assert.NotNull(currentResult.Data);
        Assert.Equal(slug, currentResult.Data.Campaign.Name);
    }

    [Fact]
    public async Task UpsertCharacter_BeforeCreateCampaign_PersistsDnd5eConfigAndWarnsInSummary()
    {
        var tools = CreateTools();
        var slug = "out-of-order-world-" + Guid.NewGuid().ToString("N")[..8];
        var charId = "chars/" + Guid.NewGuid().ToString("N")[..8];

        var upsertResult = await tools.UpsertCharacter(
            new CharacterUpsertRequest { Id = charId, Name = "Early Bird" }, slug);

        Assert.True(upsertResult.Success);
        Assert.Contains("No campaign ruleset is configured yet", upsertResult.Summary);
        Assert.Contains("dnd5e", upsertResult.Summary);

        var configResult = await tools.GetConfig(slug);
        Assert.True(configResult.Success);
        Assert.Equal(RulesetSystem.Dnd5e, configResult.Data!.ActiveSystem);
    }

    [Fact]
    public async Task CreateCampaign_AfterEarlyUpsertCharacter_StillSucceedsAndCorrectsConfig()
    {
        var tools = CreateTools();
        var slug = "out-of-order-world-" + Guid.NewGuid().ToString("N")[..8];
        var charId = "chars/" + Guid.NewGuid().ToString("N")[..8];

        // Out-of-order: character created before the campaign is formally established.
        await tools.UpsertCharacter(new CharacterUpsertRequest { Id = charId, Name = "Early Bird" }, slug);

        var createResult = await tools.CreateCampaign(slug, RulesetSystem.Pathfinder2e);
        Assert.True(createResult.Success, $"create_campaign should still succeed: {createResult.Summary}");
        Assert.Equal(RulesetSystem.Pathfinder2e, createResult.Data!.System);

        // The config document, which is what character bootstrap actually reads, must now agree
        // with the locked Campaign.System rather than being stuck at the earlier implicit Dnd5e default.
        var configResult = await tools.GetConfig(slug);
        Assert.True(configResult.Success);
        Assert.Equal(RulesetSystem.Pathfinder2e, configResult.Data!.ActiveSystem);
    }

    [Fact]
    public async Task CreateCampaign_AfterEarlyGetWorldState_AdoptsPhantomMetaInsteadOfRefusing()
    {
        var repo = _fixture.CreateRepository();
        var exploration = TestCampaignToolsFactory.CreateTool<ExplorationTools>(_fixture, repo);
        var management = TestCampaignToolsFactory.CreateTool<CampaignManagementTools>(_fixture, repo);
        var slug = "phantom-world-" + Guid.NewGuid().ToString("N")[..8];

        // Out-of-order: a read tool (get_world_state, which underlies get_session_briefing) is
        // called against a slug that was never created via create_campaign. This auto-vivifies
        // bare Campaign/CampaignConfig/CampaignTime docs as a side effect of the read.
        var earlyRead = await exploration.GetWorldState(campaignName: slug);
        Assert.True(earlyRead.Success);

        // Regression: create_campaign used to reject this with "AlreadyExists" forever, because the
        // phantom Campaign meta doc already existed — permanently blocking real creation for the slug.
        var createResult = await management.CreateCampaign(slug, RulesetSystem.Pathfinder2e, loreYear: 900);
        Assert.True(createResult.Success, $"create_campaign should adopt the phantom instead of refusing: {createResult.Summary}");
        Assert.Equal(RulesetSystem.Pathfinder2e, createResult.Data!.System);
        Assert.True(createResult.Data.IsSystemLocked);
        Assert.Equal(900, createResult.Data.LoreSettings.Year);

        // The config doc (what bootstrap actually reads) must agree with the now-locked system.
        var configResult = await management.GetConfig(slug);
        Assert.True(configResult.Success);
        Assert.Equal(RulesetSystem.Pathfinder2e, configResult.Data!.ActiveSystem);

        // A second create_campaign call for the now-real, locked campaign must still be refused.
        var secondCreate = await management.CreateCampaign(slug, RulesetSystem.Dnd5e);
        Assert.False(secondCreate.Success);
        Assert.Equal("AlreadyExists", secondCreate.Error);

        // The served WorldState.Time must reflect the reseeded year, not just Campaign.LoreSettings.
        var worldStateAfter = await exploration.GetWorldState(campaignName: slug);
        Assert.True(worldStateAfter.Success);
        Assert.Equal(900, worldStateAfter.Data!.Time.Year);
    }

    [Fact]
    public async Task CreateCampaign_FreshSlug_WorldStateReflectsLoreYear()
    {
        var repo = _fixture.CreateRepository();
        var exploration = TestCampaignToolsFactory.CreateTool<ExplorationTools>(_fixture, repo);
        var management = TestCampaignToolsFactory.CreateTool<CampaignManagementTools>(_fixture, repo);
        var slug = "fresh-year-world-" + Guid.NewGuid().ToString("N")[..8];

        var createResult = await management.CreateCampaign(slug, RulesetSystem.Dnd5e, loreYear: 250);
        Assert.True(createResult.Success);
        Assert.Equal(250, createResult.Data!.LoreSettings.Year);

        var worldState = await exploration.GetWorldState(campaignName: slug);
        Assert.True(worldState.Success);
        Assert.Equal(250, worldState.Data!.Time.Year);
    }

    [Fact]
    public async Task UpsertCharacter_OnExistingId_WarnsInSummary_InsteadOfSilentOverwrite()
    {
        var tools = CreateTools();
        var slug = "collision-world-" + Guid.NewGuid().ToString("N")[..8];
        var charId = "chars/" + Guid.NewGuid().ToString("N")[..8];

        var firstResult = await tools.UpsertCharacter(
            new CharacterUpsertRequest { Id = charId, Name = "Original", MaxHp = 20, CurrentHp = 20 }, slug);
        Assert.True(firstResult.Success);
        Assert.DoesNotContain("already existed", firstResult.Summary);

        var secondResult = await tools.UpsertCharacter(
            new CharacterUpsertRequest { Id = charId, Name = "Sparse Re-Upsert" }, slug);

        Assert.True(secondResult.Success);
        Assert.Contains("already existed and was merged/overwritten", secondResult.Summary);
        Assert.Contains(charId, secondResult.Summary);
    }

    [Fact]
    public async Task UpsertCharacter_WithNonexistentCurrentLocationId_WarnsButSucceeds()
    {
        var tools = CreateTools();
        var slug = "dangling-ref-world-" + Guid.NewGuid().ToString("N")[..8];
        var charId = "chars/" + Guid.NewGuid().ToString("N")[..8];

        var result = await tools.UpsertCharacter(
            new CharacterUpsertRequest { Id = charId, Name = "Wanderer", CurrentLocationId = "locations/ghost-town" },
            slug);

        Assert.True(result.Success);
        Assert.Contains("currentLocationId 'locations/ghost-town' does not currently exist", result.Summary);
    }

    [Fact]
    public async Task UpsertQuest_WithNonexistentGiverAndRelatedIds_WarnsButSucceeds()
    {
        var repo = _fixture.CreateRepository();
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture, repo);
        var slug = "dangling-ref-world-" + Guid.NewGuid().ToString("N")[..8];
        var questId = "quests/" + Guid.NewGuid().ToString("N")[..8];

        var result = await worldBuilder.UpsertQuest(new QuestUpsertRequest
        {
            Id = questId,
            Title = "Find the Ghost Giver",
            GiverId = "chars/nonexistent-giver",
            RelatedFactionIds = ["factions/nonexistent-faction"]
        }, slug);

        Assert.True(result.Success);
        Assert.Contains("giverId='chars/nonexistent-giver'", result.Summary);
        Assert.Contains("relatedFactionIds[0]='factions/nonexistent-faction'", result.Summary);
    }

    [Fact]
    public async Task UpsertQuest_WithExistingReferences_NoWarning()
    {
        var repo = _fixture.CreateRepository();
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture, repo);
        var slug = "dangling-ref-world-" + Guid.NewGuid().ToString("N")[..8];
        var giverId = "chars/" + Guid.NewGuid().ToString("N")[..8];
        var questId = "quests/" + Guid.NewGuid().ToString("N")[..8];

        await worldBuilder.UpsertCharacter(new CharacterUpsertRequest { Id = giverId, Name = "Real Giver" }, slug);
        var result = await worldBuilder.UpsertQuest(
            new QuestUpsertRequest { Id = questId, Title = "Real Quest", GiverId = giverId }, slug);

        Assert.True(result.Success);
        Assert.DoesNotContain("WARNING", result.Summary);
    }

    [Fact]
    public async Task LockIn_RejectionPath_PreventsRulesetChange()
    {
        var tools = CreateTools();

        await TestCampaignDefaults.EnsureExistsAsync(tools, "locked-world");

        // Setup initial campaign config with Dnd5e
        var configResult = await tools.SetActiveSystem(RulesetSystem.Dnd5e, null, "locked-world");
        Assert.True(configResult.Success);

        // Try to change to Pathfinder2e, should fail due to lock
        var changeResult = await tools.SetActiveSystem(RulesetSystem.Pathfinder2e, null, "locked-world");
        Assert.False(changeResult.Success);
        Assert.Equal("SystemLocked", changeResult.Error);
    }

    [Fact]
    public async Task IndependentCampaigns_MaintainSeparateConfigsAndCombat()
    {
        var tools = CreateTools();
        var repo = _fixture.CreateRepository();

        // Upsert characters with explicit campaign for scoping (no BC for legacy needed)
        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, "campaign-a"), new CharacterUpsertRequest
                    { Id = "chars/char-1", Name = "Char 1", CurrentHp = 10, MaxHp = 10, KeepAlive = true });
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, "campaign-b"), new CharacterUpsertRequest
                    { Id = "chars/char-2", Name = "Char 2", CurrentHp = 10, MaxHp = 10, KeepAlive = true });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Setup Campaign A (D&D 5e)
        await TestCampaignDefaults.EnsureExistsAsync(tools, "campaign-a");
        await tools.SetActiveSystem(RulesetSystem.Dnd5e, null, "campaign-a");
        await tools.StartCombat("loc-1", ["chars/char-1"], "campaign-a");

        // Setup Campaign B (Pathfinder 2e)
        var createB = await tools.CreateCampaign("campaign-b", RulesetSystem.Pathfinder2e);
        Assert.True(createB.Success);
        await tools.StartCombat("loc-2", ["chars/char-2"], "campaign-b");

        // Verify Campaign B
        var configB = await tools.GetConfig("campaign-b");
        Assert.NotNull(configB.Data);
        Assert.Equal(RulesetSystem.Pathfinder2e, configB.Data.ActiveSystem);
        var combatB = await tools.NextTurn(null, "campaign-b");
        Assert.NotNull(combatB.Data);
        Assert.Equal("chars/char-2", combatB.Data.Combatants[0].CharacterId);

        // Switch back to Campaign A (explicit campaignName — no session selection)
        var configA = await tools.GetConfig("campaign-a");
        Assert.NotNull(configA.Data);
        Assert.Equal(RulesetSystem.Dnd5e, configA.Data.ActiveSystem);
        var combatA = await tools.NextTurn(null, "campaign-a");
        Assert.NotNull(combatA.Data);
        Assert.Equal("chars/char-1", combatA.Data.Combatants[0].CharacterId);

        // === Scoping hardening verification (per plan + code_review.md) ===
        // Set high need on both for pressure test (loose filter for shareables, but here per-camp)
        using (var session = _store.OpenAsyncSession())
        {
            var cfgA = await session.LoadAsync<CampaignConfig>(new CampaignDocumentKeys().Config("campaign-a"), TestContext.Current.CancellationToken);
            if (cfgA != null)
            {
                cfgA.MaxPressuresPerResponse = 50;
            }

            var cfgB = await session.LoadAsync<CampaignConfig>(new CampaignDocumentKeys().Config("campaign-b"), TestContext.Current.CancellationToken);
            if (cfgB != null)
            {
                cfgB.MaxPressuresPerResponse = 50;
            }

            var c1 = await session.LoadAsync<Character>("chars/char-1", TestContext.Current.CancellationToken);
            c1.Needs.ActiveNeeds["hunger"] = 95f; // triggers pressure for A
            var c2 = await session.LoadAsync<Character>("chars/char-2", TestContext.Current.CancellationToken);
            c2.Needs.ActiveNeeds["hunger"] = 95f; // triggers for B
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Verify scoping set during upsert
            Assert.Equal("campaign-a", c1.CampaignName);
            Assert.Equal("campaign-b", c2.CampaignName);
        }

        // Pressures for camp A should include char-1, not char-2
        var wsA = await tools.GetWorldState("loc-1", "campaign-a");
        Assert.True(wsA.Success, $"GetWorldState failed: {wsA.Error} / {wsA.Summary}");
        var pressureTextA = string.Join(" | ", wsA.Data?.WorldPressure ?? []);
        Assert.Contains("Char 1", pressureTextA);
        Assert.DoesNotContain("Char 2", pressureTextA);

        // Direct pressure for B via contributor (to debug ws)
        using (var ps = _store.OpenAsyncSession())
        {
            var time = await repo.GetTimeAsync(_fixture.CreateCampaignSession(ps, "campaign-b"));
            var config = await repo.GetCampaignConfigAsync(_fixture.CreateCampaignSession(ps, "campaign-b"));
            var contributor = new CampaignVault.Data.Pressure.Contributors.CharacterDistressPressureContributor();
            var ctx = new CampaignVault.Data.Pressure.PressureContext("campaign-b", time, config, ps);
            var dps = await contributor.EvaluateAsync(ctx, TestContext.Current.CancellationToken);
            var dpText = string.Join(" | ", dps.Select(p => p.Text));
            Assert.Contains("Char 2", dpText);
            Assert.DoesNotContain("Char 1", dpText);
        }

        // Pressures for camp B should include char-2 (if high), not char-1
        var wsB = await tools.GetWorldState("loc-2", "campaign-b");
        var pressureTextB = string.Join(" | ", wsB.Data?.WorldPressure ?? []);
        Assert.Contains("Char 2", pressureTextB);
        Assert.DoesNotContain("Char 1", pressureTextB);

        // Sim scoping: add schedules to both, set needs, advance only A, verify only A affected
        using (var session = _store.OpenAsyncSession())
        {
            var c1 = await session.LoadAsync<Character>("chars/char-1", TestContext.Current.CancellationToken);
            c1.Schedule = new Schedule { DefaultLocationId = "loc-1", Routines = [] };
            c1.Needs.ActiveNeeds["tiredness"] = 50f;
            var c2 = await session.LoadAsync<Character>("chars/char-2", TestContext.Current.CancellationToken);
            c2.Schedule = new Schedule { DefaultLocationId = "loc-2", Routines = [] };
            c2.Needs.ActiveNeeds["tiredness"] = 50f;
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await tools.AdvanceWorld(1, 9, "Advance A only", "campaign-a");

        using (var verify = _store.OpenAsyncSession())
        {
            var c1After = await verify.LoadAsync<Character>("chars/char-1", TestContext.Current.CancellationToken);
            var c2After = await verify.LoadAsync<Character>("chars/char-2", TestContext.Current.CancellationToken);
            // A should have changed (needs accumulation or schedule), B should not (or at least test no cross)
            // Since needs rule runs, tiredness may increase for scheduled; check A was processed
            // A may or may not have changed depending on rules/time (not strict for this test); main is B untouched by A advance
            // B should remain untouched by A's advance
            Assert.Equal(50f, c2After.Needs.ActiveNeeds["tiredness"]);
        }
    }

    [Fact]
    public async Task TwoMcpSessions_UseExplicitCampaignName_Independently()
    {
        var toolsA = TestCampaignToolsFactory.Create(_fixture);
        var toolsB = TestCampaignToolsFactory.Create(_fixture);
        var slugA = "session-a-" + Guid.NewGuid().ToString("N")[..8];
        var slugB = "session-b-" + Guid.NewGuid().ToString("N")[..8];

        await TestCampaignDefaults.EnsureExistsAsync(toolsA, slugA);
        await TestCampaignDefaults.EnsureExistsAsync(toolsB, slugB);

        var currentA = await toolsA.GetCurrentCampaign(slugA);
        var currentB = await toolsB.GetCurrentCampaign(slugB);

        Assert.True(currentA.Success);
        Assert.True(currentB.Success);
        Assert.Equal(slugA, currentA.Data!.Campaign.Name);
        Assert.Equal(slugB, currentB.Data!.Campaign.Name);
    }

    [Theory]
    [InlineData("1492 DR", 1492, "DR")]
    [InlineData("the Age of Dragons, year 20", 20, null)]
    [InlineData("present day", 1492, null)]
    [InlineData("Third Age", 1492, "Third Age")]
    public async Task FinalizeOnboarding_StartingEraAnswer_ParsesIntoLoreYearAndReachesWorldState(
        string startingEraAnswer, int expectedYear, string? expectedEpochContains)
    {
        var repo = _fixture.CreateRepository();
        var onboarding = TestCampaignToolsFactory.CreateTool<OnboardingTools>(_fixture, repo);
        var exploration = TestCampaignToolsFactory.CreateTool<ExplorationTools>(_fixture, repo);
        var slug = "onboard-era-" + Guid.NewGuid().ToString("N")[..8];

        var start = await onboarding.StartCampaignOnboarding(slug);
        Assert.True(start.Success);

        // Answer questions in whatever order the catalog presents them until ready to finalize,
        // giving the StartingEra question our test value and generic-but-valid answers otherwise.
        OnboardingQuestion? current = start.Data!.CurrentQuestion;
        var guard = 0;
        while (current != null)
        {
            Assert.True(++guard < 30, "Onboarding question loop did not terminate.");

            string answer = current.Key switch
            {
                OnboardingQuestionCatalog.StartingEra => startingEraAnswer,
                OnboardingQuestionCatalog.System => "Dnd5e",
                OnboardingQuestionCatalog.WorldSetting => "solo",
                OnboardingQuestionCatalog.Party => BuildAtTableParty,
                OnboardingQuestionCatalog.PlotSource => "generated-surprise",
                OnboardingQuestionCatalog.SideQuestGeneration => "on-the-fly",
                _ when current.AnswerType == OnboardingAnswerType.Enum => current.EnumOptions![0],
                _ when current.AnswerType == OnboardingAnswerType.Number => "3",
                _ => "A reasonable free-text answer for this question."
            };

            var submit = await onboarding.SubmitOnboardingAnswer(slug, answer);
            Assert.True(submit.Success, submit.Summary);
            current = submit.Data!.CurrentQuestion;
        }

        var finalize = await onboarding.FinalizeCampaignOnboarding(slug);
        Assert.True(finalize.Success, finalize.Summary);

        // When the starting-era answer had no digit, finalize must surface that the year was
        // defaulted rather than silently dropping the user's intent.
        var noDigit = !Regex.IsMatch(startingEraAnswer, @"\d");
        Assert.Equal(noDigit, finalize.Data!.NextSteps.Any(s => s.Contains("No numeric year found")));

        var worldState = await exploration.GetWorldState(campaignName: slug);
        Assert.True(worldState.Success);
        Assert.Equal(expectedYear, worldState.Data!.Time.Year);
        if (expectedEpochContains != null)
        {
            Assert.Contains(expectedEpochContains, worldState.Data.Time.Epoch);
        }

        // The formatted date should always be a usable sentence carrying the same Year.
        Assert.Contains($"Year {expectedYear}", worldState.Data.Time.FormattedDate);
    }

    private const string BuildAtTableParty = "{\"mode\":\"build-at-table\",\"level\":1}";

    private async Task<string> StorePcAsync(string slug, string name, bool isPc = true)
    {
        var id = $"chars/{slug}-{name.ToLowerInvariant()}";
        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(new Character { Id = id, Name = name, IsPc = isPc, IsPartyCompanion = !isPc, CampaignName = slug });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    [Theory]
    [InlineData("party-homebrew", OnboardingPartyAnswer.ModeBuildNow, true)]
    [InlineData("party-existing", OnboardingPartyAnswer.ModeDmDrafts, true)]
    [InlineData("solo", OnboardingPartyAnswer.ModeBuildAtTable, false)]
    public async Task Onboarding_EveryPath_AsksAboutTheWorldAndPlayerCharacters(
        string worldSetting, string mode, bool expectPartyQuestion)
    {
        var repo = _fixture.CreateRepository();
        var onboarding = TestCampaignToolsFactory.CreateTool<OnboardingTools>(_fixture, repo);
        var sessions = TestCampaignToolsFactory.CreateTool<SessionTools>(_fixture, repo);
        var slug = "onboard-path-" + Guid.NewGuid().ToString("N")[..8];

        // build-now arrives with built characters; dm-drafts with drafts the player already committed.
        var lyra = mode == OnboardingPartyAnswer.ModeBuildAtTable ? null : await StorePcAsync(slug, "Lyra");
        // build-now also brings a companion the player built (or reviewed from the DM's drafts).
        var brann = mode == OnboardingPartyAnswer.ModeBuildNow ? await StorePcAsync(slug, "Brann", isPc: false) : null;
        var party = lyra == null
            ? $"{{\"mode\":\"{mode}\",\"level\":3}}"
            : brann != null
                ? $"{{\"mode\":\"{mode}\",\"level\":3,\"characterIds\":[\"{lyra}\"],\"companionIds\":[\"{brann}\"]}}"
                : $"{{\"mode\":\"{mode}\",\"level\":3,\"characterIds\":[\"{lyra}\"]}}";

        var start = await onboarding.StartCampaignOnboarding(slug);
        Assert.True(start.Success);

        var asked = new List<string>();
        OnboardingQuestion? current = start.Data!.CurrentQuestion;
        while (current != null)
        {
            Assert.True(asked.Count < 30, "Onboarding question loop did not terminate.");
            asked.Add(current.Key);
            string answer = current.Key switch
            {
                OnboardingQuestionCatalog.System => "Dnd5e",
                OnboardingQuestionCatalog.WorldSetting => worldSetting,
                OnboardingQuestionCatalog.Party => party,
                OnboardingQuestionCatalog.PlotSource => "user-provided",
                OnboardingQuestionCatalog.PlotDirection => "A stolen crown and a drowned city",
                OnboardingQuestionCatalog.OpeningScene => "The docks of Saltmere at dawn",
                _ when current.AnswerType == OnboardingAnswerType.Enum => current.EnumOptions![0],
                _ => "A reasonable free-text answer for this question."
            };
            var submit = await onboarding.SubmitOnboardingAnswer(slug, answer);
            Assert.True(submit.Success, submit.Summary);
            current = submit.Data!.CurrentQuestion;
        }

        Assert.Contains(OnboardingQuestionCatalog.HomebrewWorldDetails, asked);
        Assert.Contains(OnboardingQuestionCatalog.Party, asked);
        Assert.Contains(OnboardingQuestionCatalog.PlotDirection, asked);
        Assert.Contains(OnboardingQuestionCatalog.OpeningScene, asked);
        Assert.Equal(expectPartyQuestion, asked.Contains(OnboardingQuestionCatalog.PartyComposition));
        // The party step replaced these three, and solo companions live inside it.
        Assert.DoesNotContain(OnboardingQuestionCatalog.PcCreation, asked);
        Assert.DoesNotContain(OnboardingQuestionCatalog.PcRoster, asked);
        Assert.DoesNotContain(OnboardingQuestionCatalog.StartingLevel, asked);
        Assert.DoesNotContain(OnboardingQuestionCatalog.SoloCompanions, asked);
        // The plot follows the world directly, before any character questions.
        Assert.Equal(asked.IndexOf(OnboardingQuestionCatalog.HomebrewWorldDetails) + 1, asked.IndexOf(OnboardingQuestionCatalog.PlotSource));
        Assert.True(asked.IndexOf(OnboardingQuestionCatalog.PlotDirection) < asked.IndexOf(OnboardingQuestionCatalog.Party));

        var finalize = await onboarding.FinalizeCampaignOnboarding(slug);
        Assert.True(finalize.Success, finalize.Summary);
        var brief = finalize.Data!.SeedBrief;
        Assert.Contains("Starting level: 3", brief);
        Assert.Contains("The docks of Saltmere at dawn", brief);
        Assert.Contains("A stolen crown and a drowned city", brief);
        if (lyra != null)
        {
            // Pre-built characters are listed by id and must not be world_built again.
            Assert.Contains(lyra, brief);
            Assert.Contains("already built", brief);
            Assert.Contains("Do NOT world_build", brief);
            Assert.DoesNotContain("isPc=true", brief);
            if (brann != null)
            {
                // Built companions are listed and must not be world_built again either.
                Assert.Contains(brann, brief);
                Assert.Contains("The companions are already built too", brief);
            }
            else
            {
                Assert.DoesNotContain("companions are already built", brief);
            }
        }
        else
        {
            Assert.Contains("isPc=true", brief);
            Assert.Contains("walk the player through creating", brief);
        }

        // Until the world is seeded, start_session hands the brief back instead of just refusing. Pre-built
        // characters are a party but stand nowhere yet, so that alone doesn't open a session.
        var session = await sessions.StartSession(slug);
        Assert.False(session.Success);
        Assert.Contains("CAMPAIGN SETUP BRIEF", session.Summary);
        if (lyra != null)
        {
            Assert.Contains("no world yet", session.Summary);
            using (var place = _store.OpenAsyncSession())
            {
                var pc = await place.LoadAsync<Character>(lyra, TestContext.Current.CancellationToken);
                pc.CurrentLocationId = "locations/saltmere-docks";
                await place.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var started = await sessions.StartSession(slug);
            Assert.True(started.Success, started.Summary);
        }
    }

    [Fact]
    public async Task Onboarding_DmDraftsWithNoDraftsYet_BriefAsksTheDmToDraftThem()
    {
        var brief = OnboardingBrief.Build("s", "S", "Dnd5e", new Dictionary<string, object>
        {
            [OnboardingQuestionCatalog.Party] = "{\"mode\":\"dm-drafts\",\"level\":2}"
        });
        Assert.Contains("pre-generates", brief);
        Assert.Contains("Invent the player characters", brief);
        Assert.Contains("level 2", brief);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Onboarding_PartyAnswer_RejectsBadShapesAndUnknownIds()
    {
        var question = OnboardingQuestionCatalog.GetQuestionSequence().Single(q => q.Key == OnboardingQuestionCatalog.Party);
        Assert.NotNull(OnboardingQuestionCatalog.ValidateAnswer(question, "build-at-table"));
        Assert.NotNull(OnboardingQuestionCatalog.ValidateAnswer(question, "{\"mode\":\"nope\"}"));
        Assert.NotNull(OnboardingQuestionCatalog.ValidateAnswer(question, "{\"mode\":\"build-now\",\"characterIds\":[]}"));
        Assert.NotNull(OnboardingQuestionCatalog.ValidateAnswer(question, "{\"mode\":\"build-at-table\",\"level\":21}"));
        Assert.NotNull(OnboardingQuestionCatalog.ValidateAnswer(question, "{\"mode\":\"build-at-table\",\"characterIds\":\"x\"}"));
        Assert.Null(OnboardingQuestionCatalog.ValidateAnswer(question, "{\"mode\":\"build-at-table\"}"));
        Assert.Null(OnboardingQuestionCatalog.ValidateAnswer(question, "{\"mode\":\"build-now\",\"level\":20,\"characterIds\":[\"chars/x\"]}"));

        // Shape is fine, but the character does not exist (or is a companion, not a PC).
        var repo = _fixture.CreateRepository();
        var onboarding = TestCampaignToolsFactory.CreateTool<OnboardingTools>(_fixture, repo);
        var slug = "onboard-ids-" + Guid.NewGuid().ToString("N")[..8];
        var dog = await StorePcAsync(slug, "Dog", isPc: false);
        var current = (await onboarding.StartCampaignOnboarding(slug)).Data!.CurrentQuestion;
        while (current is { Key: not OnboardingQuestionCatalog.Party })
        {
            string answer = current.Key switch
            {
                OnboardingQuestionCatalog.System => "Dnd5e",
                OnboardingQuestionCatalog.WorldSetting => "solo",
                OnboardingQuestionCatalog.PlotSource => "generated-surprise",
                _ when current.AnswerType == OnboardingAnswerType.Enum => current.EnumOptions![0],
                _ => "A reasonable free-text answer for this question."
            };
            var step = await onboarding.SubmitOnboardingAnswer(slug, answer);
            Assert.True(step.Success, step.Summary);
            current = step.Data!.CurrentQuestion;
        }

        Assert.NotNull(current);
        var missing = await onboarding.SubmitOnboardingAnswer(slug, "{\"mode\":\"build-now\",\"characterIds\":[\"chars/ghost\"]}");
        Assert.False(missing.Success);
        var companionAsPc = await onboarding.SubmitOnboardingAnswer(slug, $"{{\"mode\":\"build-now\",\"characterIds\":[\"{dog}\"]}}");
        Assert.False(companionAsPc.Success);
        var companionOk = await onboarding.SubmitOnboardingAnswer(slug, $"{{\"mode\":\"build-at-table\",\"companionIds\":[\"{dog}\"]}}");
        Assert.True(companionOk.Success, companionOk.Summary);
    }

    [Fact]
    public async Task Onboarding_AlreadyAnsweredPcCreation_KeepsTheOldQuestionsAndWording()
    {
        var repo = _fixture.CreateRepository();
        var onboarding = TestCampaignToolsFactory.CreateTool<OnboardingTools>(_fixture, repo);
        var slug = "onboard-legacy-" + Guid.NewGuid().ToString("N")[..8];

        var asked = new List<string>();
        OnboardingQuestion? current = (await onboarding.StartCampaignOnboarding(slug)).Data!.CurrentQuestion;
        var answeredLegacy = false;
        while (current != null)
        {
            Assert.True(asked.Count < 30, "Onboarding question loop did not terminate.");
            asked.Add(current.Key);
            if (current.Key == OnboardingQuestionCatalog.Party && !answeredLegacy)
            {
                // The state saved before the party step existed: pc_creation is already in its answers.
                using var session = _store.OpenAsyncSession();
                var state = await repo.GetOnboardingStateAsync(session, slug);
                state!.CollectedAnswers[OnboardingQuestionCatalog.PcCreation] = OnboardingQuestionCatalog.PcCreationDescribeNow;
                state.NextQuestion = OnboardingQuestionCatalog.GetNextQuestion(state);
                await repo.UpsertOnboardingStateAsync(session, state, slug);
                await session.SaveChangesAsync(TestContext.Current.CancellationToken);
                answeredLegacy = true;
                current = state.NextQuestion;
                asked.Remove(OnboardingQuestionCatalog.Party);
                continue;
            }

            string answer = current.Key switch
            {
                OnboardingQuestionCatalog.System => "Dnd5e",
                OnboardingQuestionCatalog.WorldSetting => "solo",
                OnboardingQuestionCatalog.PcRoster => "Lyra — elf ranger, exiled scout",
                OnboardingQuestionCatalog.StartingLevel => "3",
                OnboardingQuestionCatalog.PlotSource => "generated-surprise",
                _ when current.AnswerType == OnboardingAnswerType.Enum => current.EnumOptions![0],
                _ => "A reasonable free-text answer for this question."
            };
            var submit = await onboarding.SubmitOnboardingAnswer(slug, answer);
            Assert.True(submit.Success, submit.Summary);
            current = submit.Data!.CurrentQuestion;
        }

        Assert.DoesNotContain(OnboardingQuestionCatalog.Party, asked);
        Assert.Contains(OnboardingQuestionCatalog.PcRoster, asked);
        Assert.Contains(OnboardingQuestionCatalog.StartingLevel, asked);
        var finalize = await onboarding.FinalizeCampaignOnboarding(slug);
        Assert.True(finalize.Success, finalize.Summary);
        Assert.Contains("described by the player", finalize.Data!.SeedBrief);
        Assert.Contains("Lyra — elf ranger, exiled scout", finalize.Data.SeedBrief);
        Assert.Contains("level 3", finalize.Data.SeedBrief);
    }

    [Fact]
    public async Task Onboarding_Narrative_SkipsStartingLevel_AndBriefStatesNoLevel()
    {
        var repo = _fixture.CreateRepository();
        var onboarding = TestCampaignToolsFactory.CreateTool<OnboardingTools>(_fixture, repo);
        var slug = "onboard-narrative-" + Guid.NewGuid().ToString("N")[..8];

        var start = await onboarding.StartCampaignOnboarding(slug);
        var asked = new List<string>();
        OnboardingQuestion? current = start.Data!.CurrentQuestion;
        while (current != null)
        {
            Assert.True(asked.Count < 30, "Onboarding question loop did not terminate.");
            asked.Add(current.Key);
            string answer = current.Key switch
            {
                OnboardingQuestionCatalog.System => "Narrative",
                OnboardingQuestionCatalog.WorldSetting => "solo",
                OnboardingQuestionCatalog.Party => BuildAtTableParty,
                _ when current.AnswerType == OnboardingAnswerType.Enum => current.EnumOptions![0],
                _ => "A reasonable free-text answer for this question."
            };
            var submit = await onboarding.SubmitOnboardingAnswer(slug, answer);
            Assert.True(submit.Success, submit.Summary);
            current = submit.Data!.CurrentQuestion;
        }

        Assert.DoesNotContain(OnboardingQuestionCatalog.StartingLevel, asked);
        var finalize = await onboarding.FinalizeCampaignOnboarding(slug);
        Assert.True(finalize.Success, finalize.Summary);
        Assert.DoesNotContain("Starting level", finalize.Data!.SeedBrief);
        Assert.DoesNotContain("level 1", finalize.Data.SeedBrief);
    }
}
