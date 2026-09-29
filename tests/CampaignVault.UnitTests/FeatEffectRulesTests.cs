using System;
using System.Collections.Generic;
using CampaignVault.Models;
using CampaignVault.Plugins;
using CampaignVault.Rulesets;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Validation and plugin/mode gating for declarative feat effects.</summary>
[Collection("PluginStatics")]
public class FeatEffectRulesTests
{
    [Fact]
    public void Validate_AcceptsAWellFormedEffect()
    {
        var ok = new FeatEffect { Kind = "attackBonus", Value = 1, Weapon = ["ranged"], Assert = ["allyNear"], When = "an ally is adjacent" };

        Assert.Empty(FeatEffectRules.Validate([ok]));
    }

    [Fact]
    public void Validate_RejectsUnknownKindZeroValueBadTypeBadWeaponAndMissingWhen()
    {
        var bad = new FeatEffect
        {
            Kind = "teleport", Value = 0, BonusType = "cosmic", Weapon = ["laser"], Assert = ["x"], Subject = "stealth",
        };

        var errors = string.Join(" | ", FeatEffectRules.Validate([bad]));

        Assert.Contains("unknown kind", errors);
        Assert.Contains("non-zero", errors);
        Assert.Contains("bonusType", errors);
        Assert.Contains("weapon condition", errors);
        Assert.Contains("'when'", errors);
        Assert.Contains("subject only applies", errors);
    }

    [Fact]
    public void Requires_PluginOnly_NeedsThePluginLoaded_AnyMode()
    {
        var id = "plug-" + Guid.NewGuid().ToString("N")[..6];
        var previous = PluginDataRoots.LoadedPluginIds;
        try
        {
            var req = new FeatRequirement { Plugin = id };
            Assert.False(FeatEffectRules.IsAvailable(req, []));

            PluginDataRoots.LoadedPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
            Assert.True(FeatEffectRules.IsAvailable(req, []));
            Assert.True(FeatEffectRules.IsAvailable(req, ["anything"]));
        }
        finally
        {
            PluginDataRoots.LoadedPluginIds = previous;
        }
    }

    [Fact]
    public void Requires_PluginAndMode_NeedsTheModeRunning_AndOwnedByThePlugin()
    {
        var id = "plug-" + Guid.NewGuid().ToString("N")[..6];
        var mode = "mode-" + Guid.NewGuid().ToString("N")[..6];
        var previousPlugins = PluginDataRoots.LoadedPluginIds;
        var previousOwners = PluginDataRoots.ModeOwners;
        try
        {
            PluginDataRoots.LoadedPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
            PluginDataRoots.ModeOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [mode] = id };

            var req = new FeatRequirement { Plugin = id, Mode = mode };
            Assert.False(FeatEffectRules.IsAvailable(req, []));
            Assert.True(FeatEffectRules.IsAvailable(req, [mode]));

            var wrongOwner = new FeatRequirement { Plugin = id, Mode = mode };
            PluginDataRoots.ModeOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [mode] = "someone-else" };
            Assert.False(FeatEffectRules.IsAvailable(wrongOwner, [mode]));
        }
        finally
        {
            PluginDataRoots.LoadedPluginIds = previousPlugins;
            PluginDataRoots.ModeOwners = previousOwners;
        }
    }

    [Fact]
    public void Requires_ModeAlone_OnlyNeedsTheModeRunning()
    {
        var mode = "mode-" + Guid.NewGuid().ToString("N")[..6];
        var req = new FeatRequirement { Mode = mode };

        Assert.False(FeatEffectRules.IsAvailable(req, []));
        Assert.True(FeatEffectRules.IsAvailable(req, [mode]));
    }

    [Fact]
    public void NoRequirement_IsAlwaysAvailable() => Assert.True(FeatEffectRules.IsAvailable(null, []));

    [Fact]
    public void YamlFeat_WithNestedEffectsAndRequires_LoadsThroughTheProvider()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cv_feat_yaml_" + Guid.NewGuid());
        var feats = System.IO.Path.Combine(dir, "dnd5e", "feats");
        System.IO.Directory.CreateDirectory(feats);
        System.IO.File.WriteAllText(System.IO.Path.Combine(feats, "long_shot.yaml"), """
            name: long_shot
            system: dnd5e
            requires:
              plugin: some-plugin
              mode: some-mode
            effects:
              - kind: attackBonus
                value: -5
                toggle: powerAttack
                weapon: [ranged]
              - kind: damageBonus
                value: 3
                bonusType: circumstance
                assert: [highGround]
                when: shooting from higher ground
            """);

        var provider = new CampaignVault.Services.FeatDefinitionProvider(dir, typeof(CampaignVault.Services.FeatDefinitionProvider).Assembly);

        Assert.True(provider.TryGet("dnd5e", "long_shot", out var feat));
        Assert.Equal("some-plugin", feat!.Requires!.Plugin);
        Assert.Equal("some-mode", feat.Requires.Mode);
        Assert.Equal(2, feat.Effects.Count);
        Assert.Equal("powerAttack", feat.Effects[0].Toggle);
        Assert.Equal(["ranged"], feat.Effects[0].Weapon);
        Assert.Equal(["highGround"], feat.Effects[1].Assert);
        Assert.Empty(FeatEffectRules.Validate(feat.Effects));
    }

    [Fact]
    public void Handbook_HidesFeatsGatedOnAMissingPluginOrStoppedMode()
    {
        var mode = "mode-" + Guid.NewGuid().ToString("N")[..6];
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cv_hb_" + Guid.NewGuid());
        var homebrew = new List<CustomFeat>
        {
            new() { Id = "feats/a", Name = "Open Feat", System = "dnd5e" },
            new() { Id = "feats/b", Name = "Mode Feat", System = "dnd5e", Requires = new FeatRequirement { Mode = mode } },
            new() { Id = "feats/c", Name = "Plugin Feat", System = "dnd5e", Requires = new FeatRequirement { Plugin = "nope-" + mode } },
        };

        SystemHandbookResponse Build(IReadOnlyCollection<string>? modes) => CampaignVault.Services.SystemHandbookBuilder.Build(
            "dnd5e",
            new CampaignVault.Services.ClassDefinitionProvider(dir, typeof(CampaignVault.Services.FeatDefinitionProvider).Assembly),
            new CampaignVault.Services.RaceDefinitionProvider(dir, typeof(CampaignVault.Services.FeatDefinitionProvider).Assembly),
            new CampaignVault.Services.BackgroundDefinitionProvider(dir, typeof(CampaignVault.Services.FeatDefinitionProvider).Assembly),
            new CampaignVault.Services.FeatDefinitionProvider(dir, typeof(CampaignVault.Services.FeatDefinitionProvider).Assembly),
            new CampaignVault.Services.ConditionDefinitionProvider(dir, typeof(CampaignVault.Services.FeatDefinitionProvider).Assembly),
            null, homebrew, modes);

        var stopped = Build([]);
        Assert.Contains("Open Feat", stopped.Feats);
        Assert.DoesNotContain("Mode Feat", stopped.Feats);
        Assert.DoesNotContain("Plugin Feat", stopped.Feats);

        Assert.Contains("Mode Feat", Build([mode]).Feats);
    }
}
