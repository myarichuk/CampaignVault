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
            derivedTypeAttrs.Select(attr => (string)attr.TypeDiscriminator ?? attr.DerivedType.Name)
        );

        // Check that every discriminator has a registry entry
        var registryEntries = CommitSchemaRegistry.GetAll();
        var discriminatorsFromRegistry = new HashSet<string>(
            registryEntries.Select(s => s.Type)
        );

        Assert.Equal(discriminatorsFromAttrs, discriminatorsFromRegistry);
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
}
