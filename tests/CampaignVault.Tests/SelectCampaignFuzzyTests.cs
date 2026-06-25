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
    public async Task SelectCampaign_FuzzyMatch_ReturnsSuggestionsWithoutSelecting()
    {
        var context = new CurrentCampaignContext();
        var tools = TestCampaignToolsFactory.Create(_fixture, context);
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
        Assert.Equal(ToolErrors.SlugAmbiguous, result.Error);
        Assert.NotNull(result.Data?.Suggestions);
        Assert.Contains(result.Data!.Suggestions!, s => s.Slug == slug);
        Assert.False(context.HasSelection);
    }

    [Fact]
    public async Task SelectCampaign_ExactMatch_ReturnsPostureAndSelects()
    {
        var context = new CurrentCampaignContext();
        var tools = TestCampaignToolsFactory.Create(_fixture, context);
        var keys = new CampaignDocumentKeys();
        var slug = "dragon-heist-" + Guid.NewGuid().ToString("N")[..6];
        var repo = _fixture.CreateRepository();

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign
            {
                Id = keys.Meta(slug),
                Name = slug,
                DisplayName = "Dragon Heist",
                System = RulesetSystem.Dnd5e,
                IsSystemLocked = true,
            });
            await repo.UpsertCharacterAsync(session, new Character
            {
                Id = "chars/pc-" + Guid.NewGuid().ToString("N")[..6],
                Name = "Valen",
                IsPc = true,
            }, slug);
            await session.SaveChangesAsync();
        }

        var result = await tools.SelectCampaign(slug);

        Assert.True(result.Success);
        Assert.Equal(slug, result.Data!.Slug);
        Assert.NotNull(result.Data.Posture);
        Assert.Single(result.Data.Posture!.Pcs);
        Assert.Equal(CampaignEntryHint.AddCompanion, result.Data.Posture.EntryHint);
        Assert.Equal(slug, context.CurrentCampaignName);
    }
}
