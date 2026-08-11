using System;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class SceneCombatScopingTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;
    private readonly CampaignRepository _repo;
    private readonly IDocumentStore _store;
    private readonly CampaignDocumentKeys _keys = new();

    public SceneCombatScopingTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
        _repo = fixture.CreateRepository();
        _store = fixture.Store;
    }

    [Fact]
    public async Task GetScene_HidesLocationTaggedForOtherCampaign()
    {
        var locId = "locations/scoped-only-" + Guid.NewGuid();
        const string owner = "campaign-a";
        const string other = "campaign-b";

        using (var session = _store.OpenAsyncSession())
        {
            await _repo.UpsertLocationAsync(_fixture.CreateCampaignSession(session, owner), new LocationUpsertRequest { Id = locId, Name = "Private Room", CampaignName = owner });
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await _repo.GetSceneAsync(_fixture.CreateCampaignSession(session, other), locId);
            Assert.False(scene.IsLocationAnchored);
            Assert.Equal("[Unanchored]", scene.Location.Name);
        }
    }

    [Fact]
    public async Task GetScene_DoesNotExposeOtherCampaignActiveCombat()
    {
        var locId = "locations/shared-combat-" + Guid.NewGuid();
        const string campA = "combat-camp-a";
        const string campB = "combat-camp-b";

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Location { Id = locId, Name = "Arena", CampaignName = null });
            await session.StoreAsync(new CombatEncounter
            {
                Id = _keys.CombatCurrent(campA),
                LocationId = locId,
                IsActive = true,
                Round = 2,
            });
            await session.SaveChangesAsync();
        }

        // Each GetSceneAsync call issues many round trips; production always opens a fresh
        // session per tool call (see CampaignToolBase.ExecuteAsync), so mirror that here rather
        // than sharing one session across both calls (which trips RavenDB's per-session request cap).
        SceneView sceneA;
        using (var session = _store.OpenAsyncSession())
        {
            sceneA = await _repo.GetSceneAsync(_fixture.CreateCampaignSession(session, campA), locId);
        }

        SceneView sceneB;
        using (var session = _store.OpenAsyncSession())
        {
            sceneB = await _repo.GetSceneAsync(_fixture.CreateCampaignSession(session, campB), locId);
        }

        Assert.NotNull(sceneA.ActiveCombat);
        Assert.Equal(2, sceneA.ActiveCombat!.Round);
        Assert.Null(sceneB.ActiveCombat);
    }

    [Fact]
    public async Task GetHelp_IncludesToolIndexAndCommitEnumSections()
    {
        var meta = new MetaTools();
        var reference = await meta.GetHelp();
        var commitEnum = await meta.GetHelp("commit-enum");
        var tools = await meta.GetHelp("tools");

        Assert.True(reference.Success);
        Assert.Contains("Reference lookup", reference.Data!, StringComparison.Ordinal);
        Assert.Contains("Commit Enum Values", commitEnum.Data!, StringComparison.Ordinal);
        Assert.Contains("Region, Settlement, District, Building, Room, Wilderness", commitEnum.Data!, StringComparison.Ordinal);
        // Combat guidance is now delivered on tool responses, not via get_help (Phase 3)
        Assert.Contains("start_session", tools.Data!, StringComparison.Ordinal);
    }
}
