using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents.Session;
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
            new WorldChangeDispatcher(new List<IWorldChangeHandler>(), new CampaignVault.Data.CampaignDocumentKeys()),
            null,
            "test-campaign"
        );
    }

    [Fact]
    public async Task ItemUpdate_FailsIfItemDoesNotExist()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var ctx = CreateContext(session);
        var handler = new ItemUpdateHandler();

        var result = await handler.ApplyAsync(new ItemUpdate { ItemId = "items/missing", NewState = "Broken" }, ctx);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ItemCreate_SetsCoreCategory()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var ctx = CreateContext(session);
        var handler = new ItemCreateHandler();

        var result = await handler.ApplyAsync(new ItemCreate
        {
            ItemId = "items/shield",
            Name = "Shield",
            Description = "A sturdy shield",
            HolderId = "chars/pc1",
            CoreCategory = ItemCategory.Armor
        }, ctx);

        Assert.True(result.Success);
        var loaded = await session.LoadAsync<Item>("items/shield");
        Assert.Equal(ItemCategory.Armor, loaded.CoreCategory);
    }

    [Fact]
    public async Task ItemUpdate_PatchesFieldsCorrectly()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var item = new Item
        {
            Id = "items/1", Name = "Sword", Tags = ["sharp"], DistinctiveFeatures = ["rusty"],
            Properties = new Dictionary<string, object> { ["weight"] = 5 }
        };
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

        var handler = new CharacterUpdateHandler(new CampaignVault.Data.CampaignDocumentKeys());
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

    [Fact]
    public async Task CharacterUpdate_SetsKeepAlive()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var c = new Character { Id = "chars/giver", Name = "Quest Giver", KeepAlive = false };
        await session.StoreAsync(c);
        await session.SaveChangesAsync();

        var ctx = CreateContext(session);
        var handler = new CharacterUpdateHandler(new CampaignVault.Data.CampaignDocumentKeys());
        var result = await handler.ApplyAsync(new CharacterUpdate
        {
            CharacterId = "chars/giver",
            KeepAlive = true
        }, ctx);

        Assert.True(result.Success);
        var loaded = await session.LoadAsync<Character>("chars/giver");
        Assert.True(loaded.KeepAlive);
    }

    [Fact]
    public async Task KnowledgeUpdate_AddsNewMemoryNode()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var c = new Character { Id = "chars/bob", Name = "Bob" };
        await session.StoreAsync(c);
        await session.SaveChangesAsync();

        var ctx = CreateContext(session);

        var handler = new KnowledgeUpdateHandler();
        var update = new KnowledgeUpdate
        {
            CharacterId = "chars/bob",
            Topic = "The Rusty Tavern",
            Details = "Owned by Bram.",
            Importance = MemoryImportance.Core
        };

        var result = await handler.ApplyAsync(update, ctx);

        Assert.True(result.Success);

        var loaded = await session.LoadAsync<Character>("chars/bob");
        Assert.True(loaded.Psychology.Memories.ContainsKey("The Rusty Tavern"));
        var mem = loaded.Psychology.Memories["The Rusty Tavern"];
        Assert.Equal("Owned by Bram.", mem.Details);
        Assert.Equal(MemoryImportance.Core, mem.Importance);
        Assert.Equal(10, mem.DayAcquired); // Context mock returns TotalDaysElapsed = 10
    }

    [Fact]
    public async Task KnowledgeUpdate_UpdatesExistingMemory()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var c = new Character { Id = "chars/alice", Name = "Alice" };
        c.Psychology.Memories["Mayor Bob"] = new MemoryNode
        {
            Topic = "Mayor Bob",
            Details = "Good guy.",
            DayAcquired = 2,
            Importance = MemoryImportance.Important
        };
        await session.StoreAsync(c);
        await session.SaveChangesAsync();

        var ctx = CreateContext(session);

        var handler = new KnowledgeUpdateHandler();
        var update = new KnowledgeUpdate
        {
            CharacterId = "chars/alice",
            Topic = "Mayor Bob",
            Details = "Actually a thief!"
        };

        var result = await handler.ApplyAsync(update, ctx);

        Assert.True(result.Success);

        var loaded = await session.LoadAsync<Character>("chars/alice");
        var mem = loaded.Psychology.Memories["Mayor Bob"];
        Assert.Equal("Actually a thief!", mem.Details);
        // Importance unchanged because it was null in update
        Assert.Equal(MemoryImportance.Important, mem.Importance);
        // DayAcquired resets to 10
        Assert.Equal(10, mem.DayAcquired);
    }

    [Fact]
    public async Task KnowledgeUpdate_CreateMemoryFalse_SkipsWrite()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var c = new Character { Id = "chars/skip", Name = "Skip" };
        c.Psychology.Memories["Old Topic"] = new MemoryNode
        {
            Topic = "Old Topic",
            Details = "Unchanged.",
            DayAcquired = 3
        };
        await session.StoreAsync(c);
        await session.SaveChangesAsync();

        var ctx = CreateContext(session);
        var handler = new KnowledgeUpdateHandler();
        var update = new KnowledgeUpdate
        {
            CharacterId = "chars/skip",
            Topic = "New Topic",
            Details = "Should not be stored.",
            CreateMemory = false
        };

        var result = await handler.ApplyAsync(update, ctx);

        Assert.True(result.Success);
        var loaded = await session.LoadAsync<Character>("chars/skip");
        Assert.False(loaded.Psychology.Memories.ContainsKey("New Topic"));
        Assert.Equal("Unchanged.", loaded.Psychology.Memories["Old Topic"].Details);
    }

    [Fact]
    public async Task KnowledgeUpdate_StructuredEnrichment_OverridesInference()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var c = new Character { Id = "chars/enriched", Name = "Enriched" };
        await session.StoreAsync(c);
        await session.SaveChangesAsync();

        var ctx = CreateContext(session);
        var handler = new KnowledgeUpdateHandler();
        var update = new KnowledgeUpdate
        {
            CharacterId = "chars/enriched",
            Topic = "Party gift",
            Details = "They gave me a necklace after I warned them about the road.",
            Source = MemorySource.Experienced,
            Valence = EmotionalValence.Positive,
            Salience = 0.8,
            Urgency = MemoryUrgency.Normal,
            RelatedEntityIds = ["chars/pc1", "items/necklace"]
        };

        var result = await handler.ApplyAsync(update, ctx);

        Assert.True(result.Success);
        var mem = (await session.LoadAsync<Character>("chars/enriched")).Psychology.Memories["Party gift"];
        Assert.Equal(MemorySource.Experienced, mem.Source);
        Assert.Equal(EmotionalValence.Positive, mem.Valence);
        Assert.Equal(0.8, mem.Salience);
        Assert.Equal(MemoryUrgency.Normal, mem.Urgency);
        Assert.Equal(["chars/pc1", "items/necklace"], mem.RelatedEntityIds);
    }

    [Fact]
    public async Task KnowledgeUpdate_InferenceDefaults_FromDetailsKeywords()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var c = new Character { Id = "chars/infer", Name = "Infer" };
        await session.StoreAsync(c);
        await session.SaveChangesAsync();

        var ctx = CreateContext(session);
        var handler = new KnowledgeUpdateHandler();
        var update = new KnowledgeUpdate
        {
            CharacterId = "chars/infer",
            Topic = "The brawl",
            Details = "I witnessed violence and death in the square."
        };

        var result = await handler.ApplyAsync(update, ctx);

        Assert.True(result.Success);
        var mem = (await session.LoadAsync<Character>("chars/infer")).Psychology.Memories["The brawl"];
        Assert.Equal(MemorySource.Witnessed, mem.Source);
        Assert.Equal(EmotionalValence.Negative, mem.Valence);
        Assert.True(mem.Salience >= 0.7);
        Assert.Equal(MemoryUrgency.High, mem.Urgency);
    }

    [Fact]
    public async Task KnowledgeUpdate_LegacyMemory_GetsMigrationDefaultsOnTouch()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var c = new Character { Id = "chars/legacy", Name = "Legacy" };
        c.Psychology.Memories["Mayor"] = new MemoryNode
        {
            Topic = "Mayor",
            Details = "Old memory.",
            DayAcquired = 2,
            Importance = MemoryImportance.Important,
            Salience = 0
        };
        await session.StoreAsync(c);
        await session.SaveChangesAsync();

        var ctx = CreateContext(session);
        var handler = new KnowledgeUpdateHandler();
        var update = new KnowledgeUpdate
        {
            CharacterId = "chars/legacy",
            Topic = "Mayor",
            Details = "Updated details."
        };

        var result = await handler.ApplyAsync(update, ctx);

        Assert.True(result.Success);
        var mem = (await session.LoadAsync<Character>("chars/legacy")).Psychology.Memories["Mayor"];
        Assert.Equal(MemorySource.Told, mem.Source);
        Assert.Equal(EmotionalValence.Neutral, mem.Valence);
        Assert.Equal(0.5, mem.Salience);
        Assert.Equal(MemoryUrgency.Normal, mem.Urgency);
    }

    [Fact]
    public async Task LocationState_PatchesFieldsCorrectly()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var l = new Location
            { Id = "locations/1", Name = "Tavern", VisualTags = ["clean"], DistinctiveFeatures = ["sign"] };
        await session.StoreAsync(l);
        await session.SaveChangesAsync();

        var ctx = CreateContext(session);

        var handler = new LocationUpdateHandler();
        var update = new LocationUpdate
        {
            LocationId = "locations/1",
            NewState = "On fire!",
            TagsToAdd = ["smoky"],
            TagsToRemove = ["clean"],
            FeaturesToAdd = ["crater"],
            FeaturesToRemove = ["sign"]
        };

        var result = await handler.ApplyAsync(update, ctx);

        Assert.True(result.Success);

        var loaded = await session.LoadAsync<Location>("locations/1");
        Assert.Equal("On fire!", loaded.CurrentState);
        Assert.Contains("smoky", loaded.VisualTags);
        Assert.DoesNotContain("clean", loaded.VisualTags);
        Assert.Contains("crater", loaded.DistinctiveFeatures);
        Assert.DoesNotContain("sign", loaded.DistinctiveFeatures);
    }

    [Fact]
    public async Task ActivityChange_LoadsCharacterFromSession_WhenNotPreloaded()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = new Character
        {
            Id = "chars/offline-npc",
            Name = "Offline NPC",
            CurrentActivity = "Idle"
        };
        await session.StoreAsync(character);
        await session.SaveChangesAsync();

        var ctx = CreateContext(session);
        var handler = new ActivityChangeHandler();

        var result = await handler.ApplyAsync(new ActivityChange
        {
            CharacterId = character.Id,
            NewActivity = "Patrolling"
        }, ctx);

        Assert.True(result.Success);
        Assert.Equal("Patrolling", character.CurrentActivity);
        Assert.True(ctx.Characters.ContainsKey(character.Id));
    }
}