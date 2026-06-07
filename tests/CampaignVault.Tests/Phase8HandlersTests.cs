using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class Phase8HandlersTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public Phase8HandlersTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private ChangeContext CreateContext(IAsyncDocumentSession session)
    {
        return new ChangeContext(
            session,
            new Dictionary<string, Character>(),
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            () => Task.FromResult(new CampaignTime { TotalDaysElapsed = 10 }),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask,
            new List<string>(),
            new WorldChangeDispatcher(new List<IWorldChangeHandler>()),
            null,
            "test-campaign"
        );
    }

    [Fact]
    public async Task ItemUpdate_PatchesFieldsCorrectly()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var item = new Item { Id = "items/1", Name = "Sword", Tags = ["sharp"], DistinctiveFeatures = ["rusty"], Properties = new Dictionary<string, object> { ["weight"] = 5 } };
        await session.StoreAsync(item);
        await session.SaveChangesAsync();

        var ctx = CreateContext(session);

        var handler = new ItemUpdateHandler();
        var update = new ItemUpdate
        {
            ItemId = "items/1",
            NewState = "Glows blue",
            TagsToAdd = ["glowing"],
            TagsToRemove = ["sharp"],
            FeaturesToAdd = ["engraved"],
            FeaturesToRemove = ["rusty"],
            PropertiesToUpsert = new Dictionary<string, object> { ["magic"] = true },
            PropertiesToRemove = ["weight"]
        };

        var result = await handler.ApplyAsync(update, ctx);

        Assert.True(result.Success);
        
        var loaded = await session.LoadAsync<Item>("items/1");
        Assert.Equal("Glows blue", loaded.CurrentState);
        Assert.Contains("glowing", loaded.Tags);
        Assert.DoesNotContain("sharp", loaded.Tags);
        Assert.Contains("engraved", loaded.DistinctiveFeatures);
        Assert.DoesNotContain("rusty", loaded.DistinctiveFeatures);
        Assert.True((bool)loaded.Properties["magic"]);
        Assert.False(loaded.Properties.ContainsKey("weight"));
    }

    [Fact]
    public async Task CharacterUpdate_PatchesFieldsCorrectly()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var c = new Character { Id = "chars/1", Name = "Bob", VisualTags = ["clean"], DistinctiveFeatures = ["scar"] };
        await session.StoreAsync(c);
        await session.SaveChangesAsync();

        var ctx = CreateContext(session);

        var handler = new CharacterUpdateHandler();
        var update = new CharacterUpdate
        {
            CharacterId = "chars/1",
            AppearanceOverride = "Muddy and tired",
            TagsToAdd = ["muddy"],
            TagsToRemove = ["clean"],
            FeaturesToAdd = ["tattoo"],
            FeaturesToRemove = ["scar"]
        };

        var result = await handler.ApplyAsync(update, ctx);

        Assert.True(result.Success);
        
        var loaded = await session.LoadAsync<Character>("chars/1");
        Assert.Equal("Muddy and tired", loaded.CurrentAppearance);
        Assert.Contains("muddy", loaded.VisualTags);
        Assert.DoesNotContain("clean", loaded.VisualTags);
        Assert.Contains("tattoo", loaded.DistinctiveFeatures);
        Assert.DoesNotContain("scar", loaded.DistinctiveFeatures);
    }
}
