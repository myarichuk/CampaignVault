using System.Collections.Generic;
using System.Linq;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// SystemExtension.Traits gating on NpcCard: an out-of-tree plugin writes "&lt;modeId&gt;.&lt;name&gt;"
/// entries into the character's shared Traits dictionary (its only extension point on SystemExtension,
/// whose $system-discriminated derived types are a closed set). Unprefixed entries always ride the card;
/// prefixed ones ride only while the NPC is an active participant in that mode's encounter, so a plugin's
/// mode-only facts don't cost tokens on every turn regardless of whether the mode is in play.
/// </summary>
public class NpcCardFactoryTests
{
    private static Character MakeNpc(string id = "chars/npc1", Dictionary<string, string>? systemTraits = null) => new()
    {
        Id = id,
        Name = "Test NPC",
        IsPc = false,
        SystemStats = new SystemExtension { Traits = systemTraits ?? [] }
    };

    private static NpcCard Build(Character npc, IReadOnlyDictionary<string, HashSet<string>>? modeParticipants = null) =>
        NpcCardFactory.Build(npc, party: [], heldItems: [], modeParticipants: modeParticipants);

    [Fact]
    public void Build_UnprefixedTrait_AlwaysVisible_RegardlessOfModeParticipants()
    {
        var npc = MakeNpc(systemTraits: new() { ["recovery_die"] = "d8" });

        var card = Build(npc, modeParticipants: null);

        Assert.Equal("recovery_die=d8", card.SystemTraits);
    }

    [Fact]
    public void Build_PrefixedTrait_Hidden_WhenNoModeParticipantsSupplied()
    {
        var npc = MakeNpc(systemTraits: new() { ["crafting.tool_quality"] = "masterwork" });

        var card = Build(npc, modeParticipants: null);

        Assert.Null(card.SystemTraits);
    }

    [Fact]
    public void Build_PrefixedTrait_Hidden_WhenModeEnabledButNpcNotAParticipant()
    {
        var npc = MakeNpc(id: "chars/npc1", systemTraits: new() { ["crafting.tool_quality"] = "masterwork" });
        var modeParticipants = new Dictionary<string, HashSet<string>>
        {
            ["crafting"] = ["chars/npc2"] // someone else is crafting, not this NPC
        };

        var card = Build(npc, modeParticipants);

        Assert.Null(card.SystemTraits);
    }

    [Fact]
    public void Build_PrefixedTrait_Visible_WhenNpcIsActiveParticipantInThatMode()
    {
        var npc = MakeNpc(id: "chars/npc1", systemTraits: new() { ["crafting.tool_quality"] = "masterwork" });
        var modeParticipants = new Dictionary<string, HashSet<string>>
        {
            ["crafting"] = ["chars/npc1"]
        };

        var card = Build(npc, modeParticipants);

        Assert.Equal("crafting.tool_quality=masterwork", card.SystemTraits);
    }

    [Fact]
    public void Build_MixOfPrefixedAndUnprefixed_OnlyGatedEntriesFiltered()
    {
        var npc = MakeNpc(id: "chars/npc1", systemTraits: new()
        {
            ["recovery_die"] = "d8",
            ["crafting.tool_quality"] = "masterwork",
            ["astral.tether_strength"] = "3"
        });
        var modeParticipants = new Dictionary<string, HashSet<string>>
        {
            ["crafting"] = ["chars/npc1"],
            ["astral"] = ["chars/someone_else"]
        };

        var card = Build(npc, modeParticipants);

        Assert.NotNull(card.SystemTraits);
        var entries = card.SystemTraits!.Split(", ");
        Assert.Contains("recovery_die=d8", entries);
        Assert.Contains("crafting.tool_quality=masterwork", entries);
        Assert.DoesNotContain(entries, e => e.StartsWith("astral."));
    }

    [Fact]
    public void Build_NpcInMultipleActiveModesSimultaneously_BothPrefixedTraitsVisible()
    {
        var npc = MakeNpc(id: "chars/npc1", systemTraits: new()
        {
            ["crafting.tool_quality"] = "masterwork",
            ["social.rapport"] = "warm"
        });
        var modeParticipants = new Dictionary<string, HashSet<string>>
        {
            ["crafting"] = ["chars/npc1"],
            ["social"] = ["chars/npc1"]
        };

        var card = Build(npc, modeParticipants);

        var entries = card.SystemTraits!.Split(", ");
        Assert.Contains("crafting.tool_quality=masterwork", entries);
        Assert.Contains("social.rapport=warm", entries);
    }

    [Fact]
    public void Build_NoSystemTraits_SystemTraitsIsNull()
    {
        var npc = MakeNpc();

        var card = Build(npc);

        Assert.Null(card.SystemTraits);
    }

    [Fact]
    public void StableHash_ChangesWhenSystemTraitsVisibilityChanges()
    {
        var npc = MakeNpc(id: "chars/npc1", systemTraits: new() { ["crafting.tool_quality"] = "masterwork" });

        var hiddenCard = Build(npc, modeParticipants: null);
        var visibleCard = Build(npc, modeParticipants: new Dictionary<string, HashSet<string>> { ["crafting"] = ["chars/npc1"] });

        Assert.NotEqual(hiddenCard.StableHash(), visibleCard.StableHash());
    }
}
