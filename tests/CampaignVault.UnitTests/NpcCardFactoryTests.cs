using System.Collections.Generic;
using System.Linq;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// SystemExtension.Traits gating on NpcCard: an out-of-tree plugin writes "&lt;modeId&gt;.&lt;name&gt;"
/// entries into the character's shared Traits dictionary (its only extension point on SystemExtension,
/// whose $system-discriminated derived types are a closed set). Unprefixed entries always ride the card.
/// A dotted entry is gated only when its prefix is a key in modeParticipants (i.e. a currently-enabled
/// mode) — then it rides only while the NPC is an active participant in that mode's encounter. A dotted
/// entry whose prefix isn't a currently-enabled mode (including "anatomy.cock"-style non-mode data, and
/// the case where modeParticipants itself is null/empty) always rides, same as an unprefixed entry.
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
    public void Build_PrefixedTrait_Visible_WhenNoModeParticipantsSupplied()
    {
        // "crafting" is never a key in a null/empty modeParticipants dict, same as a non-mode prefix
        // like "anatomy.cock" — not gated data, so it rides.
        var npc = MakeNpc(systemTraits: new() { ["crafting.tool_quality"] = "masterwork" });

        var card = Build(npc, modeParticipants: null);

        Assert.Equal("crafting.tool_quality=masterwork", card.SystemTraits);
    }

    [Fact]
    public void Build_NonModePrefixedTrait_AlwaysVisible_EvenWhenOtherModesAreActive()
    {
        var npc = MakeNpc(id: "chars/npc1", systemTraits: new() { ["anatomy.cock"] = "described in appearance" });
        var modeParticipants = new Dictionary<string, HashSet<string>>
        {
            ["crafting"] = ["chars/someone_else"] // "anatomy" is not a registered mode at all
        };

        var card = Build(npc, modeParticipants);

        Assert.Equal("anatomy.cock=described in appearance", card.SystemTraits);
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

        // "crafting" enabled but this NPC isn't a participant: gated-and-hidden.
        var hiddenCard = Build(npc, modeParticipants: new Dictionary<string, HashSet<string>> { ["crafting"] = ["chars/someone_else"] });
        var visibleCard = Build(npc, modeParticipants: new Dictionary<string, HashSet<string>> { ["crafting"] = ["chars/npc1"] });

        Assert.NotEqual(hiddenCard.StableHash(), visibleCard.StableHash());
    }

    [Fact]
    public void StableHash_UnaffectedByTraitsDictionaryEnumerationOrder()
    {
        var npcA = MakeNpc(id: "chars/npc1", systemTraits: new()
        {
            ["astral.tether_strength"] = "3",
            ["recovery_die"] = "d8"
        });
        var npcB = MakeNpc(id: "chars/npc1", systemTraits: new()
        {
            ["recovery_die"] = "d8",
            ["astral.tether_strength"] = "3"
        });

        var cardA = Build(npcA);
        var cardB = Build(npcB);

        Assert.Equal(cardA.StableHash(), cardB.StableHash());
    }
}
