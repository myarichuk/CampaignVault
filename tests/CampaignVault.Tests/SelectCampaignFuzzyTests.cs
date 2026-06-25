using System;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class SelectCampaignFuzzyTests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;
    private readonly RavenDBFixture _fixture;

    public SelectCampaignFuzzyTests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
        _fixture = fixture;
    }

    [Fact]
    public async Task SelectCampaign_ReturnsRemoved_ForAnySlug()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var keys = new CampaignDocumentKeys();
        var slug = "sword-coast-" + Guid.NewGuid().ToString("N")[..6];

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign
            {
                Id = keys.Meta(slug),
                Name = slug,
                DisplayName = "Sword Coast",
                System = RulesetSystem.Dnd5e,
            });
            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true);
            await session.SaveChangesAsync();
        }

        var typo = slug.Replace("-", "");
        var result = await tools.SelectCampaign(typo);

        Assert.False(result.Success);
        Assert.Equal("Removed", result.Error);
        Assert.Contains("select_campaign has been removed", result.Summary);
    }

    [Fact]
    public async Task CreateCampaign_ThenGetCurrentCampaign_ReturnsPosture()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var keys = new CampaignDocumentKeys();
        var slug = "dragon-heist-" + Guid.NewGuid().ToString("N")[..6];
        var repo = _fixture.CreateRepository();

        var createResult = await tools.CreateCampaign(slug, RulesetSystem.Dnd5e, "Dragon Heist");
        Assert.True(createResult.Success, createResult.Summary);

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(session, new Character
            {
                Id = "chars/pc-" + Guid.NewGuid().ToString("N")[..6],
                Name = "Valen",
                IsPc = true,
            }, slug);
            await session.SaveChangesAsync();
        }

        var result = await tools.GetCurrentCampaign(slug);

        Assert.True(result.Success);
        Assert.Equal(slug, result.Data!.Campaign.Name);
        Assert.NotNull(result.Data.Posture);
        Assert.Single(result.Data.Posture!.Pcs);
        Assert.Equal(CampaignEntryHint.AddCompanion, result.Data.Posture.EntryHint);
    }
}