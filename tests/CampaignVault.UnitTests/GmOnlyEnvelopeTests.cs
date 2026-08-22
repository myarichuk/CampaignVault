using System.Text.Json;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Verifies that GM-only authored content (Character.Notes, Quest/PlotThread/WorldEvent.DmNotes) is
/// fenced under a labeled `gmOnly` envelope in get_entity/world_build response DTOs, rather than
/// shipped as an undifferentiated flat field — see GmOnly's doc comment (NpcViews.cs) for why.
/// </summary>
public class GmOnlyEnvelopeTests
{
    private static readonly JsonSerializerOptions WireOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void CharacterDetailView_FencesNotesUnderGmOnly()
    {
        var character = new Character { Id = "chars/npc", Name = "Elara", Notes = "aware of the bounty and curious if it is worth the risk" };

        var view = CharacterDetailView.From(character);

        Assert.Equal("aware of the bounty and curious if it is worth the risk", view.GmOnly.Notes);

        var json = JsonSerializer.Serialize(view, WireOptions);
        Assert.Contains("\"gmOnly\":{\"notes\":\"aware of the bounty", json);
        // Exactly one "notes" key in the whole payload — inside gmOnly, not also as a flat sibling field.
        Assert.Equal(1, CountOccurrences(json, "\"notes\":"));
    }

    [Fact]
    public void QuestDetailView_FencesDmNotesUnderGmOnly()
    {
        var quest = new Quest { Id = "quests/rats_01", Title = "Rat Problem", DmNotes = "the merchant is lying about the rat infestation" };

        var view = QuestDetailView.From(quest);

        Assert.Equal("the merchant is lying about the rat infestation", view.GmOnly.Notes);

        var json = JsonSerializer.Serialize(view, WireOptions);
        Assert.Contains("\"gmOnly\":{\"notes\":\"the merchant is lying", json);
        Assert.DoesNotContain("\"dmNotes\"", json);
    }

    [Fact]
    public void PlotThreadDetailView_FencesDmNotesUnderGmOnly()
    {
        var thread = new PlotThread { Id = "plot-threads/guild-infiltration", Title = "Guild Infiltration", DmNotes = "the guildmaster is a doppelganger" };

        var view = PlotThreadDetailView.From(thread);

        Assert.Equal("the guildmaster is a doppelganger", view.GmOnly.Notes);

        var json = JsonSerializer.Serialize(view, WireOptions);
        Assert.Contains("\"gmOnly\":{\"notes\":\"the guildmaster is a doppelganger", json);
        Assert.DoesNotContain("\"dmNotes\"", json);
    }

    [Fact]
    public void WorldEventDetailView_FencesDmNotesUnderGmOnly()
    {
        var worldEvent = new WorldEvent { Id = "events/uprising", Title = "The Uprising", DmNotes = "triggers only if faction influence exceeds 80" };

        var view = WorldEventDetailView.From(worldEvent);

        Assert.Equal("triggers only if faction influence exceeds 80", view.GmOnly.Notes);

        var json = JsonSerializer.Serialize(view, WireOptions);
        Assert.Contains("\"gmOnly\":{\"notes\":\"triggers only if faction influence exceeds 80", json);
        Assert.DoesNotContain("\"dmNotes\"", json);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
