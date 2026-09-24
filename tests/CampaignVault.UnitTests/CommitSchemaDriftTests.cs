using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using CampaignVault.Models;
using CampaignVault.Schema;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Guards against metadata drift: ensures WorldChange variants, CommitSchemaRegistry, and CommitSchemaModel stay synchronized.
/// These tests prevent the 35-vs-40 gap that existed when variants went undocumented.
/// </summary>
public class CommitSchemaDriftTests
{
    [Fact]
    public void EveryDerivedType_HasARegistryEntry()
    {
        // Ground truth: [JsonDerivedType] discriminators on WorldChange
        var derivedTypeAttrs = typeof(WorldChange)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .ToList();

        var discriminatorsFromAttrs = new HashSet<string>(
            derivedTypeAttrs.Select(attr => (string?)attr.TypeDiscriminator ?? attr.DerivedType.Name)
        );

        // Check that every discriminator has a registry entry
        var registryEntries = CommitSchemaRegistry.GetAll();
        var discriminatorsFromRegistry = new HashSet<string>(
            registryEntries.Select(s => s.Type)
        );

        // Plugin $types may extend the live schema beyond core [JsonDerivedType] attributes.
        // Drift guard: every core discriminator must still be present.
        // Engine-only verbs are deliberately hidden from the model-facing registry.
        var engineOnly = CommitSchemaModel.Variants.Where(v => v.IsEngineOnly).Select(v => v.Discriminator);
        var missing = discriminatorsFromAttrs.Except(discriminatorsFromRegistry).Except(engineOnly).OrderBy(x => x).ToList();
        Assert.True(missing.Count == 0,
            "Core discriminators missing from CommitSchemaRegistry: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryVariant_HasCategoryAndSummary()
    {
        var variants = CommitSchemaModel.Variants;

        foreach (var variant in variants)
        {
            Assert.False(string.IsNullOrWhiteSpace(variant.Category),
                $"Variant '{variant.Discriminator}' has no [CommitCategory]");
            Assert.False(string.IsNullOrWhiteSpace(variant.Summary),
                $"Variant '{variant.Discriminator}' has no [Description] on the class");
        }
    }

    [Fact]
    public void DeclaredSideEffects_MatchRegisteredHandlers()
    {
        var variants = CommitSchemaModel.Variants;
        var validDiscriminators = new HashSet<string>(
            variants.Select(v => v.Discriminator)
        );

        foreach (var variant in variants)
        {
            foreach (var sideEffect in variant.SideEffects)
            {
                Assert.True(validDiscriminators.Contains(sideEffect),
                    $"Variant '{variant.Discriminator}' declares side effect '{sideEffect}' which is not a registered discriminator");
            }
        }
    }

    [Fact]
    public void HotTier_IsExactlyTen()
    {
        var hotTierVariants = CommitSchemaModel.Variants.Where(v => v.IsHotTier).ToList();

        Assert.Equal(10, hotTierVariants.Count);

        var expectedHotTier = new[] { "hp", "status", "event", "relationship", "mood", "activity", "item", "ruleset_action", "engagement_relation", "travel" };
        var actualHotTier = hotTierVariants.Select(v => v.Discriminator).OrderBy(x => x).ToList();

        Assert.Equal(
            expectedHotTier.OrderBy(x => x),
            actualHotTier
        );
    }

    [Fact]
    public void SchemaModel_DerivesAllVariantsFromReflection()
    {
        var variants = CommitSchemaModel.Variants;

        // Should have at least 40 variants (35 in registry + 5 new ones)
        Assert.True(variants.Count >= 40, $"Expected at least 40 variants, got {variants.Count}");

        // Each variant should have at least a discriminator and category
        foreach (var variant in variants)
        {
            Assert.False(string.IsNullOrWhiteSpace(variant.Discriminator));
            Assert.False(string.IsNullOrWhiteSpace(variant.Category));
        }
    }

    [Fact]
    public void EngineOnlyVerbs_AreHiddenFromIndexAndLookup()
    {
        var engineOnly = new[] { "rest_recovery_ack", "item_persistence_surfaced", "memory_decay", "ambient_encounter_check" };
        var index = CommitSchemaRegistry.GetIndex().Select(s => s.Type).ToHashSet();

        foreach (var type in engineOnly)
        {
            Assert.True(CommitSchemaModel.Find(type)?.IsEngineOnly, $"{type} should be [EngineOnly]");
            Assert.DoesNotContain(type, index);
            Assert.Empty(CommitSchemaRegistry.GetAll(type: type));
        }
    }

    [Fact]
    public void Index_OmitsModeScopedPluginVerbs_ButLookupResolvesThem()
    {
        foreach (var variant in CommitSchemaModel.Variants.Where(v => v.ModeId is not null))
        {
            Assert.DoesNotContain(variant.Discriminator, CommitSchemaRegistry.GetIndex().Select(s => s.Type));
            Assert.Single(CommitSchemaRegistry.GetAll(type: variant.Discriminator));
        }
    }

    [Theory]
    [InlineData("Short one.", "Short one.")]
    [InlineData("First sentence here. Second sentence follows.", "First sentence here.")]
    public void ClipSummary_KeepsFirstSentence(string input, string expected) =>
        Assert.Equal(expected, CommitSchemaRegistry.ClipSummary(input));

    [Fact]
    public void ClipSummary_CapsLongSummaries()
    {
        var clipped = CommitSchemaRegistry.ClipSummary(new string('a', 30) + " " + new string('b', 60));
        Assert.True(clipped.Length <= 62, clipped);
        Assert.EndsWith("…", clipped);
    }
}
