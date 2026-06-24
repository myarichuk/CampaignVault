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
            await _repo.UpsertLocationAsync(session,
                new Location { Id = locId, Name = "Private Room", CampaignName = owner });
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var scene = await _repo.GetSceneAsync(session, locId, other);
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

        using (var session = _store.OpenAsyncSession())
        {
            var sceneA = await _repo.GetSceneAsync(session, locId, campA);
            var sceneB = await _repo.GetSceneAsync(session, locId, campB);

            Assert.NotNull(sceneA.ActiveCombat);
            Assert.Equal(2, sceneA.ActiveCombat!.Round);
            Assert.Null(sceneB.ActiveCombat);
        }
    }

    [Fact]
    public async Task GetHelp_IncludesToolIndexAndCommitEnumSections()
    {
        var meta = new MetaTools();
        var result = await meta.GetHelp();

        Assert.True(result.Success);
        Assert.Contains("get_party", result.Data!, StringComparison.Ordinal);
        Assert.Contains("Commit Enum Values", result.Data!, StringComparison.Ordinal);
        Assert.Contains(CommitTypesReference.SupportedTypesList, result.Data!, StringComparison.Ordinal);
        Assert.Contains("chars/valen", result.Data!, StringComparison.Ordinal);
    }
}