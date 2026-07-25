using System.Linq;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class PlotThreadReverseIndexTests
{
    [Fact]
    public void AllInvolvedEntityIds_FlattensThreadAndClueIds()
    {
        // Arrange
        var thread = new PlotThread
        {
            Id = "plot-threads/test-1",
            Title = "Test Thread",
            InvolvedEntityIds = ["chars/primary-npc", "factions/guild"],
            Clues =
            [
                new PlotClue("clue-1", "A letter",
                    InvolvedEntityIds: ["items/letter", "chars/witness"]),
                new PlotClue("clue-2", "A rumor",
                    InvolvedEntityIds: ["locations/tavern"])
            ]
        };

        // Act
        var flattened = thread.AllInvolvedEntityIds;

        // Assert: Should include all thread-level + all clue-level IDs
        Assert.Equal(5, flattened.Count);
        Assert.Contains("chars/primary-npc", flattened);
        Assert.Contains("factions/guild", flattened);
        Assert.Contains("items/letter", flattened);
        Assert.Contains("chars/witness", flattened);
        Assert.Contains("locations/tavern", flattened);
    }

    [Fact]
    public void AllInvolvedEntityIds_NullCluesHandledGracefully()
    {
        // Arrange
        var thread = new PlotThread
        {
            Id = "plot-threads/test-2",
            Title = "Test Thread",
            InvolvedEntityIds = ["chars/npc-1"],
            Clues = null!
        };

        // Act
        var flattened = thread.AllInvolvedEntityIds;

        // Assert
        Assert.Single(flattened);
        Assert.Contains("chars/npc-1", flattened);
    }

    [Fact]
    public void AllInvolvedEntityIds_EmptyCluesHandledGracefully()
    {
        // Arrange
        var thread = new PlotThread
        {
            Id = "plot-threads/test-3",
            Title = "Test Thread",
            InvolvedEntityIds = ["chars/npc-1"],
            Clues = []
        };

        // Act
        var flattened = thread.AllInvolvedEntityIds;

        // Assert
        Assert.Single(flattened);
        Assert.Contains("chars/npc-1", flattened);
    }

    [Fact]
    public void AllInvolvedEntityIds_ClueWithNullInvolvedEntitiesHandledGracefully()
    {
        // Arrange
        var thread = new PlotThread
        {
            Id = "plot-threads/test-4",
            Title = "Test Thread",
            InvolvedEntityIds = ["chars/primary-npc"],
            Clues =
            [
                new PlotClue("clue-1", "A clue with no involved entities",
                    InvolvedEntityIds: null)
            ]
        };

        // Act
        var flattened = thread.AllInvolvedEntityIds;

        // Assert
        Assert.Single(flattened);
        Assert.Contains("chars/primary-npc", flattened);
    }

    [Fact]
    public void AllInvolvedEntityIds_ClueWithEmptyInvolvedEntitiesHandledGracefully()
    {
        // Arrange
        var thread = new PlotThread
        {
            Id = "plot-threads/test-5",
            Title = "Test Thread",
            InvolvedEntityIds = ["chars/primary-npc"],
            Clues =
            [
                new PlotClue("clue-1", "A clue with empty list",
                    InvolvedEntityIds: [])
            ]
        };

        // Act
        var flattened = thread.AllInvolvedEntityIds;

        // Assert
        Assert.Single(flattened);
        Assert.Contains("chars/primary-npc", flattened);
    }

    [Fact]
    public void AllInvolvedEntityIds_DeduplicatesDuplicateAcrossThreadAndClues()
    {
        // Arrange
        var thread = new PlotThread
        {
            Id = "plot-threads/test-6",
            Title = "Test Thread",
            InvolvedEntityIds = ["chars/shared-npc", "factions/guild"],
            Clues =
            [
                new PlotClue("clue-1", "A clue",
                    InvolvedEntityIds: ["chars/shared-npc", "items/letter"])
            ]
        };

        // Act
        var flattened = thread.AllInvolvedEntityIds;

        // Assert: chars/shared-npc appears in both, but should only be in list once
        Assert.Equal(3, flattened.Count);
        Assert.Equal(1, flattened.ToList().Count(id => id == "chars/shared-npc"));
        Assert.Contains("factions/guild", flattened);
        Assert.Contains("items/letter", flattened);
    }

    [Fact]
    public void AllInvolvedEntityIds_IgnoresWhitespaceOnlyIds()
    {
        // Arrange
        var thread = new PlotThread
        {
            Id = "plot-threads/test-7",
            Title = "Test Thread",
            InvolvedEntityIds = ["chars/valid-npc", "", "  ", null!],
            Clues =
            [
                new PlotClue("clue-1", "A clue",
                    InvolvedEntityIds: ["items/valid-item", ""])
            ]
        };

        // Act
        var flattened = thread.AllInvolvedEntityIds;

        // Assert: Only non-empty IDs included
        Assert.Equal(2, flattened.Count);
        Assert.Contains("chars/valid-npc", flattened);
        Assert.Contains("items/valid-item", flattened);
    }

    [Fact]
    public void AllInvolvedEntityIds_EmptyThreadAndCluesReturnsEmptyList()
    {
        // Arrange
        var thread = new PlotThread
        {
            Id = "plot-threads/test-8",
            Title = "Test Thread",
            InvolvedEntityIds = [],
            Clues = []
        };

        // Act
        var flattened = thread.AllInvolvedEntityIds;

        // Assert
        Assert.Empty(flattened);
    }

    [Fact]
    public void AllInvolvedEntityIds_MultipleCluesWithMultipleIds()
    {
        // Arrange
        var thread = new PlotThread
        {
            Id = "plot-threads/test-9",
            Title = "Test Thread",
            InvolvedEntityIds = ["factions/guild"],
            Clues =
            [
                new PlotClue("clue-1", "Clue 1",
                    InvolvedEntityIds: ["chars/npc-1", "items/item-1"]),
                new PlotClue("clue-2", "Clue 2",
                    InvolvedEntityIds: ["chars/npc-2", "locations/loc-1"]),
                new PlotClue("clue-3", "Clue 3",
                    InvolvedEntityIds: ["chars/npc-3", "items/item-2"])
            ]
        };

        // Act
        var flattened = thread.AllInvolvedEntityIds;

        // Assert: All IDs from thread + all clues should be present
        Assert.Equal(7, flattened.Count); // 1 thread + 2+2+2 clues
        Assert.Contains("factions/guild", flattened);
        Assert.Contains("chars/npc-1", flattened);
        Assert.Contains("chars/npc-2", flattened);
        Assert.Contains("chars/npc-3", flattened);
        Assert.Contains("items/item-1", flattened);
        Assert.Contains("items/item-2", flattened);
        Assert.Contains("locations/loc-1", flattened);
    }
}
