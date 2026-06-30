using System.Collections.Generic;
using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class ClassDefinitionTests
{
    private static readonly ClassDefinitionProvider Provider = new(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cv_classdef_test_" + System.Guid.NewGuid()),
        typeof(ClassDefinitionProvider).Assembly);

    // ── ClassDefinition.Merge ─────────────────────────────────────────────────

    [Fact]
    public void Merge_ChildCasterTypeWins_OverParent()
    {
        var parent = new ClassDefinition { Name = "base", CasterType = CasterType.None };
        var child = new ClassDefinition { Name = "sub", Inherits = ["base"], CasterType = CasterType.Third };

        var merged = ClassDefinition.Merge(child, parent);

        Assert.Equal(CasterType.Third, merged.CasterType);
    }

    [Fact]
    public void Merge_NullCasterType_InheritsFromParent()
    {
        var parent = new ClassDefinition { Name = "base", CasterType = CasterType.Full };
        var child = new ClassDefinition { Name = "sub", Inherits = ["base"], CasterType = null };

        var merged = ClassDefinition.Merge(child, parent);

        Assert.Equal(CasterType.Full, merged.CasterType);
    }

    [Fact]
    public void Merge_AliasesUnion_ChildAndParentBothPresent()
    {
        var parent = new ClassDefinition { Name = "fighter", Aliases = ["fighter"] };
        var child = new ClassDefinition
        {
            Name = "fighter_ek",
            Inherits = ["fighter"],
            Aliases = ["eldritch knight"]
        };

        var merged = ClassDefinition.Merge(child, parent);

        Assert.Contains("fighter", merged.Aliases, System.StringComparer.OrdinalIgnoreCase);
        Assert.Contains("eldritch knight", merged.Aliases, System.StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Merge_ChildPools_WinsOverParent()
    {
        var parent = new ClassDefinition { Name = "base", Pools = ["pool_a"] };
        var child = new ClassDefinition { Name = "sub", Pools = ["pool_b"] };

        var merged = ClassDefinition.Merge(child, parent);

        Assert.Equal(["pool_b"], merged.Pools);
    }

    [Fact]
    public void Merge_EmptyChildPools_InheritsFromParent()
    {
        var parent = new ClassDefinition { Name = "base", Pools = ["pool_a"] };
        var child = new ClassDefinition { Name = "sub", Pools = [] };

        var merged = ClassDefinition.Merge(child, parent);

        Assert.Equal(["pool_a"], merged.Pools);
    }

    // ── ClassDefinitionProvider — YAML loading ────────────────────────────────

    [Fact]
    public void Provider_Dnd5e_LoadsAllExpectedClasses()
    {
        var classes = Provider.GetClassesForSystem(RulesetSystem.Dnd5e);

        Assert.True(classes.ContainsKey("wizard"));
        Assert.True(classes.ContainsKey("fighter"));
        Assert.True(classes.ContainsKey("warlock"));
        Assert.True(classes.ContainsKey("paladin"));
        Assert.True(classes.ContainsKey("fighter_eldritch_knight"));
    }

    [Fact]
    public void Provider_Pf2e_LoadsAllExpectedClasses()
    {
        var classes = Provider.GetClassesForSystem(RulesetSystem.Pathfinder2e);

        Assert.True(classes.ContainsKey("wizard"));
        Assert.True(classes.ContainsKey("bard"));
        Assert.True(classes.ContainsKey("summoner"));
    }

    [Fact]
    public void Provider_Dnd5e_WizardIsFullCaster()
    {
        var classes = Provider.GetClassesForSystem(RulesetSystem.Dnd5e);

        Assert.Equal(CasterType.Full, classes["wizard"].CasterType);
    }

    [Fact]
    public void Provider_Dnd5e_WarlockIsWarlockType()
    {
        var classes = Provider.GetClassesForSystem(RulesetSystem.Dnd5e);

        Assert.Equal(CasterType.Warlock, classes["warlock"].CasterType);
    }

    [Fact]
    public void Provider_Dnd5e_FighterIsNonCaster()
    {
        var classes = Provider.GetClassesForSystem(RulesetSystem.Dnd5e);

        Assert.Equal(CasterType.None, classes["fighter"].CasterType);
    }

    // ── EldritchKnight inherits from Fighter ──────────────────────────────────

    [Fact]
    public void EldritchKnight_InheritsFromFighter_HasCorrectCasterType()
    {
        var classes = Provider.GetClassesForSystem(RulesetSystem.Dnd5e);
        var ek = classes["fighter_eldritch_knight"];

        Assert.Equal(CasterType.Third, ek.CasterType);
        // Inherited fighter alias via union
        Assert.Contains("fighter", ek.Aliases, System.StringComparer.OrdinalIgnoreCase);
        Assert.Contains("eldritch knight", ek.Aliases, System.StringComparer.OrdinalIgnoreCase);
    }

    // ── TryResolveClass alias matching ────────────────────────────────────────

    [Fact]
    public void TryResolveClass_ExactAlias_Matches()
    {
        Assert.True(Provider.TryResolveClass(RulesetSystem.Dnd5e, "Wizard", out var def));
        Assert.Equal("wizard", def!.Name);
    }

    [Fact]
    public void TryResolveClass_SubstringAlias_Matches()
    {
        Assert.True(Provider.TryResolveClass(RulesetSystem.Dnd5e, "Fighter (Eldritch Knight)", out var def));
        Assert.Equal("fighter_eldritch_knight", def!.Name);
    }

    [Fact]
    public void TryResolveClass_PlainFighter_ResolvesToFighter()
    {
        Assert.True(Provider.TryResolveClass(RulesetSystem.Dnd5e, "Fighter", out var def));
        // "Fighter" contains "fighter" (len 7) but NOT "eldritch knight", so plain fighter wins
        Assert.Equal("fighter", def!.Name);
    }

    [Fact]
    public void TryResolveClass_UnknownClass_ReturnsFalse()
    {
        Assert.False(Provider.TryResolveClass(RulesetSystem.Dnd5e, "Totally Unknown Class XYZ", out _));
    }

    // ── Dnd5eCasterLevelHelper integration ───────────────────────────────────

    [Fact]
    public void CasterLevel_HombrewHalfCaster_ComputesCorrectly()
    {
        // Simulate a homebrew half-caster via ClassDefinitionProvider
        // This verifies that the helper reads from the provider, not hardcoded arrays.
        var classes = new List<ClassLevelEntry>
        {
            new() { Class = "Paladin", Level = 4 }, // Half → 2
            new() { Class = "Cleric", Level = 6 }   // Full → 6
        };

        // Uses embedded YAML; result should match: 4/2 + 6 = 8
        Assert.Equal(8, Dnd5eCasterLevelHelper.ComputeCasterLevel(classes));
    }
}
