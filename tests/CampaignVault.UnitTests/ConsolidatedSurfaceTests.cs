using System;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Behavioral smoke coverage for the consolidated dispatcher tools introduced by the
/// surface-reduction refactor: start_session, get_entity, combat(action), get_rules_reference,
/// and the campaign_update change type.
/// </summary>
[Collection("RavenDB")]
public class ConsolidatedSurfaceTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public ConsolidatedSurfaceTests(RavenDBFixture fixture) => _fixture = fixture;

    private string NewSlug(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task StartSession_ReturnsKickoffSuperset_AndResumesWhenAlreadyOpen()
    {
        var slug = NewSlug("kickoff");
        var tools = TestCampaignToolsFactory.Create(_fixture);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);
        var session = TestCampaignToolsFactory.CreateTool<SessionTools>(_fixture);

        var first = await session.StartSession(slug);
        Assert.True(first.Success, first.Summary);
        Assert.False(first.Data!.Resumed);
        Assert.Equal(1, first.Data.SessionNumber);
        Assert.Equal("No prior sessions.", first.Data.LastSessionRecap);
        Assert.NotNull(first.Data.WorldState);
        Assert.NotNull(first.Data.WorldState.SeedCoverage);
        Assert.NotNull(first.Data.Campaign);

        // Calling again with the session still open resumes instead of erroring — the kickoff
        // must survive a reconnect/context-loss mid-session.
        var second = await session.StartSession(slug);
        Assert.True(second.Success, second.Summary);
        Assert.True(second.Data!.Resumed);
        Assert.Equal(1, second.Data.SessionNumber);
    }

    [Fact]
    public async Task GetEntity_DispatchesByIdPrefix_AndRejectsUnknownPrefixes()
    {
        var slug = NewSlug("entity");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture, repo);
        var deepDive = TestCampaignToolsFactory.CreateDeepDiveTools(_fixture, repo);

        var build = await worldBuilder.WorldBuild(new WorldBuildBatch
        {
            Locations = [new LocationUpsertRequest { Id = "locations/entity-test-tavern", Name = "Tavern", Description = "A tavern.", Type = LocationType.Building }],
            Characters = [new CharacterUpsertRequest { Id = "chars/entity-test-npc", Name = "Tam", CurrentLocationId = "locations/entity-test-tavern" }],
        }, slug);
        Assert.True(build.Success, build.Summary);

        var npc = await deepDive.GetEntity("chars/entity-test-npc", slug);
        Assert.True(npc.Success, npc.Summary);
        Assert.IsType<NpcContextView>(npc.Data);

        var scene = await deepDive.GetEntity("locations/entity-test-tavern", slug, partyPresent: false);
        Assert.True(scene.Success, scene.Summary);
        Assert.IsType<SceneView>(scene.Data);

        var missingQuest = await deepDive.GetEntity("quests/does-not-exist", slug);
        Assert.False(missingQuest.Success);
        Assert.Equal("NotFound", missingQuest.Error);

        var badPrefix = await deepDive.GetEntity("spells/fireball", slug);
        Assert.False(badPrefix.Success);
        Assert.Contains("search_world", badPrefix.Summary);

        var threadList = await deepDive.GetEntity("plot-threads", slug);
        Assert.True(threadList.Success, threadList.Summary);
    }

    [Fact]
    public async Task Combat_DispatchesLifecycleActions_AndRejectsUnknownAction()
    {
        var slug = NewSlug("combatd");
        var tools = TestCampaignToolsFactory.Create(_fixture);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);
        var combat = TestCampaignToolsFactory.CreateTool<CombatTools>(_fixture);

        var status = await combat.Combat(slug, "status");
        Assert.True(status.Success, status.Summary);
        Assert.Contains("No active combat", status.Summary);

        var next = await combat.Combat(slug, "next");
        Assert.False(next.Success);

        var unknown = await combat.Combat(slug, "flee");
        Assert.False(unknown.Success);
        Assert.Contains("'start', 'next', 'end', or 'status'", unknown.Summary);
    }

    [Fact]
    public async Task GetRulesReference_DispatchesByKind_AndValidatesInputs()
    {
        var slug = NewSlug("rules");
        var tools = TestCampaignToolsFactory.Create(_fixture);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);
        var mgmt = TestCampaignToolsFactory.CreateTool<CampaignManagementTools>(_fixture);

        var handbook = await mgmt.GetRulesReference(slug, "handbook");
        Assert.True(handbook.Success, handbook.Summary);
        Assert.IsType<SystemHandbookResponse>(handbook.Data);

        var spellsNoClass = await mgmt.GetRulesReference(slug, "spells");
        Assert.False(spellsNoClass.Success);
        Assert.Contains("className", spellsNoClass.Summary);

        var spells = await mgmt.GetRulesReference(slug, "spells", className: "Wizard", level: 1);
        Assert.True(spells.Success, spells.Summary);

        var creatures = await mgmt.GetRulesReference(slug, "creatures", nameQuery: "goblin");
        Assert.True(creatures.Success, creatures.Summary);

        var badKind = await mgmt.GetRulesReference(slug, "monsters");
        Assert.False(badKind.Success);
        Assert.Contains("'handbook', 'spells', or 'creatures'", badKind.Summary);
    }

    [Fact]
    public async Task TakeTurn_CampaignUpdateChange_ReplacesNarrativeFocus()
    {
        var slug = NewSlug("focus");
        var tools = TestCampaignToolsFactory.Create(_fixture);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var result = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new CampaignUpdateChange { NarrativeFocus = ["political intrigue", "court politics"] }],
            Narrative = "The campaign's center of gravity shifts to the capital's politics.",
        }, slug);

        Assert.True(result.Success, result.Summary);
        Assert.Contains(result.Data!.Summary, s => s.Contains("political intrigue"));

        var session = TestCampaignToolsFactory.CreateTool<SessionTools>(_fixture);
        var kickoff = await session.StartSession(slug);
        Assert.True(kickoff.Success, kickoff.Summary);
        Assert.Contains("political intrigue", kickoff.Data!.Campaign!.Campaign.NarrativeFocus);
    }
}
