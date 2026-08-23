using System.Collections.Generic;
using System.Text.Json;
using CampaignVault.Models;
using Xunit;
using Xunit.Abstractions;

namespace CampaignVault.Tests;

/// <summary>
/// Measures the serialized-size impact of the Phase 1 response-shape fixes (NpcContextView
/// double-serialization, SceneView.VisibleItems/Event summary projections, WorldPressureItems
/// wire exclusion). Not a strict regression gate on exact byte counts — reproducible evidence
/// of the magnitude of savings on representative data, printed via test output.
/// </summary>
public class ResponseShapeSizeTests
{
    private readonly ITestOutputHelper _output;
    private static readonly JsonSerializerOptions WireOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ResponseShapeSizeTests(ITestOutputHelper output) => _output = output;

    private static Character BuildRealisticNpc() => new()
    {
        Id = "chars/sample-npc",
        Name = "Old Tam",
        Psychology = new PsychologyProfile
        {
            Memories =
            {
                ["harbor gossip"] = new MemoryNode { Topic = "harbor gossip", Details = "Heard the Nightshade gang paid off the guards.", DayAcquired = 3 },
                ["party arrival"] = new MemoryNode { Topic = "party arrival", Details = "The party first walked in soaked from the storm.", DayAcquired = 1 },
            },
            Wants = ["a quiet night", "steady coin"],
            Fears = ["trouble in his bar", "the gang finding out he talked"],
            CurrentMood = "wary",
            Traits = ["gruff", "loyal to regulars"],
        },
        Social = new SocialProfile
        {
            Relationships = { ["chars/valen"] = 40, ["chars/lyra"] = 15 },
            FactionReputations = { ["factions/city-watch"] = 10 },
        },
        Needs = new NeedsProfile(),
        SystemStats = new SystemExtension(),
    };

    private static Item BuildRealisticItem() => new()
    {
        Id = "items/rusty-sword",
        Name = "Rusty Sword",
        Description = "A dull, notched blade that has seen better decades.",
        HolderId = "locations/rusty-nail",
        CoreCategory = ItemCategory.Weapon,
        Tags = ["rusty", "well-worn"],
        DistinctiveFeatures = ["Leather wrap loose at the hilt"],
        CurrentState = "Dull",
    };

    private static Event BuildRealisticEvent() => new()
    {
        Id = "events/sample",
        Summary = "The party bartered with Old Tam for news of the docks and left him a few coins richer.",
        Category = EventCategory.Conversation,
        Involved = ["chars/valen", "chars/sample-npc"],
        LocationId = "locations/rusty-nail",
        DayLogged = 5,
        Importance = MemoryImportance.Important,
        Details = new Dictionary<string, object> { ["itemTransfers"] = new[] { "items/coin-pouch" } },
    };

    [Fact]
    public void NpcContextView_NoLongerDuplicatesCharacterSubProfiles()
    {
        var npc = BuildRealisticNpc();

        var characterJson = JsonSerializer.Serialize(npc, WireOptions);
        var duplicatedFieldsSize =
            JsonSerializer.Serialize(npc.Psychology, WireOptions).Length +
            JsonSerializer.Serialize(npc.Social, WireOptions).Length +
            JsonSerializer.Serialize(npc.Needs, WireOptions).Length +
            JsonSerializer.Serialize(npc.SystemStats, WireOptions).Length;

        var context = new NpcContextView { Character = CharacterDetailView.From(npc) };
        var newSize = JsonSerializer.Serialize(context, WireOptions).Length;
        var oldSize = newSize + duplicatedFieldsSize; // old shape also shipped these 4 fields a second time at the top level

        _output.WriteLine($"NpcContextView: old={oldSize}B, new={newSize}B, saved={duplicatedFieldsSize}B ({duplicatedFieldsSize * 100.0 / oldSize:F0}%)");

        Assert.True(newSize < oldSize);
        Assert.True(duplicatedFieldsSize > 0);
    }

    [Fact]
    public void VisibleItems_SummaryProjectionIsSmallerThanFullItem()
    {
        var item = BuildRealisticItem();
        var fullSize = JsonSerializer.Serialize(item, WireOptions).Length;
        var summarySize = JsonSerializer.Serialize(ItemSummaryView.From(item), WireOptions).Length;

        _output.WriteLine($"Item vs ItemSummaryView: full={fullSize}B, summary={summarySize}B, saved={fullSize - summarySize}B ({(fullSize - summarySize) * 100.0 / fullSize:F0}%)");

        Assert.True(summarySize < fullSize);
    }

    [Fact]
    public void EquippedCarriedPaths_EnrichedItemSummaryViewCostIsSmall()
    {
        // ItemSummaryView already shipped on every NPC/party Equipped+Carried list before this
        // change (unaffected by the VisibleItems projection). Adding Description/Tags/
        // DistinctiveFeatures (needed so VisibleItems narration doesn't regress — see
        // LazyLlmScenarios' Tags/DistinctiveFeatures assertions) also grows every existing use of
        // this type. Measure that cost in isolation so it's not hidden inside the VisibleItems win.
        var item = BuildRealisticItem();
        var view = ItemSummaryView.From(item);

        var addedFieldsSize =
            JsonSerializer.Serialize(view.Description, WireOptions).Length +
            JsonSerializer.Serialize(view.Tags, WireOptions).Length +
            JsonSerializer.Serialize(view.DistinctiveFeatures, WireOptions).Length;
        var totalSize = JsonSerializer.Serialize(view, WireOptions).Length;

        _output.WriteLine($"ItemSummaryView enrichment cost: +{addedFieldsSize}B per item on Equipped/Carried lists (of {totalSize}B total, {addedFieldsSize * 100.0 / totalSize:F0}%)");

        // Sanity bound: the enrichment should stay a minor fraction of one item's summary, not
        // approach the size of a full Item (762B in VisibleItems_SummaryProjectionIsSmallerThanFullItem).
        Assert.True(addedFieldsSize < totalSize / 2);
    }

    [Fact]
    public void RecentEvents_SummaryProjectionIsSmallerThanFullEvent()
    {
        var ev = BuildRealisticEvent();
        var fullSize = JsonSerializer.Serialize(ev, WireOptions).Length;
        var summarySize = JsonSerializer.Serialize(EventSummaryView.From(ev), WireOptions).Length;

        _output.WriteLine($"Event vs EventSummaryView: full={fullSize}B, summary={summarySize}B, saved={fullSize - summarySize}B ({(fullSize - summarySize) * 100.0 / fullSize:F0}%)");

        Assert.True(summarySize < fullSize);
    }

    [Fact]
    public void WorldPressureItems_ExcludedFromWireSerialization()
    {
        var pressureItems = new List<WorldPressureItem>
        {
            new(PressureSeverity.EngineWarning, "locations/rusty-nail",
                "ENGINE WARNING: location expects a crowd but none are anchored. Example: {...}",
                "AmbientCrowd:Sparse") { SuggestedCommitJson = """{"character":{"id":"chars/example"}}""" },
        };

        var worldState = new WorldStateView(
            CampaignTimeView.From(new CampaignTime()),
            [],
            [],
            pressure: ["ENGINE WARNING: location expects a crowd but none are anchored. Example: {...}"],
            pressureItems: pressureItems);

        var json = JsonSerializer.Serialize(worldState, WireOptions);

        // The structured duplicate must not appear on the wire; the display-string form still does.
        Assert.DoesNotContain("worldPressureItems", json);
        Assert.Contains("ENGINE WARNING", json);

        // CampaignTime's Id (singleton-doc-key) and LastUpdated (write-time stamp) are bookkeeping,
        // not narrative content — CampaignTimeView drops them but keeps FormattedDate.
        Assert.DoesNotContain("\"id\"", json);
        Assert.DoesNotContain("lastUpdated", json);
        Assert.Contains("formattedDate", json);

        // But it's still readable in-process (tests / internal callers rely on this).
        Assert.Single(worldState.WorldPressureItems);
    }

    [Fact]
    public void ActiveCombat_ProjectedView_DropsSingletonDocKeyId()
    {
        var scene = new SceneView
        {
            Location = LocationDetailView.From(new Location { Id = "locations/arena" }),
            ActiveCombat = new CombatEncounterView("locations/arena", 2, [], "chars/example", true)
        };

        var json = JsonSerializer.Serialize(scene, WireOptions);

        // CombatEncounter.Id is a singleton-doc-key ("campaigns/{name}/combat/current"), not narrative
        // content — CombatEncounterView drops it but keeps round/isActive.
        Assert.DoesNotContain("\"id\":\"campaigns", json);
        Assert.Contains("\"round\":2", json);
        Assert.Contains("\"isActive\":true", json);
    }

    [Fact]
    public void NpcPresenceSummary_TagProvenance_ExcludedFromWireSerialization()
    {
        var presence = new NpcPresenceSummary(
            Id: "chars/old-tam",
            Name: "Old Tam",
            CurrentActivity: "tending bar",
            CurrentMood: "wary",
            TopNeeds: [],
            KnownNeeds: [],
            NeedDescriptors: [])
        {
            TagProvenance = new Dictionary<string, List<string>> { ["gruff"] = ["events/first-meeting"] }
        };

        var json = JsonSerializer.Serialize(presence, WireOptions);

        // TagProvenance is internal event-id provenance, not narrative content — not sent over the wire.
        Assert.DoesNotContain("tagProvenance", json);

        // But it's still readable in-process (same treatment as its Memories sibling).
        Assert.NotNull(presence.TagProvenance);
        Assert.Contains("gruff", presence.TagProvenance!.Keys);
    }
}
