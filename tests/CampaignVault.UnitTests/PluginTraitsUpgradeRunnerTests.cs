using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Plugins;
using CampaignVault.Rulesets;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

public class PluginTraitsUpgradeRunnerTests
{
    private static Character CharacterWithTraits(params (string Key, string Value)[] traits)
    {
        var character = new Character { Id = "chars/test", Name = "Test" };
        foreach (var (key, value) in traits)
        {
            character.SystemStats.Traits[key] = value;
        }
        return character;
    }

    [Fact]
    public void RenameUpgrader_MovesKeyAndReportsChanged()
    {
        var character = CharacterWithTraits(("crafting.tool_quality", "fine"));

        var changed = PluginTraitsUpgradeRunner.ApplyUpgrades(character, [new RenamingUpgrader()]);

        Assert.True(changed);
        Assert.False(character.SystemStats.Traits.ContainsKey("crafting.tool_quality"));
        Assert.Equal("fine", character.SystemStats.Traits["crafting.toolQuality"]);
    }

    [Fact]
    public void AlreadyCurrentDocument_ReportsNoChange()
    {
        var character = CharacterWithTraits(("crafting.toolQuality", "fine"));

        var changed = PluginTraitsUpgradeRunner.ApplyUpgrades(character, [new RenamingUpgrader()]);

        Assert.False(changed);
        Assert.Equal("fine", character.SystemStats.Traits["crafting.toolQuality"]);
    }

    [Fact]
    public void ThrowingUpgrader_IsSwallowed_AndOtherUpgradersStillRun()
    {
        var character = CharacterWithTraits(("crafting.tool_quality", "fine"), ("astral.rank", "novice"));

        var changed = PluginTraitsUpgradeRunner.ApplyUpgrades(
            character, [new ThrowingUpgrader(), new RenamingUpgrader()]);

        Assert.True(changed);
        Assert.Equal("fine", character.SystemStats.Traits["crafting.toolQuality"]);
    }

    [Fact]
    public void NoUpgraders_NoTraits_NoOp()
    {
        var character = new Character { Id = "chars/empty", Name = "Empty" };

        Assert.False(PluginTraitsUpgradeRunner.ApplyUpgrades(character, null));
        Assert.False(PluginTraitsUpgradeRunner.ApplyUpgrades(character, []));

        var withTraits = CharacterWithTraits(("astral.rank", "novice"));
        Assert.False(PluginTraitsUpgradeRunner.ApplyUpgrades(withTraits, []));
    }

    [Collection("RavenDB")]
    public class OrphanedPrefixTests : IClassFixture<RavenDBFixture>
    {
        private readonly IDocumentStore _store;

        public OrphanedPrefixTests(RavenDBFixture fixture)
        {
            _store = fixture.Store;
        }

        [Fact]
        public async Task UnclaimedPrefix_IsReported_ClaimedAndUnprefixedAreNot()
        {
            var campaign = "traits-orphan-" + Guid.NewGuid().ToString("N")[..8];
            var characterId = $"campaigns/{campaign}/chars/rook";

            using (var session = _store.OpenAsyncSession())
            {
                var character = new Character
                {
                    Id = characterId,
                    Name = "Rook",
                    CampaignName = campaign,
                };
                character.SystemStats.Traits["retired_mode.stage"] = "gathering";
                character.SystemStats.Traits["crafting.toolQuality"] = "fine";
                character.SystemStats.Traits["reputation"] = "trusted";
                await session.StoreAsync(character, characterId);
                await session.SaveChangesAsync();
            }

            var orphaned = await PluginTraitsUpgradeRunner.WarnOnOrphanedTraitPrefixesAsync(
                _store, claimedPrefixes: ["crafting"], logger: null);

            Assert.Contains("retired_mode", orphaned);
            Assert.DoesNotContain("crafting", orphaned);
            Assert.DoesNotContain("reputation", orphaned);
        }
    }

    private sealed class RenamingUpgrader : IPluginTraitsUpgrader
    {
        public string PluginId => "crafting";

        public bool TryUpgrade(IDictionary<string, string> traits)
        {
            if (traits.Remove("crafting.tool_quality", out var value))
            {
                traits["crafting.toolQuality"] = value;
                return true;
            }
            return false;
        }
    }

    private sealed class ThrowingUpgrader : IPluginTraitsUpgrader
    {
        public string PluginId => "astral";

        public bool TryUpgrade(IDictionary<string, string> traits) =>
            throw new InvalidOperationException("boom");
    }
}
